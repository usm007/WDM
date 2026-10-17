namespace Redm.Core;

/// <summary>Trial license state. Keys are stored locally; there is no license
/// server, so a stored key only records registration, it is not verified.</summary>
public static class LicenseStore
{
    public static License Load(string? baseDir = null)
    {
        string path = LicensePath(baseDir);
        try
        {
            if (File.Exists(path))
            {
                var lic = System.Text.Json.JsonSerializer.Deserialize<License>(File.ReadAllText(path));
                if (lic is not null) return lic with { FirstUseUnix = lic.FirstUseUnix == 0 ? Now() : lic.FirstUseUnix };
            }
        }
        catch { }
        var fresh = new License("trial", Now(), "");
        Save(fresh, baseDir);
        return fresh;
    }

    public static void Save(License license, string? baseDir = null)
    {
        try
        {
            string dir = Path.GetDirectoryName(LicensePath(baseDir))!;
            Directory.CreateDirectory(dir);
            File.WriteAllText(LicensePath(baseDir), System.Text.Json.JsonSerializer.Serialize(license));
        }
        catch { }
    }

    private static string LicensePath(string? baseDir) => System.IO.Path.Combine(
        baseDir ?? System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ReDM"),
        "license.json");

    private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();
}
