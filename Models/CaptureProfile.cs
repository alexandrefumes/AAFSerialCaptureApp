using System.IO;
using System.Text.Json;

namespace AAFSerialCaptureApp.Models;

/// <summary>A saved combination of regex pattern + channel labels, so users can switch between experiments.</summary>
public class CaptureProfile
{
    public string Name { get; set; } = "";
    public string RegexPattern { get; set; } = "";
    public string LabelsText { get; set; } = "";
}

/// <summary>Persists <see cref="CaptureProfile"/> entries to a JSON file under %AppData%.</summary>
public static class ProfileStore
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "AAFSerialCaptureApp", "profiles.json");

    public static List<CaptureProfile> Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new List<CaptureProfile>();
            var json = File.ReadAllText(FilePath);
            return JsonSerializer.Deserialize<List<CaptureProfile>>(json) ?? new List<CaptureProfile>();
        }
        catch
        {
            return new List<CaptureProfile>();
        }
    }

    public static void Save(List<CaptureProfile> profiles)
    {
        var dir = Path.GetDirectoryName(FilePath)!;
        Directory.CreateDirectory(dir);
        var json = JsonSerializer.Serialize(profiles, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(FilePath, json);
    }
}
