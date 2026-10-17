namespace Redm.Core;

/// <summary>Saved grabber projects (templates) with optional daily run time.</summary>
public record GrabberTemplate(string Name, GrabberProject Project, string RunAt);

public static class GrabberTemplates
{
    public static string Dir(string? baseDir = null) => Path.Combine(
        baseDir ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ReDM", "grabber"));

    public static void Save(string name, GrabberProject project, string runAt, string? baseDir = null)
    {
        string dir = Dir(baseDir);
        Directory.CreateDirectory(dir);
        string safe = string.Concat(name.Select(c => char.IsLetterOrDigit(c) ? c : '_'));
        File.WriteAllText(Path.Combine(dir, safe + ".json"),
            System.Text.Json.JsonSerializer.Serialize(new GrabberTemplate(name, project, runAt)));
    }

    public static List<GrabberTemplate> List(string? baseDir = null)
    {
        var outList = new List<GrabberTemplate>();
        string dir = Dir(baseDir);
        if (!Directory.Exists(dir)) return outList;
        foreach (var f in Directory.GetFiles(dir, "*.json"))
        {
            try
            {
                var t = System.Text.Json.JsonSerializer.Deserialize<GrabberTemplate>(File.ReadAllText(f));
                if (t is not null) outList.Add(t);
            }
            catch { }
        }
        return outList;
    }

    /// <summary>True when a daily "HH:mm" time matches now (same hour+minute).</summary>
    public static bool IsDue(string runAt, DateTime now)
    {
        var parts = runAt.Split(':');
        return parts.Length == 2 && int.TryParse(parts[0], out int h) && int.TryParse(parts[1], out int m)
            && now.Hour == h && now.Minute == m;
    }
}
