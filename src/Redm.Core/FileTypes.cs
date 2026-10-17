namespace Redm.Core;

public static class FileTypes
{
    public static readonly string[] Default = "exe zip rar 7z mp4 mp3 pdf avi mkv mov pdf docx".Split(' ');
    public static bool ShouldCatch(string url, IEnumerable<string> types)
    {
        var ext = System.IO.Path.GetExtension(url.Split('?')[0]).TrimStart('.').ToLowerInvariant();
        return types.Contains(ext);
    }
}

public static class AvHook
{
    public static string BuildCommand(string avExe, string file)
        => $"{avExe} \"{file}\"";
}

public static class Exclusions
{
    // mirrors defexclist.txt intent: skip noisy static assets
    public static readonly string[] Masks = { "windowsupdate.com", "fbcdn", "widevine-cdm", "whatsapp", "skype" };
    public static bool IsExcluded(string url) => Masks.Any(m => url.Contains(m, System.StringComparison.OrdinalIgnoreCase));
}
