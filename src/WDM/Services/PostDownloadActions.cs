using System.Runtime.InteropServices;
using WDM.Models;

namespace WDM.Services;

/// <summary>Post-download OS integration (1DM MediaScannerService +
/// TempFilesDeletionService, desktop equivalents): shell visibility for finished
/// files, move-on-finish, and orphaned-temp/state reconciliation. Static service
/// class; all methods are best-effort and never throw.</summary>
public static class PostDownloadActions
{
    private const int SHCNE_CREATE = 0x0002;
    private const uint SHCNF_PATH = 0x0005;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern void SHChangeNotify(int wEventId, uint uFlags, string? dwItem1, string? dwItem2);

    /// <summary>Tells Explorer/indexers a file appeared (desktop MediaScanner
    /// equivalent) so completed downloads show up immediately.</summary>
    public static void NotifyFileCreated(string? fullPath)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(fullPath) || !File.Exists(fullPath))
                return;
            SHChangeNotify(SHCNE_CREATE, SHCNF_PATH, fullPath, null);
        }
        catch { }
    }

    /// <summary>Moves a finished download into the move-on-finish folder (1DM
    /// move-on-finish path). Same-volume rename, cross-volume copy+delete.
    /// Collisions get a numbered name; the task's folder/name are updated.</summary>
    public static bool TryMoveFinishedFile(DownloadTask task, string destFolder)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(task.FullPath) || !File.Exists(task.FullPath))
                return false;
            string dest = Path.GetFullPath(destFolder.Trim());
            if (dest.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
                return false;
            string source = Path.GetFullPath(task.FullPath);
            if (string.Equals(Path.GetDirectoryName(source), dest, StringComparison.OrdinalIgnoreCase))
                return true; // already there
            Directory.CreateDirectory(dest);
            string finalName = UniqueFileName(dest, task.FileName);
            string target = Path.Combine(dest, finalName);
            try
            {
                File.Move(source, target);
            }
            catch (IOException)
            {
                // Cross-volume: copy then delete the source.
                File.Copy(source, target);
                File.Delete(source);
            }
            task.SaveFolder = dest;
            task.FileName = finalName;
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Numbers a filename until it is free in the folder
    /// (same rule as the add-dialog dedupe).</summary>
    internal static string UniqueFileName(string folder, string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
            fileName = "download";
        if (!File.Exists(Path.Combine(folder, fileName)))
            return fileName;
        string ext = Path.GetExtension(fileName);
        string stem = Path.GetFileNameWithoutExtension(fileName);
        int counter = 1;
        string candidate;
        do
        {
            candidate = $"{stem} ({counter}){ext}";
            counter++;
        } while (File.Exists(Path.Combine(folder, candidate)) && counter < 100000);
        return candidate;
    }

    /// <summary>Deletes orphaned resume sidecars (<c>*.wdmstate</c>) whose file no
    /// longer has a live task, plus stale HLS temp dirs. Only touches WDM's own
    /// temp/state artifacts — never user files. Returns the orphan count.</summary>
    public static int CleanupOrphanedState(IEnumerable<string> liveFullPaths, IEnumerable<string> folders)
    {
        int removed = 0;
        try
        {
            var live = new HashSet<string>(
                liveFullPaths.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p =>
                {
                    try { return Path.GetFullPath(p); } catch { return p; }
                }),
                StringComparer.OrdinalIgnoreCase);
            foreach (var folder in folders.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
                    continue;
                string[] states;
                try { states = Directory.GetFiles(folder, "*.wdmstate"); }
                catch { continue; }
                foreach (var state in states)
                {
                    try
                    {
                        // Sidecar sits next to its file: <file>.wdmstate.
                        string owner = state[..^".wdmstate".Length];
                        if (!live.Contains(Path.GetFullPath(owner)))
                        {
                            File.Delete(state);
                            removed++;
                        }
                    }
                    catch { }
                }
                try { HlsDownloader.CleanStaleTempDirs(folder); } catch { }
            }
        }
        catch { }
        return removed;
    }
}
