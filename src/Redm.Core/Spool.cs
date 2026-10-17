namespace Redm.Core;

/// <summary>Closed-app spool: when the app is not running, the native host
/// stores validated URLs here instead of failing; the app drains the spool
/// at startup. No service, no admin.</summary>
public static class Spool
{
    public static string Dir(string? baseDir = null) => Path.Combine(
        baseDir ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ReDM", "spool"));

    public static void Write(string url, string? baseDir = null)
    {
        string dir = Dir(baseDir);
        Directory.CreateDirectory(dir);
        string file = Path.Combine(dir, DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-ffffff") + "-" + Guid.NewGuid().ToString("N")[..8] + ".json");
        File.WriteAllText(file, System.Text.Json.JsonSerializer.Serialize(new { url }));
    }

    /// <summary>Reads and removes all spooled URLs, oldest first.</summary>
    public static List<string> Drain(string? baseDir = null)
    {
        var outList = new List<string>();
        string dir = Dir(baseDir);
        if (!Directory.Exists(dir)) return outList;
        foreach (var f in Directory.GetFiles(dir, "*.json").OrderBy(x => x))
        {
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(f));
                if (doc.RootElement.TryGetProperty("url", out var u) && u.GetString() is string s && s != "")
                    outList.Add(s);
            }
            catch { }
            try { File.Delete(f); } catch { }
        }
        return outList;
    }
}
