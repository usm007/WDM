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
/// Delta packages are ~1-5 MB vs full ~60 MB and apply silently without wizard.
/// </summary>
public static class VelopackUpdateService
{
    private const string RepoUrl = "https://github.com/usm007/WDM";

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
        static SharedHttpDownloader()
        {
            _http.DefaultRequestHeaders.UserAgent.ParseAdd($"WDM/{UpdateChecker.CurrentVersion}");
            _http.DefaultRequestHeaders.Accept.ParseAdd("application/octet-stream");
        }
        public void DownloadFile(string url, string targetFile, Action<int> progress, IDictionary<string, string> headers, double timeout, CancellationToken cancelToken)
        {
            DownloadFileAsync(url, targetFile, progress, headers, timeout, cancelToken).GetAwaiter().GetResult();
        }
        private async Task DownloadFileAsync(string url, string targetFile, Action<int> progress, IDictionary<string, string> headers, double timeout, CancellationToken cancelToken)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            if (headers != null) foreach(var kv in headers) req.Headers.TryAddWithoutValidation(kv.Key, kv.Value);
            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancelToken).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            long total = resp.Content.Headers.ContentLength ?? -1;
            using var src = await resp.Content.ReadAsStreamAsync(cancelToken).ConfigureAwait(false);
            using var dst = System.IO.File.Create(targetFile);
            var buf = new byte[81920];
            long read = 0;
            while(true)
            {
                int n = await src.ReadAsync(buf, cancelToken).ConfigureAwait(false);
                if (n <= 0) break;
                await dst.WriteAsync(buf.AsMemory(0, n), cancelToken).ConfigureAwait(false);
                read += n;
                if (total > 0) progress?.Invoke((int)(read * 100 / total));
            }
        }
        public byte[] DownloadBytes(string url, IDictionary<string, string> headers, double timeout)
        {
            return DownloadBytesAsync(url, headers, timeout).GetAwaiter().GetResult();
        }
        private async Task<byte[]> DownloadBytesAsync(string url, IDictionary<string, string> headers, double timeout)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            if (headers != null) foreach(var kv in headers) req.Headers.TryAddWithoutValidation(kv.Key, kv.Value);
            using var resp = await _http.SendAsync(req).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            return await resp.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
        }
        public string DownloadString(string url, IDictionary<string, string> headers, double timeout)
        {
            return DownloadStringAsync(url, headers, timeout).GetAwaiter().GetResult();
        }
        private async Task<string> DownloadStringAsync(string url, IDictionary<string, string> headers, double timeout)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            if (headers != null) foreach(var kv in headers) req.Headers.TryAddWithoutValidation(kv.Key, kv.Value);
            // GitHub API needs Accept header
            if (!req.Headers.Contains("Accept")) req.Headers.TryAddWithoutValidation("Accept", "application/vnd.github+json");
            req.Headers.UserAgent.ParseAdd($"WDM/{UpdateChecker.CurrentVersion}");
            using var resp = await _http.SendAsync(req).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            return await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
        }
    }

    private static UpdateManager CreateManager()
    {
        var downloader = new SharedHttpDownloader();
        var source = new GithubSource(RepoUrl, accessToken: null, prerelease: false, downloader: downloader);
        var options = new UpdateOptions
        {
            AllowVersionDowngrade = false,
            MaximumDeltasBeforeFallback = 10,
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

            var info = await mgr.CheckForUpdatesAsync().ConfigureAwait(false);
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
        try
        {
            // Try normal first
            var normal = await CheckForUpdatesAsync(ct).ConfigureAwait(false);
            if (normal != null) return normal;

            // Fallback: use Test locator with current assembly version to query GitHub feed directly
            var currentVer = UpdateChecker.CurrentVersion.ToString();
            var tempDir = Path.Combine(Path.GetTempPath(), "WDM_Velopack_Check");
            Directory.CreateDirectory(tempDir);
            var locator = new Velopack.Locators.TestVelopackLocator("WDM", currentVer, tempDir, null);
            var source = new GithubSource(RepoUrl, null, false);
            var options = new UpdateOptions { MaximumDeltasBeforeFallback = 10 };
            var mgr = new UpdateManager(source, options, locator);
            var info = await mgr.CheckForUpdatesAsync().ConfigureAwait(false);
            return info;
        }
        catch
        {
            return null;
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

        await mgr.DownloadUpdatesAsync(update, onProgress, ct).ConfigureAwait(false);
    }

    /// <summary>Applies the pending update and restarts WDM. Must be called after <see cref="DownloadUpdatesAsync"/>.</summary>
    public static void ApplyAndRestart(VelopackAsset asset, string[]? restartArgs = null)
    {
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
