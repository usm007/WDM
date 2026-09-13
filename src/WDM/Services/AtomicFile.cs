using System;
using System.IO;

namespace WDM.Services;

/// <summary>
/// Crash-safe file writes: content goes to a unique temp file first, then it is atomically
/// renamed over the target. A crash mid-write leaves the previous file intact instead
/// of a half-written one. Thread-safe to prevent concurrent write collisions.
/// </summary>
public static class AtomicFile
{
    private static readonly object FileLock = new();

    public static void Write(string path, string content)
    {
        lock (FileLock)
        {
            string dir = Path.GetDirectoryName(path) ?? AppDomain.CurrentDomain.BaseDirectory;
            Directory.CreateDirectory(dir);
            SweepStaleTemps(dir, Path.GetFileName(path));
            string tmp = Path.Combine(dir, $"{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
            try
            {
                File.WriteAllText(tmp, content);
                File.Move(tmp, path, overwrite: true);
            }
            catch
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
                throw;
            }
        }
    }

    /// <summary>Best-effort cleanup of orphaned temp files from crashed/killed
    /// writes. Only touches our own pattern older than a day.</summary>
    private static void SweepStaleTemps(string dir, string baseName)
    {
        try
        {
            foreach (string f in Directory.GetFiles(dir, baseName + ".*.tmp"))
            {
                try
                {
                    if (DateTime.UtcNow - File.GetLastWriteTimeUtc(f) > TimeSpan.FromDays(1))
                        File.Delete(f);
                }
                catch { }
            }
        }
        catch { }
    }
}