using System.Diagnostics;
using System.Net.Http;
using Velopack;
using Velopack.Sources;

namespace WDM.Services;

/// <summary>
/// Velopack delta-update service for WDM.
/// Uses GitHub Releases as the update feed (nupkg + RELEASES assets).
/// When the app is NOT installed via Velopack (dev build, Inno-only portable), all
/// methods gracefully fall back to <see cref="UpdateChecker"/> full-installer flow.
/// Update policy: 1 version behind → delta nupkg only (~0.2-5 MB);
/// 2+ versions behind → self-contained full nupkg (with .NET, ~70 MB+).
/// Full Setup.exe / portable zip are for new users only and are never auto-downloaded.
/// </summary>
public static class VelopackUpdateService
{
    private const string RepoUrl = "https://github.com/usm007/WDM";

    /// <summary>Which package Velopack will download for an update.</summary>
    public enum UpdatePackageKind
    {
        Delta,
        Full,
    }

    /// <summary>True when running from a Velopack-installed location (not dev/Inno portable).</summary>
    public static bool IsVelopackInstalled
    {
        get
        {
            try
            {
                var mgr = CreateManager();
                return mgr.IsInstalled;
            }
            catch { return false; }
        }
    }

    /// <summary>Current version as Velopack sees it, or assembly version as fallback.</summary>
    public static Version CurrentVersion
    {
        get
        {
            try
            {
                var mgr = CreateManager();
                if (mgr.IsInstalled && mgr.CurrentVersion is not null)
                {
                    // mgr.CurrentVersion is NuGet.Versioning.SemanticVersion; convert to System.Version
                    string s = mgr.CurrentVersion.ToString();
                    if (Version.TryParse(s, out var v)) return v;
                    // fallback via Major.Minor.Patch
                    try { return new Version(mgr.CurrentVersion.Major, mgr.CurrentVersion.Minor, mgr.CurrentVersion.Patch, 0); } catch { }
                }
            }
            catch { }
            return UpdateChecker.CurrentVersion;
        }
    }

    /// <summary>True when the running copy bundles the .NET runtime (self-contained
    /// publish). Framework-dependent copies have no coreclr/System.Private.CoreLib
    /// next to the exe — those come from the shared runtime instead.</summary>
    public static bool IsSelfContainedInstall
    {
        get
        {
            try
            {
                string dir = AppContext.BaseDirectory;
                return File.Exists(Path.Combine(dir, "coreclr.dll"))
                    || File.Exists(Path.Combine(dir, "System.Private.CoreLib.dll"));
            }
            catch { return false; }
        }
    }

    /// <summary>
    /// Refuses a cross-flavor update: a framework-only package applied over a
    /// self-contained install would delete the bundled .NET runtime during
    /// Velopack's obsolete-file removal and leave a broken app behind.
    /// A self-contained full (~70+ MB) can never be confused with a
    /// framework-only one (~7 MB), so size is a reliable discriminator.
    /// </summary>
    private static void ThrowIfFeedFlavorMismatch(UpdateInfo update)
    {
        const long MinSelfContainedFullBytes = 20_000_000;
        var full = update.TargetFullRelease;
        if (IsSelfContainedInstall && full is not null && full.Size < MinSelfContainedFullBytes)
        {
            throw new InvalidOperationException(
                $"Update feed mismatch: installed WDM is self-contained (.NET bundled) but the feed " +
                $"offers '{full.FileName}' ({full.Size / 1048576} MB, framework-only, no .NET). " +
                $"Refusing to apply — download the full setup from the release page instead. " +
                $"See wdm_error.log for details.");
        }
    }
    /// <summary>Converts Velopack SemanticVersion to System.Version for ReleaseInfo.</summary>
    public static Version ToSystemVersion(NuGet.Versioning.SemanticVersion semVer)
    {
        if (Version.TryParse(semVer.ToString(), out var v)) return v;
        return new Version(semVer.Major, semVer.Minor, semVer.Patch, 0);
    }

    /// <summary>Pending restart asset if an update has been downloaded but not yet applied.</summary>
    public static VelopackAsset? PendingRestartAsset
    {
        get
        {
            try
            {
                var mgr = CreateManager();
                return mgr.UpdatePendingRestart;
            }
            catch { return null; }
        }
    }

    // Shared HttpClient-based downloader that reuses UpdateChecker's client settings (avoids socket permission issues)
    private sealed class SharedHttpDownloader : IFileDownloader
    {
        private static readonly HttpClient _http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        // File payloads (full nupkg ~70MB) must not share the 30s metadata
        // timeout: a slow link reliably exceeded it mid-download. No global
        // timeout here — cancellation comes from the per-call token below.
        private static readonly HttpClient _fileHttp = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        static SharedHttpDownloader()
        {
            _http.DefaultRequestHeaders.UserAgent.ParseAdd($"WDM/{UpdateChecker.CurrentVersion}");
            _http.DefaultRequestHeaders.Accept.ParseAdd("application/octet-stream");
            _fileHttp.DefaultRequestHeaders.UserAgent.ParseAdd($"WDM/{UpdateChecker.CurrentVersion}");
            _fileHttp.DefaultRequestHeaders.Accept.ParseAdd("application/octet-stream");
        }
        // Velopack feed packages are tens of MB; anything past this is a
        // malicious/oversized feed — abort instead of filling the disk.
        private const long MaxPackageBytes = 500L * 1024 * 1024;
        public async Task DownloadFile(string url, string targetFile, Action<int> progress, IDictionary<string, string>? headers, double timeout, CancellationToken cancelToken)
        {
            // Honor Velopack's per-call timeout (seconds, 0 = none) with a
            // 10-minute ceiling fallback so slow links can finish full nupkgs.
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancelToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeout > 0 ? timeout : 600));
            var ct = timeoutCts.Token;
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            if (headers != null) foreach(var kv in headers) req.Headers.TryAddWithoutValidation(kv.Key, kv.Value);
            using var resp = await _fileHttp.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            long total = resp.Content.Headers.ContentLength ?? -1;
            if (total > MaxPackageBytes)
                throw new InvalidOperationException($"Update package too large ({total} bytes) — refusing download.");
            using var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            Directory.CreateDirectory(Path.GetDirectoryName(targetFile)!);
            using var dst = System.IO.File.Create(targetFile);
            var buf = new byte[81920];
            long read = 0;
            while(true)
            {
                int n = await src.ReadAsync(buf, ct).ConfigureAwait(false);
                if (n <= 0) break;
                read += n;
                if (read > MaxPackageBytes)
                    throw new InvalidOperationException("Update package exceeded size limit — refusing download.");
                await dst.WriteAsync(buf.AsMemory(0, n), ct).ConfigureAwait(false);
                if (total > 0) progress?.Invoke((int)(read * 100 / total));
            }
        }
        private const int MaxFeedBytes = 10 * 1024 * 1024;
        public async Task<byte[]> DownloadBytes(string url, IDictionary<string, string>? headers, double timeout)
        {
            using var timeoutCts = new CancellationTokenSource();
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeout > 0 ? timeout : 30));
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            if (headers != null) foreach(var kv in headers) req.Headers.TryAddWithoutValidation(kv.Key, kv.Value);
            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            await using var src = await resp.Content.ReadAsStreamAsync(timeoutCts.Token).ConfigureAwait(false);
            using var ms = new System.IO.MemoryStream();
            var buf = new byte[81920];
            int n;
            while ((n = await src.ReadAsync(buf, timeoutCts.Token).ConfigureAwait(false)) > 0)
            {
                if (ms.Length + n > MaxFeedBytes)
                    throw new InvalidOperationException("Update feed response too large — refusing download.");
                ms.Write(buf, 0, n);
            }
            return ms.ToArray();
        }
        public async Task<string> DownloadString(string url, IDictionary<string, string>? headers, double timeout)
        {
            // GitHub API needs an explicit JSON Accept when the caller didn't set one.
            IDictionary<string, string>? effective = headers;
            if (headers is null || !headers.Keys.Any(k => k.Equals("Accept", StringComparison.OrdinalIgnoreCase)))
            {
                var merged = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                if (headers != null) foreach (var kv in headers) merged[kv.Key] = kv.Value;
                merged["Accept"] = "application/vnd.github+json";
                effective = merged;
            }
            var bytes = await DownloadBytes(url, effective, timeout).ConfigureAwait(false);
            return System.Text.Encoding.UTF8.GetString(bytes);
        }
    }

    /// <summary>Inspects an update: exactly 1 delta → <see cref="UpdatePackageKind.Delta"/>,
    /// otherwise (0 or 2+ deltas) → <see cref="UpdatePackageKind.Full"/> (self-contained full nupkg).</summary>
    public static UpdatePackageKind GetUpdateKind(UpdateInfo? update)
    {
        try
        {
            if (update?.DeltasToTarget is { Length: 1 })
                return UpdatePackageKind.Delta;
        }
        catch { }
        return UpdatePackageKind.Full;
    }

    /// <summary>True when the update will download only the small delta package (1 version behind).</summary>
    public static bool IsDeltaUpdate(UpdateInfo? update) => GetUpdateKind(update) == UpdatePackageKind.Delta;

    /// <summary>Human-readable size, e.g. 0.17 MB / 72.4 MB.</summary>
    public static string FormatSizeMb(long bytes) => $"{bytes / 1048576.0:F2} MB";

    /// <summary>Short label for UI: "Delta (~X MB)" or "Full (~Y MB, .NET included)".
    /// Full nupkg is self-contained (bundles .NET) — never the small framework-only package.</summary>
    public static string DescribeUpdate(UpdateInfo update)
    {
        try
        {
            if (IsDeltaUpdate(update))
            {
                long deltaBytes = update.DeltasToTarget[0]?.Size ?? 0;
                return deltaBytes > 0 ? $"Delta (~{FormatSizeMb(deltaBytes)})" : "Delta (patch-only)";
            }
            long fullBytes = update.TargetFullRelease?.Size ?? 0;
            return fullBytes > 0 ? $"Full (~{FormatSizeMb(fullBytes)}, .NET included)" : "Full (with .NET)";
        }
        catch { return "update package"; }
    }

    private static UpdateManager CreateManager()
    {
        var downloader = new SharedHttpDownloader();
        var source = new GithubSource(RepoUrl, accessToken: null, prerelease: false, downloader: downloader);
        var options = new UpdateOptions
        {
            AllowVersionDowngrade = false,
            // 1 version behind → delta only; 2+ behind → self-contained full nupkg.
            MaximumDeltasBeforeFallback = 1,
        };
        return new UpdateManager(source, options);
    }

    /// <summary>
    /// Checks GitHub for an update via Velopack feed. Returns null if not installed via Velopack,
    /// no update available, or network fails. Caller should fall back to <see cref="UpdateChecker.CheckLatestAsync"/>.
    /// </summary>
    public static async Task<UpdateInfo?> CheckForUpdatesAsync(CancellationToken ct = default)
    {
        try
        {
            var mgr = CreateManager();
            if (!mgr.IsInstalled)
                return null;

            // Velopack's check has no CancellationToken overload — run it on
            // the pool so the caller's token can at least abort the wait
            // instead of hanging the UI shutdown path.
            ct.ThrowIfCancellationRequested();
            var info = await Task.Run(() => mgr.CheckForUpdatesAsync(), ct).ConfigureAwait(false);
            if (info is null)
                return null;
            return info;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Fallback check that works even when IsInstalled check is flaky — uses TestVelopackLocator to query GitHub directly.
    /// Allows Velopack-installed users to find delta even if local locator fails.
    /// </summary>
    public static async Task<UpdateInfo?> CheckForUpdatesAnyAsync(CancellationToken ct = default)
    {
        // Try normal first
        var normal = await CheckForUpdatesAsync(ct).ConfigureAwait(false);
        if (normal != null) return normal;

        // Fallback: use Test locator with current assembly version to query GitHub feed directly.
        // Velopack versions are 3-part (2.7.2) while the assembly is 4-part (2.7.2.0) — normalize.
        // Build can be -1 (undefined) for 2-part versions; Revision is dropped (feed is 3-part).
        var asm = UpdateChecker.CurrentVersion;
        int build = asm.Build < 0 ? 0 : asm.Build;
        var currentVer = $"{asm.Major}.{asm.Minor}.{build}";
        var tempDir = Path.Combine(Path.GetTempPath(), "WDM_Velopack_Check");
        try
        {
            Directory.CreateDirectory(tempDir);
            var locator = new Velopack.Locators.TestVelopackLocator("WDM", currentVer, tempDir, null);
            var downloader = new SharedHttpDownloader();
            var source = new GithubSource(RepoUrl, null, false, downloader);
            var options = new UpdateOptions { MaximumDeltasBeforeFallback = 1 };
            var mgr = new UpdateManager(source, options, locator);
            ct.ThrowIfCancellationRequested();
            var info = await Task.Run(() => mgr.CheckForUpdatesAsync(), ct).ConfigureAwait(false);
            return info;
        }
        catch
        {
            return null;
        }
        finally
        {
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    /// <summary>
    /// Downloads the delta/full packages for the given <paramref name="update"/> and
    /// reports integer progress 0..100. No-op when not Velopack-installed.
    /// </summary>
    public static async Task DownloadUpdatesAsync(UpdateInfo update, Action<int>? onProgress = null, CancellationToken ct = default)
    {
        var mgr = CreateManager();
        if (!mgr.IsInstalled)
            throw new InvalidOperationException("Velopack is not installed — cannot download delta updates. Use full installer fallback.");

        ThrowIfFeedFlavorMismatch(update);
        await mgr.DownloadUpdatesAsync(update, onProgress, ct).ConfigureAwait(false);
    }

    /// <summary>Applies the pending update and restarts WDM. Must be called after <see cref="DownloadUpdatesAsync"/>.</summary>
    public static void ApplyAndRestart(VelopackAsset asset, string[]? restartArgs = null)
    {
        // BUG-038: signal the main window to set _exiting before the restart
        // so the MinimizeToTray guard doesn't cancel the shutdown.
        ViewModels.MainViewModel.Restarting?.Invoke();
        var mgr = CreateManager();
        mgr.ApplyUpdatesAndRestart(asset, restartArgs ?? Array.Empty<string>());
    }

    /// <summary>Applies pending update and exits without restart (Velopack helper will restart).</summary>
    public static void ApplyAndExit(VelopackAsset asset)
    {
        var mgr = CreateManager();
        mgr.ApplyUpdatesAndExit(asset);
    }

    /// <summary>Wait for WDM to exit, then apply (used by installer hooks). </summary>
    public static void WaitExitThenApply(VelopackAsset asset, bool silent = true, bool restart = true)
    {
        var mgr = CreateManager();
        mgr.WaitExitThenApplyUpdates(asset, silent, restart);
    }

    /// <summary>One-shot helper: check + download. Returns the asset to apply, or null.</summary>
    public static async Task<VelopackAsset?> CheckAndDownloadAsync(Action<int>? onProgress = null, CancellationToken ct = default)
    {
        var update = await CheckForUpdatesAsync(ct).ConfigureAwait(false);
        if (update is null)
            return null;
        if (update.TargetFullRelease is null)
            return null;

        await DownloadUpdatesAsync(update, onProgress, ct).ConfigureAwait(false);
        return update.TargetFullRelease;
    }
}
