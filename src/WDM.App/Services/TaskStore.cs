using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using WDM.Models;

namespace WDM.Services;

public enum AppTheme
{
    Default
}

/// <summary>Target container for HLS auto-remux after segment stitching.
/// Mp4 preserves historic behavior; Mkv keeps more tracks/subtitles;
/// KeepTs disables the ffmpeg remux and leaves the stitched .ts file.</summary>
public enum HlsContainer
{
    Mp4,
    Mkv,
    KeepTs
}

public sealed class AppSettings
{
    public string DownloadFolder { get; set; } = DownloadTask.DefaultSaveFolder;
    public int DefaultChunkCount { get; set; } = 0;
    public int MaxConcurrentDownloads { get; set; } = 3;
    public long GlobalSpeedLimitKbps { get; set; }
    public bool HasPromptedExtensionInstall { get; set; } = false;
    public bool MinimizeToTray { get; set; } = true;
    public bool NotifyOnCompletion { get; set; } = true;
    public bool ShowTrayProgress { get; set; } = false;
    public double? ProgressPanelLeft { get; set; }
    public double? ProgressPanelTop { get; set; }
    public bool RunAtStartup { get; set; }
    public bool StartInBackground { get; set; }
    public bool UseDarkTheme { get; set; }
    public AppTheme Theme { get; set; } = AppTheme.Default;
    public string? LastRunVersion { get; set; }

    // YouTube & Media downloads
    public bool EnableYouTubeDownloads { get; set; } = true;

    /// <summary>Master kill-switch for media discovery (embed/player-page
    /// resolving, non-YouTube media/playlist resolving, HLS/DASH streams,
    /// page-title fetching). Code stays in place; when false these paths are
    /// bypassed with a "media fetching is disabled" message. YouTube links
    /// and direct file downloads are unaffected. Temporarily off by user
    /// request; re-enable any time in Options.</summary>
    public bool EnableMediaFetching { get; set; } = true;
    public string? YouTubeBrowserCookies { get; set; } = "none";

    // Internet title sync: fetch the video title from the source page (oEmbed,
    // then page metadata) when the filename carries no title. Uses the capture's
    // own Cookie/UA session; re-requests an already-visited page.
    public bool EnableTitleSync { get; set; } = true;

    // HLS post-processing: after HLS segments are stitched, optionally remux the
    // .ts concat into .mp4 (default, best compatibility) or .mkv (more tracks)
    // via the bundled ffmpeg. KeepTs leaves the stitched .ts file untouched.
    public HlsContainer HlsContainer { get; set; } = HlsContainer.Mp4;

    // Updates
    public bool CheckForUpdates { get; set; } = true;
    public string? LastUpdateCheckUtc { get; set; }
    /// <summary>When true and the install is Velopack-managed, a found update is
    /// downloaded and applied automatically at startup (restart without asking).
    /// Full Setup.exe / portable zip are never auto-run — new users only.</summary>
    public bool AutoDownloadUpdates { get; set; } = false;

    // Automatic retry
    public int MaxRetries { get; set; } = 3;

    /// <summary>Minimum auto-catch size (browser extension): files smaller than
    /// this are left to the browser instead of being handed to WDM. 0 disables
    /// the gate (catch everything). Unknown sizes always pass (can't be judged).</summary>
    public long MinCatchSizeBytes { get; set; } = 200L * 1024 * 1024;    /// <summary>1DM <c>always_retry_download</c> (default OFF), bounded by
    /// <see cref="MaxRetries"/> per task: a timer re-queues Failed tasks until
    /// their per-task budget is spent. User Pause/Cancel/Remove always wins.</summary>
    public bool AutoResumeFailed { get; set; } = false;

    // Notifications (1DM pref_notification.xml, desktop subset: no vibration/lockscreen)
    public bool NotifyOnAdded { get; set; } = false;
    public bool NotifyOnStarted { get; set; } = false;
    public bool NotifyOnError { get; set; } = true;
    public bool NotificationSound { get; set; } = true;
    public bool DetailedNotifications { get; set; } = false;

    // Category auto-routing
    public bool RouteByCategory { get; set; } = true;
    public Dictionary<string, string> CategoryFolders { get; set; } = new()
    {
        { "Video",      Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "Video") },
        { "Music",      Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "Music") },
        { "Document",   Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "Documents") },
        { "Compressed", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "Compressed") },
        { "Program",    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "Programs") },
        { "Other",      Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads") },
    };

    // Scheduler (1DM util/Scheduler, desktop subset: time window + days + speed cap)
    public bool SchedulerEnabled { get; set; } = false;
    public TimeSpan SchedulerStart { get; set; } = new TimeSpan(22, 0, 0);
    public TimeSpan SchedulerStop { get; set; } = new TimeSpan(7, 0, 0);
    public long SchedulerSpeedLimitKbps { get; set; } = 0;
    public List<DayOfWeek> SchedulerDays { get; set; } = new()
    {
        DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday,
        DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday,
    };

    // Proxy (manual HTTP/HTTPS proxy, IDM-style; .NET has no SOCKS support)
    public bool ProxyEnabled { get; set; } = false;
    public string ProxyHost { get; set; } = "";
    public int ProxyPort { get; set; } = 8080;
    public string? ProxyUsername { get; set; }
    /// <summary>Stored in plaintext in settings.json (same tradeoff as IDM —
    /// a per-user file under %LocalAppData%).</summary>
    public string? ProxyPassword { get; set; }

    // Post-download
    public bool ComputeChecksum { get; set; }
    public string? PostDownloadScript { get; set; }

    // Post-download automation (1DM pref_automation.xml, minus wifi-off: no mobile radio)
    public bool MoveOnFinish { get; set; } = false;
    public string? MoveOnFinishFolder { get; set; }
    public bool RemoveLinkAfterFinish { get; set; } = false;
    public int DeleteFinishedLinksAfterDays { get; set; } = 0;
}

/// <summary>
/// JSON converter factory that tolerates unknown enum values instead of throwing.
/// A single unrecognized value (e.g. written by a newer or older app version) falls
/// back to the enum default, so one bad value can never sink a whole file.
/// Serialized output stays identical to JsonStringEnumConverter (camel-agnostic names).
/// </summary>
public sealed class LenientEnumConverterFactory : JsonConverterFactory
{
    public override bool CanConvert(Type typeToConvert) => typeToConvert.IsEnum;

    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options)
        => (JsonConverter)Activator.CreateInstance(
            typeof(LenientEnumConverter<>).MakeGenericType(typeToConvert))!;
}

public sealed class LenientEnumConverter<T> : JsonConverter<T> where T : struct, Enum
{
    public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            string? s = reader.GetString();
            if (!string.IsNullOrWhiteSpace(s))
            {
                // NOTE: Enum.TryParse accepts plain numeric strings ("99" →
                // (T)99) even when undefined, which would bypass the IsDefined
                // guard below and persist an undefined value that round-trips
                // forever. Route numeric strings through the IsDefined check.
                if (int.TryParse(s, out int numeric))
                    return Enum.IsDefined(typeof(T), numeric)
                        ? (T)Enum.ToObject(typeof(T), numeric)
                        : default;
                if (Enum.TryParse(s, ignoreCase: true, out T named) && Enum.IsDefined(typeof(T), named))
                    return named;
            }
            return default;
        }
        if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out int num))
        {
            if (Enum.IsDefined(typeof(T), num))
                return (T)Enum.ToObject(typeof(T), num);
            return default;
        }
        if (reader.TokenType == JsonTokenType.Null)
            return default;
        // Unexpected token (object/array/bool where an enum was expected): skip it.
        using var _ = JsonDocument.ParseValue(ref reader);
        return default;
    }

    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.ToString());
}

public sealed class TaskStore
{
    /// <summary>
    /// User data home. Deliberately OUTSIDE the install root (%LocalAppData%\WDM),
    /// which Velopack/Inno own and may wipe on uninstall or reinstall. Holds
    /// tasks.json, settings.json, cookies, downloaded engines and the WebView2
    /// profile. Never store binaries here; never delete this folder on uninstall.
    /// Plain static (not readonly) so tests/tools can redirect it; app code must
    /// treat it as read-only after startup.
    /// </summary>
    public static string AppDir = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("WDM_DATA_DIR"))
        ? Path.GetFullPath(Environment.GetEnvironmentVariable("WDM_DATA_DIR")!)
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WDM-Data");

    /// <summary>
    /// Legacy data location (inside the install root). Only read by the one-time
    /// migration in <see cref="EnsureMigrated"/>; never written to by new versions.
    /// </summary>
    public static string LegacyAppDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WDM");

    private static string SettingsPath => Path.Combine(AppDir, "settings.json");
    private static string TasksPath => Path.Combine(AppDir, "tasks.json");
    private static string TasksBackupPath => TasksPath + ".bak";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        Converters = { new LenientEnumConverterFactory() },
    };

    /// <summary>True when the last tasks load hit an error. While true,
    /// <see cref="SaveTasks"/> refuses to overwrite tasks.json so a failed read
    /// can never cement an empty list over the user's real data.</summary>
    public static bool TasksLoadFailed { get; private set; }

    /// <summary>Number of individual records skipped while salvaging a corrupt file.</summary>
    public static int TasksLoadSkippedRecords { get; private set; }

    /// <summary>Error text from the last failed tasks load (also in wdm_error.log).</summary>
    public static string? LastTasksLoadError { get; private set; }

    /// <summary>True when the last load transparently restored tasks.json from backup.</summary>
    public static bool TasksBackupRestored { get; private set; }

    private static bool _sessionBackupTaken;

    private static void LogNonFatal(Exception ex)
    {
        try { WDM.App.LogException(ex); } catch { }
    }

    private static void FailLoad(Exception ex)
    {
        TasksLoadFailed = true;
        LastTasksLoadError = ex.Message;
        LogNonFatal(ex);
    }

    private static bool IsExistingUser()
    {
        try { return File.Exists(SettingsPath); } catch { return false; }
    }

    /// <summary>
    /// One-time migration of user data out of the legacy install-root location.
    /// Safe to call on every startup: only moves files/dirs that exist in the
    /// legacy folder and are missing in the new home (copy-then-delete, so a
    /// failed delete still leaves a good copy behind).
    /// </summary>
    public static void EnsureMigrated()
    {
        try
        {
            string newRoot = Path.GetFullPath(AppDir).TrimEnd(Path.DirectorySeparatorChar);
            string oldRoot = Path.GetFullPath(LegacyAppDir).TrimEnd(Path.DirectorySeparatorChar);
            if (string.Equals(newRoot, oldRoot, StringComparison.OrdinalIgnoreCase))
                return;

            MigrateFile("tasks.json");
            MigrateFile("settings.json");
            MigrateFile("youtube_cookies.txt");
            try
            {
                if (Directory.Exists(LegacyAppDir))
                {
                    foreach (string bak in Directory.GetFiles(LegacyAppDir, "tasks.json.bak*"))
                        MigrateFile(Path.GetFileName(bak));
                }
            }
            catch (Exception ex) { LogNonFatal(ex); }
            MigrateDir("bin");
            MigrateDir("WebView2");
        }
        catch (Exception ex) { LogNonFatal(ex); }
    }

    private static bool MigrateFile(string name)
    {
        try
        {
            string src = Path.Combine(LegacyAppDir, name);
            string dst = Path.Combine(AppDir, name);
            if (!File.Exists(src) || File.Exists(dst))
                return false;
            Directory.CreateDirectory(AppDir);
            File.Copy(src, dst);
            try { File.Delete(src); } catch { /* copy won; a stale legacy file is harmless */ }
            return true;
        }
        catch (Exception ex) { LogNonFatal(ex); return false; }
    }

    private static bool MigrateDir(string name)
    {
        try
        {
            string src = Path.Combine(LegacyAppDir, name);
            string dst = Path.Combine(AppDir, name);
            if (!Directory.Exists(src))
                return false;
            if (!Directory.Exists(dst))
            {
                CopyDirectory(src, dst);
                try { Directory.Delete(src, recursive: true); } catch { }
                return true;
            }
            // A previous run left a split directory (partial copy then abort):
            // merge files missing at the destination instead of skipping forever.
            MergeMissing(src, dst);
            try
            {
                if (!Directory.EnumerateFileSystemEntries(src).Any())
                    Directory.Delete(src, recursive: true);
            }
            catch { }
            return true;
        }
        catch (Exception ex) { LogNonFatal(ex); return false; }
    }

    private static void MergeMissing(string src, string dst)
    {
        try
        {
            Directory.CreateDirectory(dst);
            foreach (string file in Directory.GetFiles(src))
            {
                try
                {
                    string target = Path.Combine(dst, Path.GetFileName(file));
                    if (!File.Exists(target))
                        File.Copy(file, target);
                }
                catch (Exception ex) { LogNonFatal(ex); }
            }
            foreach (string dir in Directory.GetDirectories(src))
            {
                try { MergeMissing(dir, Path.Combine(dst, Path.GetFileName(dir))); }
                catch (Exception ex) { LogNonFatal(ex); }
            }
        }
        catch (Exception ex) { LogNonFatal(ex); }
    }

    private static void CopyDirectory(string src, string dst)
    {
        Directory.CreateDirectory(dst);
        foreach (string file in Directory.GetFiles(src))
            File.Copy(file, Path.Combine(dst, Path.GetFileName(file)), overwrite: true);
        foreach (string dir in Directory.GetDirectories(src))
            CopyDirectory(dir, Path.Combine(dst, Path.GetFileName(dir)));
    }

    public static AppSettings LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath), JsonOptions) ?? new AppSettings();
                return ValidateSettings(loaded);
            }
        }
        catch (Exception ex)
        {
            LogNonFatal(ex);
            // Corrupt settings.json: try the backup before surrendering to
            // defaults (mirrors the tasks.json recovery path). A failed load
            // must never cement defaults over a recoverable file.
            try
            {
                string bak = SettingsPath + ".bak";
                if (File.Exists(bak))
                {
                    var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(bak), JsonOptions);
                    if (loaded is not null)
                    {
                        LogNonFatal(new InvalidOperationException("settings.json was unreadable; restored from settings.json.bak."));
                        return ValidateSettings(loaded);
                    }
                }
            }
            catch (Exception bakEx) { LogNonFatal(bakEx); }
        }
        return new AppSettings();
    }
    /// <summary>Normalizes a hand-editable folder setting. .NET's
    /// <see cref="Path.GetInvalidPathChars"/> omits Windows filename bans
    /// (&lt;&gt;:"|?*), so each non-separator char is checked against
    /// <see cref="Path.GetInvalidFileNameChars"/> too — a path with those can
    /// never be created on Windows. Returns false when unusable.</summary>
    private static bool TryNormalizeFolder(string? path, out string full)
    {
        full = "";
        if (string.IsNullOrWhiteSpace(path))
            return false;
        try
        {
            full = Path.GetFullPath(path.Trim());
        }
        catch { return false; }
        if (full.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
            return false;
        foreach (char c in full)
        {
            if (c == Path.DirectorySeparatorChar || c == Path.AltDirectorySeparatorChar)
                continue;
            if (c == Path.VolumeSeparatorChar)
                continue;
            if (Array.IndexOf(Path.GetInvalidFileNameChars(), c) >= 0)
                return false;
        }
        return true;
    }

    /// <summary>Clamps hand-edited or out-of-range settings to safe values so a
    /// corrupt settings.json can never cause thread explosions, unbounded
    /// retries, or crash loops downstream.</summary>
    private static AppSettings ValidateSettings(AppSettings s)
    {
        s.MaxConcurrentDownloads = Math.Clamp(s.MaxConcurrentDownloads, 1, 16);
        s.MaxRetries = Math.Clamp(s.MaxRetries, 0, 20);
        s.DefaultChunkCount = Math.Clamp(s.DefaultChunkCount, 0, 32);
        s.GlobalSpeedLimitKbps = Math.Clamp(s.GlobalSpeedLimitKbps, 0, 1_000_000);
        s.SchedulerSpeedLimitKbps = Math.Clamp(s.SchedulerSpeedLimitKbps, 0, 1_000_000);
        s.DeleteFinishedLinksAfterDays = Math.Clamp(s.DeleteFinishedLinksAfterDays, 0, 365);
        s.MinCatchSizeBytes = Math.Clamp(s.MinCatchSizeBytes, 0, 10L * 1024 * 1024 * 1024);
        s.ProxyPort = Math.Clamp(s.ProxyPort, 1, 65535);
        if (s.SchedulerDays is null)
            s.SchedulerDays = new AppSettings().SchedulerDays;
        // An explicitly emptied list is preserved (window never applies); only a
        // missing list gets the all-days default.
        if (!string.IsNullOrWhiteSpace(s.MoveOnFinishFolder))
        {
            string full;
            s.MoveOnFinishFolder = TryNormalizeFolder(s.MoveOnFinishFolder, out full) ? full : null;
        }
        if (string.IsNullOrWhiteSpace(s.DownloadFolder))
            s.DownloadFolder = DownloadTask.DefaultSaveFolder;
        else
        {
            string full;
            s.DownloadFolder = TryNormalizeFolder(s.DownloadFolder, out full)
                ? full
                : DownloadTask.DefaultSaveFolder;
        }
        if (s.CategoryFolders is null)
            s.CategoryFolders = new AppSettings().CategoryFolders;
        else
        {
            foreach (var key in s.CategoryFolders.Keys.ToList())
            {
                if (string.IsNullOrWhiteSpace(s.CategoryFolders[key]))
                    s.CategoryFolders.Remove(key);
            }
        }
        return s;
    }

    public static void SaveSettings(AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(AppDir);
            // Keep a backup so a crash mid-write (or a corrupt-in-memory state)
            // never destroys the last good settings. Best-effort by design.
            try
            {
                if (File.Exists(SettingsPath))
                    File.Copy(SettingsPath, SettingsPath + ".bak", overwrite: true);
            }
            catch { }
            AtomicFile.Write(SettingsPath, JsonSerializer.Serialize(settings, JsonOptions));
        }
        catch (Exception ex)
        {
            LogNonFatal(ex);
        }
    }

    public static List<TaskRecord> LoadTasks()
    {
        TasksLoadFailed = false;
        TasksLoadSkippedRecords = 0;
        LastTasksLoadError = null;
        TasksBackupRestored = false;

        if (!File.Exists(TasksPath))
            return new List<TaskRecord>(); // Fresh user — nothing to protect.

        string text;
        try
        {
            text = File.ReadAllText(TasksPath);
        }
        catch (Exception ex)
        {
            FailLoad(ex);
            return new List<TaskRecord>();
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            // An existing-but-empty file is corruption (crash mid-write), not
            // "zero tasks" — restoring .bak or blocking saves (FailLoad)
            // prevents cementing data loss over the user's real list.
            if (IsExistingUser() && TryRestoreBackup(out var restoredEmpty))
                return restoredEmpty;
            FailLoad(new InvalidDataException("tasks.json is empty; refusing to overwrite (possible truncation)."));
            return new List<TaskRecord>();
        }

        // Fast path: the whole file parses.
        try
        {
            var parsed = JsonSerializer.Deserialize<List<TaskRecord>>(text, JsonOptions)
                ?? new List<TaskRecord>();
            // A [null] element parses into a null entry and would crash the
            // loader below — a null entry carries no user data, drop it.
            parsed.RemoveAll(r => r is null);
            return parsed;
        }
        catch (Exception ex)
        {
            LastTasksLoadError = ex.Message;
            LogNonFatal(ex);
        }

        // Slow path: an existing user's list failed to parse. Try the backup
        // before falling back to per-record salvage.
        if (IsExistingUser() && TryRestoreBackup(out var restored))
            return restored;

        var salvaged = SalvageRecords(text);
        // Refuse future overwrites only when records may have been lost
        // (skipped records, or an unreadable document — the latter flags
        // itself inside SalvageRecords). A fully-salvaged list is complete,
        // so saving it is safe.
        if (TasksLoadSkippedRecords > 0)
            TasksLoadFailed = true;
        return salvaged;
    }

    private static bool TryRestoreBackup(out List<TaskRecord> restored)
    {
        restored = new List<TaskRecord>();
        try
        {
            if (!File.Exists(TasksBackupPath))
                return false;
            string text = File.ReadAllText(TasksBackupPath);
            var parsed = JsonSerializer.Deserialize<List<TaskRecord>>(text, JsonOptions);
            if (parsed is null)
                return false;
            parsed.RemoveAll(r => r is null);
            if (parsed.Count == 0)
            {
                // Valid-but-empty backup: zero tasks is a legitimate state,
                // not corruption. Leave the main file alone (the next save
                // rewrites it from the live list) and proceed empty.
                LogNonFatal(new InvalidOperationException(
                    "tasks.json was unreadable but tasks.json.bak is a valid empty list; starting empty."));
                return true;
            }
            string tmp = TasksPath + $".restore-{Guid.NewGuid():N}.tmp";
            File.WriteAllText(tmp, text);
            try
            {
                File.Move(tmp, TasksPath, overwrite: true);
            }
            catch
            {
                try { File.Delete(tmp); } catch { }
                throw;
            }
            TasksBackupRestored = true;
            LogNonFatal(new InvalidOperationException(
                $"tasks.json was unreadable and has been restored from tasks.json.bak ({parsed.Count} records)."));
            restored = parsed;
            return true;
        }
        catch (Exception ex)
        {
            LogNonFatal(ex);
            return false;
        }
    }

    private static List<TaskRecord> SalvageRecords(string text)
    {
        var salvaged = new List<TaskRecord>();
        try
        {
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return salvaged;
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                try
                {
                    var rec = el.Deserialize<TaskRecord>(JsonOptions);
                    if (rec is not null)
                        salvaged.Add(rec);
                    else
                        TasksLoadSkippedRecords++;
                }
                catch
                {
                    TasksLoadSkippedRecords++;
                }
            }
        }
        catch (Exception ex)
        {
            LastTasksLoadError = (LastTasksLoadError ?? "") + " | salvage: " + ex.Message;
            // The document itself is unreadable — records may have been lost.
            TasksLoadFailed = true;
            LogNonFatal(ex);
        }
        return salvaged;
    }

    public static void SaveTasks(IEnumerable<DownloadTask> tasks, Func<DownloadTask, List<SegmentRecord>?>? segmentProvider = null)
    {
        try
        {
            if (TasksLoadFailed)
            {
                // The in-memory list may be missing records we failed to read.
                // Never overwrite the file in that state — the cause is in wdm_error.log.
                LogNonFatal(new InvalidOperationException(
                    "SaveTasks skipped: the initial tasks load failed (" + LastTasksLoadError + "). " +
                    "Fix or delete tasks.json (a tasks.json.bak backup may exist) and restart WDM."));
                return;
            }
            Directory.CreateDirectory(AppDir);
            if (!_sessionBackupTaken)
            {
                _sessionBackupTaken = true;
                try
                {
                    // Atomic-ish snapshot: copy to temp then move, so a crash
                    // mid-copy can never leave a truncated .bak that poisons
                    // the next startup's TryRestoreBackup.
                    if (File.Exists(TasksPath))
                    {
                        string tmp = TasksBackupPath + $".snap-{Guid.NewGuid():N}.tmp";
                        File.Copy(TasksPath, tmp, overwrite: false);
                        try { File.Move(tmp, TasksBackupPath, overwrite: true); }
                        catch { try { File.Delete(tmp); } catch { } throw; }
                    }
                }
                catch { }
            }
            var records = tasks.Select(t => new TaskRecord
            {
                Id = t.Id,
                Url = t.Url,
                SourcePageUrl = t.SourcePageUrl,
                Referer = t.Referer,
                Headers = t.Headers,
                Mirrors = t.Mirrors?.ToList() ?? new(),
                Etag = t.Etag,
                LastModified = t.LastModified,
                FileName = t.FileName,
                SaveFolder = t.SaveFolder,
                ChunkCount = t.ChunkCount,
                Status = t.Status,
                TotalBytes = t.TotalBytes,
                DownloadedBytes = t.DownloadedBytes,
                Progress = t.Progress,
                SpeedLimitKbps = t.SpeedLimitKbps,
                Priority = t.Priority,
                Category = t.Category,
                Checksum = t.Checksum,
                Error = t.Error,
                AddedAt = t.AddedAt,
                CompletedAt = t.CompletedAt,
                IsYouTube = t.IsYouTube,
                YouTubeFormatArg = t.YouTubeFormatArg,
                YouTubeExtraArgs = t.YouTubeExtraArgs,
                YouTubeVideoId = t.YouTubeVideoId,
                ThumbnailUrl = t.ThumbnailUrl,
                Segments = segmentProvider?.Invoke(t),
            }).ToList();
            AtomicFile.Write(TasksPath, JsonSerializer.Serialize(records, JsonOptions));
        }
        catch (Exception ex)
        {
            LogNonFatal(ex);
        }
    }
}

public sealed class TaskRecord
{
    public Guid Id { get; set; } = Guid.Empty;
    public string Url { get; set; } = "";
    public string? SourcePageUrl { get; set; }
    public string? Referer { get; set; }
    public Dictionary<string, string> Headers { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> Mirrors { get; set; } = new();
    public string? Etag { get; set; }
    public string? LastModified { get; set; }
    public string FileName { get; set; } = "";
    public string SaveFolder { get; set; } = "";
    public int ChunkCount { get; set; } = 0;
    public TaskStatus Status { get; set; }
    public long TotalBytes { get; set; } = -1;
    public long DownloadedBytes { get; set; }
    public int Progress { get; set; }
    public long SpeedLimitKbps { get; set; }
    public PriorityLevel Priority { get; set; } = PriorityLevel.Normal;
    public DownloadCategory Category { get; set; } = DownloadCategory.Other;
    public string? Checksum { get; set; }
    public string? Error { get; set; }
    public DateTime AddedAt { get; set; } = DateTime.Now;
    public DateTime? CompletedAt { get; set; }
    public bool IsYouTube { get; set; }
    public string? YouTubeFormatArg { get; set; }
    public string? YouTubeExtraArgs { get; set; }
    public string? YouTubeVideoId { get; set; }
    public string? ThumbnailUrl { get; set; }
    /// <summary>Per-chunk completion snapshot (1DM ThreadInfo equivalent). Missing
    /// in old files — tolerated (sidecar remains the resume source).</summary>
    public List<SegmentRecord>? Segments { get; set; }
}

/// <summary>One chunk's resume state: byte range plus completion flag.</summary>
public sealed class SegmentRecord
{
    public int Index { get; set; }
    public long Start { get; set; }
    public long End { get; set; }
    public bool Done { get; set; }
}
