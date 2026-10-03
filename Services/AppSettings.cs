using System.Text.Json;

namespace LEDStripController;

public sealed class AppSettings
{
    /// <summary>Bluetooth address of the last connected strip. Cleared on explicit disconnect.</summary>
    public ulong? LastAddress { get; set; }
    public bool AmbilightSmooth { get; set; } = false;

    static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "LEDStripController", "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new();
        }
        catch { }
        return new();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this));
        }
        catch { }
    }
}
