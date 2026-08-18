namespace WDM.Services;

/// <summary>
/// Crash-safe file writes: content goes to a temp file first, then it is atomically
/// renamed over the target. A crash mid-write leaves the previous file intact instead
/// of a half-written one.
/// </summary>
public static class AtomicFile
{
    public static void Write(string path, string content)
    {
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, content);
        File.Move(tmp, path, overwrite: true);
    }
}