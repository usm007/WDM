using System;
using System.Collections.Generic;
using System.IO;
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
    /// <summary>Second-run Tips window (repurposed WelcomeWindow): shown once on
    /// the run after first-ever run. First run opens the hosted setup guide.</summary>
    public bool HasSeenTipsWindow { get; set; } = false;
    public bool MinimizeToTray { get; set; } = true;
    public bool NotifyOnCompletion { get; set; } = true;
    /// <summary>When false, new downloads start directly without showing the
    /// Add Download dialog (browser captures, second instances and valid URLs
    /// start silently with default folder/settings).</summary>
    public bool ShowAddDialog { get; set; } = true;
    /// <summary>When false, starting a download does not auto-open the
    /// per-download progress dialog. The task still runs in the list.</summary>
    public bool ShowProgressDialog { get; set; } = true;
    /// <summary>When false, the Download Complete popup is suppressed
    /// (tray balloon/sound still follow their own toggles).</summary>
    public bool ShowCompleteDialog { get; set; } = true;
    public bool ShowTrayProgress { get; set; } = false;
    public double? ProgressPanelLeft { get; set; }
    public double? ProgressPanelTop { get; set; }
    /// <summary>Autostart on Windows logon. ON by default for fresh installs;
    /// the app self-heals the Run entry at startup (missing/empty/orphaned
    /// targets are rewritten, never left behind).</summary>
    public bool RunAtStartup { get; set; } = true;
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
    /// the gate (catch everything). Unknown sizes pass unless the file type
    /// proves it small (images, pages, styles, fonts).</summary>
    public long MinCatchSizeBytes { get; set; } = 200L * 1024 * 1024;    /// <summary>1DM <c>always_retry_download</c> (default OFF), bounded by
    /// <see cref="MaxRetries"/> per task: a timer re-queues Failed tasks until
    /// their per-task budget is spent. User Pause/Cancel/Remove always wins.</summary>
    public bool AutoResumeFailed { get; set; } = false;

    // Notifications: completion + errors + sound only.
    public bool NotifyOnError { get; set; } = true;
    public bool NotificationSound { get; set; } = true;

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

    // Proxy (manual HTTP/HTTPS proxy, IDM-style; .NET has no SOCKS support)
    public bool ProxyEnabled { get; set; } = false;
    public string ProxyHost { get; set; } = "";
    public int ProxyPort { get; set; } = 8080;
    public string? ProxyUsername { get; set; }
    /// <summary>Stored in plaintext in settings.json (same tradeoff as IDM —
    /// a per-user file under %LocalAppData%).</summary>
    public string? ProxyPassword { get; set; }

    /// <summary>Per-site full-session replay approvals (page-host cookies may
    /// ride to third-party CDNs for these hosts only). Shown with a warning
    /// wherever edited; default-deny everywhere else.</summary>
    public List<string> FullSessionReplayHosts { get; set; } = new();

    // Per-host connection policy (IDM per-site exceptions): user caps
    // (host key "scheme://host:port" → max connections 1..32) plus learned
    // cooldowns (origin → until + cause) persisted across restarts so a
    // 429-burned host stays backed off after relaunch. Fragile hosts
    // auto-degrade to 1 connection on repeated integrity faults.
    public Dictionary<string, int> HostConnectionLimits { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, HostCooldown> HostCooldowns { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public sealed class HostCooldown
    {
        public long UntilUnix { get; set; }
        public string Cause { get; set; } = "";
    }

    // Post-download
    public bool ComputeChecksum { get; set; }
    public string? PostDownloadScript { get; set; }

    // Post-download automation (1DM pref_automation.xml, minus wifi-off: no mobile radio)
    public int DeleteFinishedLinksAfterDays { get; set; } = 0;
}
