using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using System.Windows;
using System.Windows.Threading;
using MessageBox = System.Windows.MessageBox;
using AAFSerialCaptureApp.Controls;
using AAFSerialCaptureApp.Models;
using AAFSerialCaptureApp.Services;

namespace AAFSerialCaptureApp;

public partial class MainWindow : Window
{
    private const string DefaultRegexPattern = @"(?<name>[A-Za-z]\w*)\s*=\s*\[?\s*(?<value>-?\d+)\s*\]?";
    private const string DefaultLabelsText = "S=Saída medida (ADC); U=Ação de controle (P)";

    // Matches named numeric channels like "A=[ 74]" or "S=2124, U=-526". User-editable via SettingsWindow.
    private Regex _valueRegex = new(DefaultRegexPattern, RegexOptions.Compiled);
    private string _regexPatternText = DefaultRegexPattern;
    private string _labelsText = DefaultLabelsText;
    private bool _smoothingEnabled = true;

    private readonly SerialPortService _serial = new();
    private readonly ObservableCollection<CaptureSession> _captures = new();
    private readonly Dictionary<CaptureSession, RealTimeChart> _charts = new();
    private readonly Dictionary<CaptureSession, System.Windows.Controls.TextBox> _rawLogBoxes = new();
    private readonly Dictionary<CaptureSession, int> _rawLogRenderedCount = new();
    private readonly DispatcherTimer _renderTimer;

    // Unbounded queue: the serial read thread only enqueues (near-instant), a separate
    // worker thread does the actual parsing + disk write, so neither UI nor disk latency
    // can ever slow down / drop incoming serial data.
    private readonly Channel<(CaptureSession Session, string Line)> _lineQueue = Channel.CreateUnbounded<(CaptureSession, string)>();

    private volatile CaptureSession? _activeCapture;
    private int _linesTotal;

    public MainWindow()
    {
        InitializeComponent();

        CapturesTabControl.ItemsSource = _captures;

        BaudCombo.ItemsSource = new[] { 300, 1200, 2400, 4800, 9600, 19200, 38400, 57600, 115200, 230400, 460800, 921600 };
        BaudCombo.SelectedItem = 115200;

        RefreshPorts();

        _serial.LineReceived += Serial_LineReceived;
        _serial.ErrorOccurred += (_, msg) => Dispatcher.Invoke(() => StatusText.Text = $"Erro na porta: {msg}");
        _ = Task.Run(ConsumeQueuedLinesAsync);

        _renderTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _renderTimer.Tick += (_, _) =>
        {
            try
            {
                if (LiveChartCheckBox.IsChecked == true) RenderActiveChart();
                RenderRawLog();
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Erro ao atualizar gráfico: {ex.Message}";
            }
        };
        _renderTimer.Start();

        FolderTextBox.Text = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Capturas");
        ApplyLabelsText(DefaultLabelsText);
        RefreshFilesList();
    }

    private void RefreshPorts()
    {
        var current = PortCombo.SelectedItem as string;
        var ports = SerialPortService.GetPortNames();
        PortCombo.ItemsSource = ports;
        if (current != null && ports.Contains(current))
            PortCombo.SelectedItem = current;
        else if (ports.Length > 0)
            PortCombo.SelectedIndex = 0;
    }

    private void RefreshPortsButton_Click(object sender, RoutedEventArgs e) => RefreshPorts();

    private void LiveChartCheckBox_Checked(object sender, RoutedEventArgs e) => RenderActiveChart();

    private void SmoothingCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        _smoothingEnabled = SmoothingCheckBox.IsChecked == true;
        RenderActiveChart();
    }

    private void WindowSizeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        var points = (int)e.NewValue;
        RealTimeChart.MaxVisiblePoints = points;
        if (WindowSizeText != null) WindowSizeText.Text = $"{points} amostras";
        RenderActiveChart();
    }

    private void OpenSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        var previewSession = _activeCapture ?? (CapturesTabControl.SelectedItem as CaptureSession) ?? _captures.FirstOrDefault();
        var previewText = previewSession?.GetRecentLinesText() ?? "";

        var settings = new SettingsWindow(_regexPatternText, _labelsText, previewText)
        {
            Owner = this
        };
        settings.RegexApplied += pattern =>
        {
            try
            {
                _valueRegex = new Regex(pattern, RegexOptions.Compiled);
                _regexPatternText = pattern;
                StatusText.Text = "Padrão de captura atualizado.";
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Regex inválida: {ex.Message}", "Erro", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        };
        settings.LabelsApplied += text =>
        {
            _labelsText = text;
            ApplyLabelsText(text);
            StatusText.Text = "Rótulos atualizados.";
            RenderActiveChart();
        };
        settings.Show();
    }

    private bool TryGetSelectedBaudRate(out int baud)
    {
        // IsEditable lets the user type a custom rate not present in the preset list.
        var text = BaudCombo.Text;
        if (int.TryParse(text, out baud) && baud > 0) return true;
        if (BaudCombo.SelectedItem is int selected)
        {
            baud = selected;
            return true;
        }
        baud = 0;
        return false;
    }

    private void ConnectButton_Click(object sender, RoutedEventArgs e)
    {
        if (PortCombo.SelectedItem is not string port)
        {
            MessageBox.Show(this, "Selecione uma porta serial.", "Aviso", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (!TryGetSelectedBaudRate(out var baud))
        {
            MessageBox.Show(this, "Baud rate inválido.", "Aviso", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            _serial.Connect(port, baud);
            ConnectionStatusText.Text = $"Conectado ({port} @ {baud})";
            ConnectionStatusText.Foreground = System.Windows.Media.Brushes.Green;
            ConnectButton.IsEnabled = false;
            DisconnectButton.IsEnabled = true;
            NewCaptureButton.IsEnabled = true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Falha ao conectar: {ex.Message}", "Erro", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void DisconnectButton_Click(object sender, RoutedEventArgs e)
    {
        StopCapture();
        _serial.Disconnect();
        ConnectionStatusText.Text = "Desconectado";
        ConnectionStatusText.Foreground = System.Windows.Media.Brushes.Red;
        ConnectButton.IsEnabled = true;
        DisconnectButton.IsEnabled = false;
        NewCaptureButton.IsEnabled = false;
    }

    private void BrowseFolderButton_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "Selecione a pasta onde os arquivos .log serão salvos",
            SelectedPath = Directory.Exists(FolderTextBox.Text) ? FolderTextBox.Text : AppDomain.CurrentDomain.BaseDirectory
        };
        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            FolderTextBox.Text = dialog.SelectedPath;
            RefreshFilesList();
        }
    }

    /// <summary>Parses "S=descrição; U=descrição" into channel-label overrides (used live and when loading files).</summary>
    private static void ApplyLabelsText(string text)
    {
        var overrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in text.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = entry.Split('=', 2);
            if (parts.Length == 2)
            {
                overrides[parts[0].Trim()] = parts[1].Trim();
            }
        }
        ChannelLabels.SetOverrides(overrides);
    }

    private void RefreshFilesButton_Click(object sender, RoutedEventArgs e) => RefreshFilesList();

    private void RefreshFilesList()
    {
        var folder = FolderTextBox.Text;
        CaptureFilesListBox.ItemsSource = Directory.Exists(folder)
            ? Directory.EnumerateFiles(folder, "*.log").Select(Path.GetFileName).OrderBy(n => n).ToList()
            : Array.Empty<string>();
        CaptureBlocksListBox.ItemsSource = null;
    }

    private void CaptureFilesListBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (CaptureFilesListBox.SelectedItem is not string fileName)
        {
            CaptureBlocksListBox.ItemsSource = null;
            return;
        }
        var filePath = Path.Combine(FolderTextBox.Text, fileName);
        try
        {
            CaptureBlocksListBox.ItemsSource = LogFileBlocks.Scan(filePath);
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Não foi possível ler blocos do arquivo: {ex.Message}";
            CaptureBlocksListBox.ItemsSource = null;
        }
    }

    private void DeleteFileButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string fileName }) return;
        var filePath = Path.Combine(FolderTextBox.Text, fileName);

        if (_activeCapture != null && string.Equals(_activeCapture.FilePath, filePath, StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show(this, "Pare a captura ativa antes de excluir este arquivo.", "Aviso", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var confirm = MessageBox.Show(this, $"Excluir permanentemente o arquivo \"{fileName}\"?", "Confirmar exclusão",
            MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;

        try
        {
            File.Delete(filePath);
            RefreshFilesList();
            StatusText.Text = $"Arquivo {fileName} excluído.";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Não foi possível excluir o arquivo: {ex.Message}", "Erro", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void DeleteBlockButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: CaptureBlockInfo block }) return;
        if (CaptureFilesListBox.SelectedItem is not string fileName) return;
        var filePath = Path.Combine(FolderTextBox.Text, fileName);

        if (_activeCapture != null && string.Equals(_activeCapture.FilePath, filePath, StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show(this, "Pare a captura ativa antes de excluir um bloco deste arquivo.", "Aviso", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var confirm = MessageBox.Show(this, $"Excluir o bloco \"{block.Label}\" ({block.LineCount} linhas) de \"{fileName}\"?",
            "Confirmar exclusão", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;

        try
        {
            LogFileBlocks.DeleteBlock(filePath, block);
            CaptureBlocksListBox.ItemsSource = LogFileBlocks.Scan(filePath);
            StatusText.Text = $"Bloco {block.Label} excluído de {fileName}.";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Não foi possível excluir o bloco: {ex.Message}", "Erro", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void LoadFileButton_Click(object sender, RoutedEventArgs e) => LoadSelectedFile();

    private void CaptureFilesListBox_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e) => LoadSelectedFile();

    private void LoadSelectedFile()
    {
        if (CaptureFilesListBox.SelectedItem is not string fileName) return;
        var filePath = Path.Combine(FolderTextBox.Text, fileName);

        var number = _captures.Count == 0 ? 1 : _captures.Max(c => c.Number) + 1;
        try
        {
            var session = CaptureSession.LoadFromFile(number, filePath, _valueRegex);
            _captures.Add(session);
            SelectTab(session);
            StatusText.Text = $"Carregado {fileName}: {session.LineCount} linhas.";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Não foi possível carregar o arquivo: {ex.Message}", "Erro", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void NewCaptureButton_Click(object sender, RoutedEventArgs e)
    {
        var prefix = string.IsNullOrWhiteSpace(PrefixTextBox.Text) ? "captura" : PrefixTextBox.Text.Trim();
        var folder = FolderTextBox.Text;

        try
        {
            Directory.CreateDirectory(folder);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Não foi possível criar a pasta: {ex.Message}", "Erro", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        var number = GetNextCaptureNumber(folder, prefix);
        var filePath = Path.Combine(folder, $"{prefix}_{number}.log");

        // Only one capture is actively fed by the serial port at a time.
        if (_activeCapture != null)
            _activeCapture.IsActive = false;

        CaptureSession session;
        try
        {
            session = new CaptureSession(number, filePath) { IsActive = true };
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Não foi possível criar o arquivo de log: {ex.Message}", "Erro", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        _captures.Add(session);
        _activeCapture = session;
        SelectTab(session);

        session.WriteRawLine($"---Nova Captura {number}---");
        session.MarkBoundary(number);
        StatusText.Text = $"Gravando em {filePath}";
        RefreshFilesList();
    }

    private void StopCapture()
    {
        if (_activeCapture == null) return;
        _activeCapture.WriteRawLine($"---Fim Captura {_activeCapture.Number}---");
        _activeCapture.IsActive = false;
        _activeCapture.Close();
        _activeCapture = null;
        StatusText.Text = "Captura parada.";
    }

    private void StopCaptureTabButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: CaptureSession session }) return;
        if (session != _activeCapture)
        {
            MessageBox.Show(this, "Esta captura já não está ativa.", "Aviso", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        StopCapture();
    }

    private static int GetNextCaptureNumber(string folder, string prefix)
    {
        var regex = new Regex($@"^{Regex.Escape(prefix)}_(\d+)\.log$", RegexOptions.IgnoreCase);
        var max = 0;
        foreach (var file in Directory.EnumerateFiles(folder))
        {
            var match = regex.Match(Path.GetFileName(file));
            if (match.Success && int.TryParse(match.Groups[1].Value, out var n) && n > max)
                max = n;
        }
        return max + 1;
    }

    /// <summary>Selects the given tab; deferred so it still works right after the tab's container is created.</summary>
    private void SelectTab(CaptureSession session)
    {
        CapturesTabControl.SelectedItem = session;
        Dispatcher.BeginInvoke(() => CapturesTabControl.SelectedItem = session, DispatcherPriority.Background);
    }

    private void Serial_LineReceived(object? sender, string line)
    {
        // Runs on the SerialPort's own read thread: just hand off and return immediately,
        // never touch disk/locks/UI here so serial reception is never the bottleneck.
        var capture = _activeCapture;
        if (capture == null) return;
        _lineQueue.Writer.TryWrite((capture, line));
    }

    /// <summary>Dedicated background worker: parses and writes each queued line, decoupled from serial I/O and UI.</summary>
    private async Task ConsumeQueuedLinesAsync()
    {
        await foreach (var (capture, line) in _lineQueue.Reader.ReadAllAsync())
        {
            try
            {
                capture.WriteRawLine(line);

                foreach (Match match in _valueRegex.Matches(line))
                {
                    if (double.TryParse(match.Groups["value"].Value, out var value))
                    {
                        capture.AddValue(match.Groups["name"].Value, value);
                    }
                }

                _linesTotal++;
                if (_linesTotal % 5 == 0)
                {
                    var total = _linesTotal;
                    _ = Dispatcher.BeginInvoke(() => StatusText.Text = $"Linhas recebidas: {total}");
                }
            }
            catch (Exception ex)
            {
                var message = ex.Message;
                _ = Dispatcher.BeginInvoke(() => StatusText.Text = $"Erro ao processar linha: {message}");
            }
        }
    }

    private void RenderActiveChart()
    {
        if (_activeCapture == null) return;
        if (!_charts.TryGetValue(_activeCapture, out var chart)) return;
        chart.Render(_activeCapture.GetChannelSnapshots(RealTimeChart.MaxVisiblePoints), _smoothingEnabled,
            _activeCapture.GetChannelTotalCounts(), _activeCapture.GetBoundaries());
    }

    private void RenderRawLog()
    {
        foreach (var (session, box) in _rawLogBoxes)
        {
            _rawLogRenderedCount.TryGetValue(session, out var lastCount);
            if (session.LineCount == lastCount) continue;

            box.Text = session.GetRecentLinesText();
            box.ScrollToEnd();
            _rawLogRenderedCount[session] = session.LineCount;
        }
    }

    private void Chart_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is RealTimeChart chart && chart.DataContext is CaptureSession session)
            BindChart(chart, null, session);
    }

    private void Chart_Unloaded(object sender, RoutedEventArgs e)
    {
        if (sender is RealTimeChart chart && chart.DataContext is CaptureSession session)
            _charts.Remove(session);
    }

    // WPF reuses the same RealTimeChart instance across tabs when they share the same
    // DataTemplate/data type, just swapping DataContext, so Loaded never fires again on
    // tab switch — this is what actually keeps the chart showing the previous tab's data.
    private void Chart_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is RealTimeChart chart)
            BindChart(chart, e.OldValue as CaptureSession, e.NewValue as CaptureSession);
    }

    private void BindChart(RealTimeChart chart, CaptureSession? oldSession, CaptureSession? newSession)
    {
        if (oldSession != null) _charts.Remove(oldSession);
        if (newSession == null) return;

        _charts[newSession] = chart;
        chart.Clear(); // drop any zoom/point state left over from whichever session previously used this instance
        chart.Render(newSession.GetChannelSnapshots(RealTimeChart.MaxVisiblePoints), _smoothingEnabled,
            newSession.GetChannelTotalCounts(), newSession.GetBoundaries());
    }

    private void RawLogTextBox_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.TextBox box && box.DataContext is CaptureSession session)
            BindRawLogBox(box, null, session);
    }

    private void RawLogTextBox_Unloaded(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.TextBox box && box.DataContext is CaptureSession session)
        {
            _rawLogBoxes.Remove(session);
            _rawLogRenderedCount.Remove(session);
        }
    }

    private void RawLogTextBox_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is System.Windows.Controls.TextBox box)
            BindRawLogBox(box, e.OldValue as CaptureSession, e.NewValue as CaptureSession);
    }

    private void BindRawLogBox(System.Windows.Controls.TextBox box, CaptureSession? oldSession, CaptureSession? newSession)
    {
        if (oldSession != null)
        {
            _rawLogBoxes.Remove(oldSession);
            _rawLogRenderedCount.Remove(oldSession);
        }
        if (newSession == null) return;

        _rawLogBoxes[newSession] = box;
        box.Text = newSession.GetRecentLinesText();
        box.ScrollToEnd();
        _rawLogRenderedCount[newSession] = newSession.LineCount;
    }

    private void ContinueCaptureButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: CaptureSession session }) return;
        if (!_serial.IsOpen)
        {
            MessageBox.Show(this, "Conecte a porta serial antes de continuar a captura.", "Aviso", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (_activeCapture != null && _activeCapture != session)
        {
            _activeCapture.IsActive = false;
            _activeCapture.Close();
        }

        session.Reopen();
        session.IsActive = true;
        _activeCapture = session;
        session.WriteRawLine($"---Continuando Captura {session.Number}---");
        session.MarkBoundary(session.Number);
        StatusText.Text = $"Continuando gravação em {session.FilePath}";
    }

    private void CloseTabButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: CaptureSession session }) return;

        if (_activeCapture == session)
        {
            _activeCapture = null;
        }
        session.IsActive = false;
        session.Close();
        _charts.Remove(session);
        _rawLogBoxes.Remove(session);
        _rawLogRenderedCount.Remove(session);
        _captures.Remove(session);
    }

    private void OpenLogButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: CaptureSession session }) return;
        try
        {
            Process.Start(new ProcessStartInfo(session.FilePath) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Não foi possível abrir o arquivo: {ex.Message}", "Erro", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _renderTimer.Stop();
        _serial.Disconnect();
        _lineQueue.Writer.TryComplete();
        foreach (var capture in _captures)
            capture.Close();
        base.OnClosed(e);
    }
}
