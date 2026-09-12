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
}