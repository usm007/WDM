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
                    return;
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
        File.AppendAllText(LogPath,
            $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{tag}] {clean}{Environment.NewLine}");
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

    /// <summary>Short host for log lines (full URL kept in ADD/CAPTURE lines).</summary>
    public static string HostOf(string? url)
    {
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
