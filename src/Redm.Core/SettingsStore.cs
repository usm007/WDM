namespace Redm.Core;

/// <summary>JSON settings persistence. Path-injectable for tests.</summary>
public static class SettingsStore
{
    public static Settings Load(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var s = System.Text.Json.JsonSerializer.Deserialize<Settings>(File.ReadAllText(path));
                if (s is not null) return s;
            }
        }
        catch { }
        return new Settings();
    }

    public static void Save(string path, Settings settings)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(settings,
            new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
    }

    public static string DefaultPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ReDM", "settings.json");
}
