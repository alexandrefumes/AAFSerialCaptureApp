using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using System.Windows;
using MessageBox = System.Windows.MessageBox;
using AAFSerialCaptureApp.Models;

namespace AAFSerialCaptureApp;

/// <summary>Editor for the capture regex and channel labels, with a live preview of what the current
/// regex matches against already-captured/loaded data.</summary>
public partial class SettingsWindow : Window
{
    private readonly string[] _previewSourceLines;
    private readonly ObservableCollection<CaptureProfile> _profiles = new(ProfileStore.Load());

    public event Action<string>? RegexApplied;
    public event Action<string>? LabelsApplied;

    public SettingsWindow(string regexPattern, string labelsText, string previewSourceText)
    {
        InitializeComponent();

        _previewSourceLines = previewSourceText.Split('\n');

        RegexTextBox.Text = regexPattern;
        LabelsTextBox.Text = labelsText;
        ProfilesListBox.ItemsSource = _profiles;

        RefreshPreview();
    }

    private void ApplyRegexButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _ = new Regex(RegexTextBox.Text);
            RegexApplied?.Invoke(RegexTextBox.Text);
            RefreshPreview();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Regex inválida: {ex.Message}", "Erro", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ApplyLabelsButton_Click(object sender, RoutedEventArgs e)
    {
        LabelsApplied?.Invoke(LabelsTextBox.Text);
    }

    private void SaveProfileButton_Click(object sender, RoutedEventArgs e)
    {
        var name = ProfileNameTextBox.Text.Trim();
        if (string.IsNullOrEmpty(name))
        {
            MessageBox.Show(this, "Informe um nome para o perfil.", "Aviso", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var existing = _profiles.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
        if (existing != null) _profiles.Remove(existing);

        _profiles.Add(new CaptureProfile { Name = name, RegexPattern = RegexTextBox.Text, LabelsText = LabelsTextBox.Text });
        ProfileStore.Save(_profiles.ToList());
    }

    private void LoadProfileButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: CaptureProfile profile }) return;
        RegexTextBox.Text = profile.RegexPattern;
        LabelsTextBox.Text = profile.LabelsText;
        RefreshPreview();
    }

    private void DeleteProfileButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: CaptureProfile profile }) return;
        if (MessageBox.Show(this, $"Excluir o perfil \"{profile.Name}\"?", "Confirmar exclusão",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;

        _profiles.Remove(profile);
        ProfileStore.Save(_profiles.ToList());
    }

    private void ProfilesListBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (ProfilesListBox.SelectedItem is CaptureProfile profile) ProfileNameTextBox.Text = profile.Name;
    }

    private void ProfilesListBox_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (ProfilesListBox.SelectedItem is not CaptureProfile profile) return;
        RegexTextBox.Text = profile.RegexPattern;
        LabelsTextBox.Text = profile.LabelsText;
        RefreshPreview();
    }

    private void RegexTextBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) => RefreshPreview();

    private void RefreshPreview()
    {
        if (PreviewTextBox == null) return;

        Regex regex;
        try
        {
            regex = new Regex(RegexTextBox.Text);
        }
        catch (Exception ex)
        {
            PreviewTextBox.Text = $"Regex inválida: {ex.Message}";
            return;
        }

        var lines = new List<string>();
        foreach (var line in _previewSourceLines.Take(40))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var matches = regex.Matches(line);
            if (matches.Count == 0)
            {
                lines.Add($"(sem correspondência) {line}");
                continue;
            }

            var parsed = string.Join(", ", matches.Select(m => $"{m.Groups["name"].Value}={m.Groups["value"].Value}"));
            lines.Add($"{line}  =>  {parsed}");
        }

        PreviewTextBox.Text = lines.Count > 0
            ? string.Join(Environment.NewLine, lines)
            : "(sem dados capturados/carregados para testar)";
    }
}
