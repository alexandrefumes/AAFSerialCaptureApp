using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;

namespace AAFSerialCaptureApp.Models;

/// <summary>Friendly descriptions for known firmware channel labels (e.g. "S", "U"). User-editable at runtime.</summary>
public static class ChannelLabels
{
    private static readonly Dictionary<string, string> Defaults = new(StringComparer.OrdinalIgnoreCase)
    {
        ["S"] = "Saída medida (ADC)",
        ["U"] = "Ação de controle (P)"
    };

    private static Dictionary<string, string> _overrides = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Replaces the current label overrides (applies to both live capture and loaded files).</summary>
    public static void SetOverrides(Dictionary<string, string> overrides) => _overrides = overrides;

    public static string Describe(string channelName)
    {
        if (_overrides.TryGetValue(channelName, out var custom)) return $"{channelName} - {custom}";
        return Defaults.TryGetValue(channelName, out var desc) ? $"{channelName} - {desc}" : channelName;
    }
}

/// <summary>One numeric channel found in the stream (e.g. "S", "U", "A").</summary>
public class ChannelData
{
    private readonly List<double> _values = new();

    public double Last { get; private set; }
    public double Min { get; private set; } = double.NaN;
    public double Max { get; private set; } = double.NaN;
    public int Count => _values.Count;

    public void Add(double value)
    {
        _values.Add(value);
        Last = value;
        if (double.IsNaN(Min) || value < Min) Min = value;
        if (double.IsNaN(Max) || value > Max) Max = value;
    }

    /// <summary>Snapshot of only the most recent <paramref name="maxPoints"/> samples (avoids copying full history).</summary>
    public double[] Snapshot(int maxPoints)
    {
        int start = Math.Max(0, _values.Count - maxPoints);
        return _values.GetRange(start, _values.Count - start).ToArray();
    }
}

/// <summary>Represents a single capture (one log file + one or more named numeric channels, e.g. S/U).</summary>
public class CaptureSession : INotifyPropertyChanged, IDisposable
{
    private const int MaxRawLinesKept = 500;

    private readonly object _lock = new();
    private readonly Dictionary<string, ChannelData> _channels = new();
    private readonly List<string> _recentLines = new();
    private readonly List<(int Number, int Index)> _boundaries = new();
    private StreamWriter? _writer;

    public int Number { get; }
    public string FilePath { get; }
    public bool IsLoadedFromFile { get; private init; }
    public string Title => IsLoadedFromFile ? $"[Carregado] {Path.GetFileNameWithoutExtension(FilePath)}" : $"Captura {Number}";

    private int _lineCount;
    public int LineCount
    {
        get => _lineCount;
        private set => SetField(ref _lineCount, value);
    }

    private string _channelsSummary = "";
    public string ChannelsSummary
    {
        get => _channelsSummary;
        private set => SetField(ref _channelsSummary, value);
    }

    private bool _isActive;
    public bool IsActive
    {
        get => _isActive;
        set => SetField(ref _isActive, value);
    }

    public CaptureSession(int number, string filePath)
    {
        Number = number;
        FilePath = filePath;
        _writer = new StreamWriter(new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.Read))
        {
            AutoFlush = true
        };
    }

    private CaptureSession(int number, string filePath, bool loaded)
    {
        Number = number;
        FilePath = filePath;
        IsLoadedFromFile = loaded;
    }

    /// <summary>Reads an existing .log file into memory (no writer opened) so it can be viewed/charted
    /// without touching the file; use <see cref="Reopen"/> afterwards to resume appending to it.</summary>
    public static CaptureSession LoadFromFile(int number, string filePath, System.Text.RegularExpressions.Regex valueRegex)
    {
        var session = new CaptureSession(number, filePath, loaded: true);
        foreach (var line in File.ReadLines(filePath))
        {
            session.IngestLine(line, valueRegex);
        }
        return session;
    }

    /// <summary>Reopens the log file in append mode so this same capture can keep receiving data.</summary>
    public void Reopen()
    {
        lock (_lock)
        {
            if (_writer != null) return;
            _writer = new StreamWriter(new FileStream(FilePath, FileMode.Append, FileAccess.Write, FileShare.Read))
            {
                AutoFlush = true
            };
        }
    }

    /// <summary>Appends a raw line to the .log file, exactly like PuTTY session logging.</summary>
    public void WriteRawLine(string line)
    {
        lock (_lock)
        {
            _writer?.WriteLine(line);
            _recentLines.Add(line);
            if (_recentLines.Count > MaxRawLinesKept)
                _recentLines.RemoveAt(0);
        }
        LineCount++;
    }

    /// <summary>Joined text of the most recently received raw lines, for the live log view.</summary>
    public string GetRecentLinesText()
    {
        lock (_lock)
        {
            return string.Join(Environment.NewLine, _recentLines);
        }
    }

    /// <summary>Adds a line read from an existing file (no disk write): updates the log preview + parses channel values.</summary>
    public void IngestLine(string line, System.Text.RegularExpressions.Regex valueRegex)
    {
        lock (_lock)
        {
            _recentLines.Add(line);
            if (_recentLines.Count > MaxRawLinesKept)
                _recentLines.RemoveAt(0);
        }
        LineCount++;

        foreach (System.Text.RegularExpressions.Match match in valueRegex.Matches(line))
        {
            if (double.TryParse(match.Groups["value"].Value, out var value))
            {
                AddValue(match.Groups["name"].Value, value);
            }
        }
    }

    /// <summary>Adds a sample for a named channel (e.g. "S", "U", "A").</summary>
    public void AddValue(string channelName, double value)
    {
        lock (_lock)
        {
            if (!_channels.TryGetValue(channelName, out var channel))
            {
                channel = new ChannelData();
                _channels[channelName] = channel;
            }
            channel.Add(value);
        }
        UpdateSummary();
    }

    private void UpdateSummary()
    {
        var sb = new StringBuilder();
        lock (_lock)
        {
            foreach (var pair in _channels.OrderBy(kv => kv.Key))
            {
                if (sb.Length > 0) sb.Append("   ");
                sb.Append($"{ChannelLabels.Describe(pair.Key)}: últ={pair.Value.Last:0.##} mín={pair.Value.Min:0.##} máx={pair.Value.Max:0.##} n={pair.Value.Count}");
            }
        }
        ChannelsSummary = sb.ToString();
    }

    /// <summary>Snapshot of every channel's values, keyed by channel name, for chart rendering.</summary>
    public Dictionary<string, double[]> GetChannelSnapshots(int maxPoints = int.MaxValue)
    {
        lock (_lock)
        {
            return _channels.ToDictionary(kv => kv.Key, kv => kv.Value.Snapshot(maxPoints));
        }
    }

    /// <summary>Total (unwindowed) sample count per channel, used to map boundary markers to absolute positions.</summary>
    public Dictionary<string, int> GetChannelTotalCounts()
    {
        lock (_lock)
        {
            return _channels.ToDictionary(kv => kv.Key, kv => kv.Value.Count);
        }
    }

    /// <summary>Records a capture-boundary marker (e.g. right after writing "---Nova Captura N---") at the
    /// current sample position, so the chart can draw a divider line where a new capture block starts.</summary>
    public void MarkBoundary(int number)
    {
        lock (_lock)
        {
            int index = _channels.Count == 0 ? 0 : _channels.Values.Max(c => c.Count);
            _boundaries.Add((number, index));
        }
    }

    public List<(int Number, int Index)> GetBoundaries()
    {
        lock (_lock)
        {
            return new List<(int, int)>(_boundaries);
        }
    }

    public void Close()
    {
        lock (_lock)
        {
            _writer?.Flush();
            _writer?.Dispose();
            _writer = null;
        }
    }

    public void Dispose() => Close();

    public event PropertyChangedEventHandler? PropertyChanged;

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

/// <summary>One "---Nova/Continuando Captura N---" delimited block within a raw .log file.</summary>
public class CaptureBlockInfo
{
    public int Number { get; init; }
    public string Label { get; init; } = "";
    public int StartLine { get; init; }
    public int EndLine { get; init; }
    public int LineCount => EndLine - StartLine;
}

/// <summary>Scans/edits raw .log files for the "---...Captura N---" marker lines written by MainWindow.</summary>
public static class LogFileBlocks
{
    private static readonly System.Text.RegularExpressions.Regex StartMarker =
        new(@"^---(?:Nova|Continuando) Captura (\d+)---$", System.Text.RegularExpressions.RegexOptions.Compiled);

    public static List<CaptureBlockInfo> Scan(string filePath)
    {
        var lines = File.ReadAllLines(filePath);
        var blocks = new List<CaptureBlockInfo>();
        int currentStart = 0;
        int currentNumber = 0;
        bool any = false;

        for (int i = 0; i < lines.Length; i++)
        {
            var match = StartMarker.Match(lines[i]);
            if (!match.Success) continue;

            if (any)
            {
                blocks.Add(new CaptureBlockInfo { Number = currentNumber, Label = $"Captura {currentNumber}", StartLine = currentStart, EndLine = i });
            }
            currentStart = i;
            currentNumber = int.Parse(match.Groups[1].Value);
            any = true;
        }

        if (any)
        {
            blocks.Add(new CaptureBlockInfo { Number = currentNumber, Label = $"Captura {currentNumber}", StartLine = currentStart, EndLine = lines.Length });
        }
        else if (lines.Length > 0)
        {
            blocks.Add(new CaptureBlockInfo { Number = 0, Label = "Captura (arquivo inteiro)", StartLine = 0, EndLine = lines.Length });
        }

        return blocks;
    }

    /// <summary>Rewrites the file without the given line range [StartLine, EndLine).</summary>
    public static void DeleteBlock(string filePath, CaptureBlockInfo block)
    {
        var lines = File.ReadAllLines(filePath);
        var kept = lines.Take(block.StartLine).Concat(lines.Skip(block.EndLine));
        File.WriteAllLines(filePath, kept);
    }
}
