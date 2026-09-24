using System;
using System.IO;

namespace WDM.Services;

/// <summary>Local-only activity log for testing: silent, file-only, starts with
/// the app and records lifecycle, task transitions, captures, updates and
/// errors to <c>%LocalAppData%\WDM-Data\activity.log</c> (or the redirected
/// <see cref="TaskStore.AppDir"/>). Nothing leaves the machine — this file is
/// for later analysis, never uploaded. Never throws, never touches the UI.</summary>
public static class ActivityLog
{
    private static readonly object _lock = new();
    private static bool _started;
    private static string _path = "";

    private const long MaxBytes = 8 * 1024 * 1024;
    private const int KeepBackups = 3;
    private const int MaxRecentEntries = 1500;
    private static readonly List<ActivityLogEntry> _recentEntries = new(500);

    public static event Action<ActivityLogEntry>? EntryLogged;

    public static IReadOnlyList<ActivityLogEntry> GetRecentEntries()
    {
        lock (_lock)
        {
            return _recentEntries.ToArray();
        }
    }

    public static void ClearRecentEntries()
    {
        lock (_lock)
        {
            _recentEntries.Clear();
        }
    }

    public static string LogPath
    {
        get
        {
            if (!string.IsNullOrEmpty(_path))
                return _path;
            try { return Path.Combine(TaskStore.AppDir, "activity.log"); }
            catch { return Path.Combine(Path.GetTempPath(), "WDM-activity.log"); }
        }
    }

    /// <summary>Starts logging (idempotent). Called once from App.OnStartup on
    /// real launches — screenshot/test runs don't start it.</summary>
    public static void Start(string version, string[] args, bool startMinimized)
    {
        try
        {
            lock (_lock)
            {
                if (_started)
                    return;
                _started = true;
                _path = "";
                RotateIfNeeded();
                string argSummary = args is { Length: > 0 } ? $"{args.Length} arg(s)" : "no args";
                WriteLine("START", $"WDM {version} started ({argSummary}, minimized={startMinimized})");
            }
        }
        catch { }
    }

    public static void Stop()
    {
        try
        {
            lock (_lock)
            {
                if (!_started)
                    return;
                WriteLine("STOP", "WDM exiting");
                _started = false;
            }
        }
        catch { }
    }

    public static void Write(string tag, string message)
    {
        try
        {
            lock (_lock)
            {
                if (!_started)
                {
                    _started = true;
                    _path = "";
                }
                RotateIfNeeded();
                WriteLine(tag, message);
            }
        }
        catch { }
    }

    private static void WriteLine(string tag, string message)
    {
        string clean = (message ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
        clean = ToAsciiSafe(clean);
        if (clean.Length > 2000)
            clean = clean.Substring(0, 2000) + "...";

        var entry = new ActivityLogEntry(DateTime.Now, tag, clean);
        if (_recentEntries.Count >= MaxRecentEntries)
        {
            _recentEntries.RemoveRange(0, 300);
        }
        _recentEntries.Add(entry);

        File.AppendAllText(LogPath,
            $"{entry.Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{tag}] {clean}{Environment.NewLine}");

        try { EntryLogged?.Invoke(entry); } catch { }
    }

    /// <summary>The log must stay readable in any editor/encoding: fold fancy
    /// punctuation to ASCII so quotes/dashes never show as mojibake.</summary>
    private static string ToAsciiSafe(string s)
    {
        var sb = new System.Text.StringBuilder(s.Length);
        foreach (char c in s)
        {
            sb.Append(c switch
            {
                '\u2018' or '\u2019' or '\u201c' or '\u201d' => '"',
                '\u2013' or '\u2014' or '\u2212' => '-',
                '\u2026' => "...",
                '\u00a0' => ' ',
                _ when c < 128 => c,
                _ when char.IsControl(c) => ' ',
                _ => c,
            });
        }
        return sb.ToString();
    }

    private static void RotateIfNeeded()
    {
        try
        {
            var info = new FileInfo(LogPath);
            if (!info.Exists || info.Length < MaxBytes)
                return;
            string oldest = LogPath + "." + KeepBackups;
            try { if (File.Exists(oldest)) File.Delete(oldest); } catch { }
            for (int i = KeepBackups - 1; i >= 1; i--)
            {
                try
                {
                    string src = LogPath + "." + i;
                    if (File.Exists(src)) File.Move(src, LogPath + "." + (i + 1), overwrite: true);
                }
                catch { }
            }
            try { File.Move(LogPath, LogPath + ".1", overwrite: true); } catch { }
        }
        catch { }
    }

    /// <summary>Compact progress summary for log lines, e.g. "16.4 MB/2.39 GB (1%)".
    /// Returns "" when the total is unknown so callers can omit it.</summary>
    public static string ProgressOf(long downloaded, long total)
    {
        try
        {
            if (total <= 0)
                return downloaded > 0 ? $"{FormatBytes(downloaded)}/unknown" : "";
            double pct = (double)downloaded / total * 100.0;
            if (pct < 0) pct = 0;
            if (pct > 100) pct = 100;
            return $"{FormatBytes(downloaded)}/{FormatBytes(total)} ({pct:0}%)";
        }
        catch { return ""; }
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 0) bytes = 0;
        const long KB = 1024, MB = KB * 1024, GB = MB * 1024;
        if (bytes >= GB) return $"{bytes / (double)GB:0.##} GB";
        if (bytes >= MB) return $"{bytes / (double)MB:0.#} MB";
        if (bytes >= KB) return $"{bytes / (double)KB:0.#} KB";
        return $"{bytes} B";
    }

    /// <summary>Short host for log lines (full URL kept in ADD/CAPTURE lines).</summary>
    public static string HostOf(string? url)    {
        try
        {
            if (!string.IsNullOrWhiteSpace(url) &&
                Uri.TryCreate(url, UriKind.Absolute, out var uri))
                return uri.Host;
        }
        catch { }
        return "?";
    }
}

public sealed class ActivityLogEntry
{
    public DateTime Timestamp { get; }
    public string Tag { get; }
    public string Message { get; }

    public string TimeString => Timestamp.ToString("HH:mm:ss.fff");
    public string TagWithBrackets => $"[{Tag}]";

    public ActivityLogEntry(DateTime timestamp, string tag, string message)
    {
        Timestamp = timestamp;
        Tag = tag;
        Message = message;
    }

    public override string ToString() => $"{TimeString} [{Tag}] {Message}";
}
