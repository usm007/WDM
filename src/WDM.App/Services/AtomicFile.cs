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
    private const string MutexName = @"Local\WDM.AtomicFile";

    public static void Write(string path, string content)
    {
        lock (FileLock)
        {
            // Cross-process guard: a second WDM copy (or updater) writing the
            // same JSON concurrently would otherwise interleave temp+rename.
            // Fail-open to the in-process lock on timeout — same as old behavior.
            bool ownsMutex = false;
            Mutex? mutex = null;
            try
            {
                mutex = new Mutex(initiallyOwned: false, MutexName);
                try { ownsMutex = mutex.WaitOne(TimeSpan.FromSeconds(10)); }
                catch (AbandonedMutexException) { ownsMutex = true; }
                catch { ownsMutex = false; }
                WriteCore(path, content);
            }
            finally
            {
                if (ownsMutex)
                {
                    try { mutex?.ReleaseMutex(); } catch { }
                }
                mutex?.Dispose();
            }
        }
    }

    private static void WriteCore(string path, string content)
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