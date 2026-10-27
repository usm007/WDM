using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace WDM.Services;

/// <summary>Details about the newest GitHub release.</summary>
public sealed record ReleaseInfo(string TagName, Version? Version, string Name, string Url, string? Body, DateTime? PublishedAt, string? InstallerUrl, string? UpdatePackageUrl = null, string? InstallerSha256 = null);

/// <summary>Queries the GitHub releases API for WDM and compares against the running
/// version. Used for the manual "Check now" button in Settings and the automatic
/// startup check.</summary>
public static class UpdateChecker
{
    private const string RepositoryOwner = "usm007";
    private const string RepositoryName = "WDM";
    private const string LatestReleaseApi = $"https://api.github.com/repos/{RepositoryOwner}/{RepositoryName}/releases/latest";
    private const string ReleasesPage = $"https://github.com/{RepositoryOwner}/{RepositoryName}/releases";

    public static Version CurrentVersion =>
        System.Reflection.Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0, 0, 0);

    /// <summary>Display version for UI (About). Test installers
    /// (build-test-install.ps1) bake their lettered tag (e.g. 2.8.2a) as
    /// assembly metadata via -p:WdmTestTag; release builds show M.m.b.</summary>
    public static string DisplayVersion
    {
        get
        {
            try
            {
                foreach (var attr in System.Reflection.Assembly.GetExecutingAssembly()
                    .GetCustomAttributes(typeof(System.Reflection.AssemblyMetadataAttribute), false))
                {
                    if (attr is System.Reflection.AssemblyMetadataAttribute meta &&
                        meta.Key == "WdmTestTag" && !string.IsNullOrWhiteSpace(meta.Value))
                        return meta.Value.Trim();
                }
            }
            catch { }
            var v = CurrentVersion;
            return $"{v.Major}.{v.Minor}.{v.Build}";
        }
    }

    /// <summary>Shared HttpClient: creating one per call causes socket exhaustion under
    /// repeated update checks. Headers are set once at construction time.</summary>
    private static readonly HttpClient _http = CreateHttpClient();

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"WDM/{CurrentVersion}");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    /// <summary>Fetches the latest release. Returns null when the repo is unreachable,
    /// the request fails, or there is no tagged release yet.</summary>
    public static async Task<ReleaseInfo?> CheckLatestAsync(CancellationToken ct = default)
    {
        try
        {
            string json = await _http.GetStringAsync(LatestReleaseApi, ct);

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            string? tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() : null;
            string? name = root.TryGetProperty("name", out var n) ? n.GetString() : null;
            string? url = root.TryGetProperty("html_url", out var u) ? u.GetString() : null;
            string? body = root.TryGetProperty("body", out var b) ? b.GetString() : null;
            DateTime? published = root.TryGetProperty("published_at", out var p) && p.TryGetDateTime(out var dt) ? dt : null;
            string? installerUrl = FindInstallerUrl(root);
            string? updatePackageUrl = FindUpdatePackageUrl(root);
            if (string.IsNullOrWhiteSpace(tag) || string.IsNullOrWhiteSpace(url))
                return null;

            string? installerSha256 = null;
            if (!string.IsNullOrWhiteSpace(installerUrl))
            {
                try { installerSha256 = await FindInstallerHashAsync(root, installerUrl, ct); }
                catch { installerSha256 = null; }
            }

            return new ReleaseInfo(tag, ParseVersion(tag), name ?? tag, url, body, published, installerUrl, updatePackageUrl, installerSha256);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Picks the browser_download_url of the WDM installer .exe from the release assets.
    /// Prefers the Inno wizard (WDM-Setup-, per-machine) for the GitHub fallback path:
    /// non-Velopack installs must never auto-switch to the Velopack per-user one-click.
    /// Velopack users never reach here (delta nupkg via VelopackUpdateService).</summary>
    private static string? FindInstallerUrl(JsonElement release)
    {
        if (!release.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
            return null;

        // A stale installer accidentally left on a newer release must not be
        // downloaded and run (it would install an older build than the tag).
        Version? tagVersion = release.TryGetProperty("tag_name", out var tagEl) && tagEl.GetString() is string tag
            ? ParseVersion(tag)
            : null;

        string? innoMatch = null;
        string? legacyInnoMatch = null;
        string? fallback = null;
        foreach (var asset in assets.EnumerateArray())
        {
            if (!asset.TryGetProperty("name", out var n) || n.GetString() is not string name)
                continue;
            if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                continue;
            if (!IsInstallerAssetCompatible(name, tagVersion))
                continue;
            // Only our own setup assets, not any random .exe.
            // Inno wizard: WDM-Setup-2.8.5.exe (new) and WDM_Setup_x.exe (legacy local builds).
            // Velopack one-click (per-user, no wizard): WDM-User-Setup-, WDM-Full-Setup- (legacy
            // misleading name for the same Velopack Setup.exe), WDM-win-Setup.exe.
            bool isInno = name.StartsWith("WDM-Setup-", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("WDM_Setup_", StringComparison.OrdinalIgnoreCase);
            bool isVelopackSetup = name.StartsWith("WDM-User-Setup", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("WDM-Full-Setup", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("WDM-Velopack", StringComparison.OrdinalIgnoreCase)
                || name.Equals("WDM-win-Setup.exe", StringComparison.OrdinalIgnoreCase);
            bool isGenericSetup = !isInno && !isVelopackSetup
                && name.StartsWith("WDM", StringComparison.OrdinalIgnoreCase)
                && name.Contains("Setup", StringComparison.OrdinalIgnoreCase);
            if (!isInno && !isVelopackSetup && !isGenericSetup)
                continue;
            if (asset.TryGetProperty("browser_download_url", out var u) &&
                u.GetString() is string dl && IsTrustedDownloadUrl(dl))
            {
                if (isInno && name.StartsWith("WDM-Setup-", StringComparison.OrdinalIgnoreCase))
                    innoMatch ??= dl;
                else if (isInno)
                    legacyInnoMatch ??= dl;
                else
                    fallback ??= dl;
            }
        }
        return innoMatch ?? legacyInnoMatch ?? fallback;
    }

    /// <summary>Picks the update package (.nupkg delta/full) for Velopack.</summary>
    private static string? FindUpdatePackageUrl(JsonElement release)
    {
        if (!release.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
            return null;

        string? deltaUrl = null;
        string? fullUrl = null;
        foreach (var asset in assets.EnumerateArray())
        {
            if (!asset.TryGetProperty("name", out var n) || n.GetString() is not string name)
                continue;
            if (name.EndsWith("-delta.nupkg", StringComparison.OrdinalIgnoreCase)
                && asset.TryGetProperty("browser_download_url", out var u))
            {
                deltaUrl = u.GetString();
            }
            else if (name.EndsWith("-full.nupkg", StringComparison.OrdinalIgnoreCase)
                && asset.TryGetProperty("browser_download_url", out var u2))
            {
                fullUrl = u2.GetString();
            }
            else if (name.EndsWith(".nupkg", StringComparison.OrdinalIgnoreCase)
                && asset.TryGetProperty("browser_download_url", out var u3) && fullUrl == null)
            {
                fullUrl = u3.GetString();
            }
        }
        return deltaUrl ?? fullUrl;
    }

    /// <summary>Looks for a published SHA-256 for the installer: an adjacent
    /// <c>&lt;installer&gt;.sha256</c> asset first, then a <c>SHA256SUMS.txt</c> /
    /// <c>checksums.txt</c> style asset containing a line for the installer file.
    /// Returns null when the release publishes no usable hash (fail-open: the
    /// PE check in <see cref="VerifyInstallerIntegrity"/> still applies).</summary>
    private static async Task<string?> FindInstallerHashAsync(JsonElement release, string installerUrl, CancellationToken ct)
    {
        if (!release.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
            return null;
        string installerName;
        try { installerName = Path.GetFileName(new Uri(installerUrl).LocalPath); }
        catch { return null; }
        if (string.IsNullOrWhiteSpace(installerName))
            return null;

        string? directUrl = null;
        string? sumsUrl = null;
        foreach (var asset in assets.EnumerateArray())
        {
            if (!asset.TryGetProperty("name", out var n) || n.GetString() is not string name)
                continue;
            if (!asset.TryGetProperty("browser_download_url", out var u) || u.GetString() is not string dl)
                continue;
            if (!IsTrustedDownloadUrl(dl))
                continue;
            if (name.Equals(installerName + ".sha256", StringComparison.OrdinalIgnoreCase) ||
                name.Equals(installerName + ".sha256sum", StringComparison.OrdinalIgnoreCase))
            {
                directUrl = dl;
                break;
            }
            if (sumsUrl is null && (name.Equals("SHA256SUMS.txt", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("checksums.txt", StringComparison.OrdinalIgnoreCase) ||
                name.EndsWith(".sha256sums", StringComparison.OrdinalIgnoreCase)))
            {
                sumsUrl = dl;
            }
        }

        // strict: in multi-file listings a token must EQUAL the installer name
        // (substring Contains matched sibling versions); a lone hash is only
        // accepted from the dedicated single-file sidecar (BUG-024).
        static string? ExtractHash(string text, string fileName, bool singleFile)
        {
            foreach (string rawLine in text.Split('\n'))
            {
                string line = rawLine.Trim().Trim('*', ' ', '\r');
                var m = Regex.Match(line, @"\b([0-9a-fA-F]{64})\b");
                if (!m.Success)
                    continue;
                string[] tokens = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (tokens.Any(t => t.Trim('*').Equals(fileName, StringComparison.OrdinalIgnoreCase)))
                    return m.Groups[1].Value.ToLowerInvariant();
                if (singleFile && tokens.Length == 1)
                    return m.Groups[1].Value.ToLowerInvariant();
            }
            return null;
        }

        // Hash sidecars must never stall the update check: dedicated short
        // budget instead of the shared 8s API client timeout (BUG-024).
        using var hashCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        hashCts.CancelAfter(TimeSpan.FromSeconds(5));
        var hct = hashCts.Token;

        async Task<string?> DownloadSmallAsync(string url)
        {
            using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, hct);
            response.EnsureSuccessStatusCode();
            if ((response.Content.Headers.ContentLength ?? 0) > 1024 * 1024)
                return null;
            using var stream = await response.Content.ReadAsStreamAsync(hct);
            using var ms = new MemoryStream();
            var tmp = new byte[32768];
            int total = 0, n;
            while ((n = await stream.ReadAsync(tmp.AsMemory(0, tmp.Length), hct)) > 0)
            {
                total += n;
                if (total > 1024 * 1024)
                    return null;
                ms.Write(tmp, 0, n);
            }
            return Encoding.UTF8.GetString(ms.ToArray());
        }

        if (directUrl is not null)
        {
            try
            {
                string? text = await DownloadSmallAsync(directUrl);
                if (text is not null)
                {
                    string? hash = ExtractHash(text, installerName, singleFile: true);
                    if (hash is not null)
                        return hash;
                }
            }
            catch (OperationCanceledException) { }
            catch { }
        }
        if (sumsUrl is not null)
        {
            try
            {
                string? text = await DownloadSmallAsync(sumsUrl);
                if (text is not null)
                    return ExtractHash(text, installerName, singleFile: false);
            }
            catch (OperationCanceledException) { }
            catch { }
        }
        return null;
    }

    /// <summary>Downloads the latest installer to the temp folder and returns its path.
    /// <paramref name="onProgress"/> reports 0..1 as bytes arrive, or -1 when the
    /// server declares no Content-Length (callers show an indeterminate bar).
    /// Rejects truncated bodies, untrusted redirect targets, and non-PE bytes.</summary>
    public static async Task<string> DownloadInstallerAsync(ReleaseInfo release, Action<double>? onProgress = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(release.InstallerUrl) || !IsTrustedDownloadUrl(release.InstallerUrl))
            throw new UpdateException("The latest release has no trusted installer asset.");

        // Reclaim disk from abandoned downloads (GUID names accumulate after
        // successful updates because the installer runs after WDM exits).
        CleanStaleInstallerDownloads(Path.GetTempPath(), DateTime.UtcNow, TimeSpan.FromDays(1));

        string fileName = $"WDM-Setup-{release.Version}_{Guid.NewGuid():N}.exe";
        string target = Path.Combine(Path.GetTempPath(), fileName);
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd($"WDM/{CurrentVersion}");

            using var response = await http.GetAsync(release.InstallerUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();

            // GitHub redirects release assets to objects.githubusercontent.com.
            // Re-check the final hop so a compromised/misconfigured redirect can
            // never make WDM execute bytes from an unrelated host.
            Uri? finalUri = response.RequestMessage?.RequestUri;
            if (finalUri is null || !IsTrustedDownloadUrl(finalUri.ToString()))
                throw new UpdateException("The installer download was redirected to an untrusted host: refusing to run it.");

            long total = response.Content.Headers.ContentLength ?? -1;
            if (total > MaxInstallerBytes)
                throw new UpdateException("Installer too large: refusing download.");

            using var source = await response.Content.ReadAsStreamAsync(ct);
            await CopyInstallerBodyAsync(source, target, total, onProgress, ct);

            VerifyInstallerIntegrity(target, release.InstallerSha256);
            return target;
        }
        catch
        {
            try { if (File.Exists(target)) File.Delete(target); } catch { }
            throw;
        }
    }

    private const long MaxInstallerBytes = 500L * 1024 * 1024;

    /// <summary>Copies an installer body to disk, refusing to return a partial
    /// file. When the server declared a length, the byte count must match
    /// exactly; without one, the caller still gets PE + optional SHA-256 checks
    /// from <see cref="VerifyInstallerIntegrity"/>.</summary>
    internal static async Task CopyInstallerBodyAsync(Stream source, string target, long declaredTotal, Action<double>? onProgress, CancellationToken ct)
    {
        long read = 0;
        int lastPct = -1;
        bool reportedUnknown = false;
        try
        {
            await using var file = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true);
            var buffer = new byte[81920];
            while (true)
            {
                int n = await source.ReadAsync(buffer, ct);
                if (n <= 0)
                    break;
                await file.WriteAsync(buffer.AsMemory(0, n), ct);
                read += n;
                if (read > MaxInstallerBytes)
                    throw new UpdateException("Installer exceeded size limit during download.");

                if (declaredTotal > 0)
                {
                    int pct = (int)(read * 100 / declaredTotal);
                    if (pct != lastPct)
                    {
                        lastPct = pct;
                        onProgress?.Invoke((double)read / declaredTotal);
                    }
                }
                else if (!reportedUnknown)
                {
                    // Unknown total: tell the UI once to switch to an
                    // indeterminate bar instead of marshalling an update per
                    // 80 KB chunk on large installers.
                    reportedUnknown = true;
                    onProgress?.Invoke(-1);
                }
            }
            await file.FlushAsync(ct);
            await file.DisposeAsync();

            if (declaredTotal >= 0 && read != declaredTotal)
                throw new UpdateException(
                    $"Downloaded installer is incomplete ({read} of {declaredTotal} bytes): refusing to run it. " +
                    "Check your connection and retry the update.");
        }
        catch
        {
            try { if (File.Exists(target)) File.Delete(target); } catch { }
            throw;
        }
    }

    /// <summary>Deletes abandoned WDM installer downloads older than
    /// <paramref name="maxAge"/>. The installer must outlive WDM's exit, so a
    /// just-downloaded file is never touched.</summary>
    internal static int CleanStaleInstallerDownloads(string directory, DateTime nowUtc, TimeSpan maxAge)
    {
        int deleted = 0;
        try
        {
            if (!Directory.Exists(directory))
                return 0;
            foreach (string path in Directory.EnumerateFiles(directory, "WDM-Setup-*.exe"))
            {
                try
                {
                    if (nowUtc - File.GetLastWriteTimeUtc(path) < maxAge)
                        continue;
                    File.Delete(path);
                    deleted++;
                }
                catch { }
            }
        }
        catch { }
        return deleted;
    }

    /// <summary>UI text for a known download progress value. Negative means the
    /// server did not declare a size.</summary>
    internal static string FormatDownloadProgress(double progress)
    {
        if (progress < 0)
            return "Downloading…";
        int pct = (int)Math.Round(Math.Clamp(progress, 0, 1) * 100, MidpointRounding.AwayFromZero);
        return $"{pct}%";
    }

    /// <summary>True when an auto-check may be skipped. A timestamp in the
    /// future (clock moved back, copied settings) is treated as stale instead
    /// of suppressing checks until real time catches up.</summary>
    internal static bool ShouldSkipAutoCheck(DateTime? lastCheckUtc, DateTime nowUtc)
    {
        if (lastCheckUtc is not { } last)
            return false;
        DateTime lastUtc = last.Kind switch
        {
            DateTimeKind.Utc => last,
            DateTimeKind.Local => last.ToUniversalTime(),
            _ => DateTime.SpecifyKind(last, DateTimeKind.Utc),
        };
        TimeSpan elapsed = nowUtc - lastUtc;
        return elapsed >= TimeSpan.Zero && elapsed < TimeSpan.FromMinutes(15);
    }

    /// <summary>True when a setup asset's file name does not contradict the
    /// release tag. Prevents a stale installer accidentally left on a newer
    /// release from being downloaded and run. Unversioned names are accepted
    /// (WDM-win-Setup.exe); a 4-part legacy name compares only M.m.b.</summary>
    internal static bool IsInstallerAssetCompatible(string assetName, Version? releaseVersion)
    {
        if (releaseVersion is null)
            return true;
        var m = Regex.Match(assetName, @"(?<!\d)(\d{1,5}\.\d{1,5}(?:\.\d{1,5}){0,2})(?!\d)");
        if (!m.Success || !Version.TryParse(m.Groups[1].Value, out var asset))
            return true;
        int releaseBuild = releaseVersion.Build < 0 ? 0 : releaseVersion.Build;
        if (asset.Major != releaseVersion.Major || asset.Minor != releaseVersion.Minor)
            return false;
        return asset.Build < 0 || asset.Build == releaseBuild;
    }

    /// <summary>The release shape this host may act on. A Velopack install
    /// must never be pointed at the per-machine Inno wizard: doing so creates
    /// a second copy and strands the user on two diverging installs.</summary>
    internal static ReleaseInfo ForHostFlavor(bool velopackInstalled, ReleaseInfo release)
        => velopackInstalled ? release with { InstallerUrl = null } : release;

    /// <summary>User-facing text for failures that are actionable (feed
    /// mismatch, corrupt download, checksum rejection). Transport errors stay
    /// generic so callers can keep their connection-oriented message.</summary>
    internal static string? DescribeUpdateFailure(Exception ex) => ex is UpdateException ? ex.Message : null;

    internal static bool IsTrustedDownloadUrl(string url)
    {
        try
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
                return false;
            string host = uri.Host.ToLowerInvariant();
            return host == "github.com" || host.EndsWith(".github.com", StringComparison.Ordinal) ||
                   host == "objects.githubusercontent.com" ||
                   host == "release-assets.githubusercontent.com" ||
                   host == "api.github.com";
        }
        catch { return false; }
    }

    /// <summary>Verifies a downloaded installer is a valid PE executable and, when the
    /// release published a SHA-256 (<see cref="ReleaseInfo.InstallerSha256"/>), that the
    /// bytes match it. Throws if the file is corrupt, too small, not a valid Windows
    /// executable, or fails the published-hash comparison.</summary>
    internal static void VerifyInstallerIntegrity(string path, string? expectedSha256 = null)
    {
        var info = new FileInfo(path);
        if (info.Length < 1024 * 1024)
            throw new UpdateException($"Downloaded installer is suspiciously small ({info.Length} bytes): likely corrupt.");

        // Check MZ + PE signature via e_lfanew.
        using var fs = File.OpenRead(path);
        var header = new byte[64];
        if (fs.Read(header, 0, header.Length) != header.Length || header[0] != 'M' || header[1] != 'Z')
            throw new UpdateException("Downloaded file is not a valid Windows executable (missing MZ header).");
        int peOffset = BitConverter.ToInt32(header, 0x3C);
        if (peOffset < 0 || peOffset > info.Length - 6)
            throw new UpdateException("Downloaded file has a corrupt PE header.");
        fs.Seek(peOffset, SeekOrigin.Begin);
        var pe = new byte[6];
        if (fs.Read(pe, 0, pe.Length) != pe.Length || pe[0] != 'P' || pe[1] != 'E' || pe[2] != 0 || pe[3] != 0)
            throw new UpdateException("Downloaded file is not a valid PE executable.");
        fs.Close();

        // Compute SHA-256: audit trail when the release published no hash,
        // hard verification when it did. Scoped blocks: every handle is
        // closed before any delete below (Windows cannot delete open files).
        string hashStr;
        using (var sha = SHA256.Create())
        using (var stream = File.OpenRead(path))
        {
            hashStr = Convert.ToHexString(sha.ComputeHash(stream));
        }
        Debug.WriteLine($"[Update] Installer SHA-256: {hashStr} ({info.Length} bytes)");
        if (!string.IsNullOrWhiteSpace(expectedSha256))
        {
            string want = expectedSha256.Trim().ToLowerInvariant();
            if (!hashStr.Equals(want, StringComparison.OrdinalIgnoreCase))
            {
                try { File.Delete(path); } catch { }
                throw new UpdateException(
                    "Downloaded installer failed SHA-256 verification: refusing to run it. " +
                    "Delete %TEMP%\\WDM-Setup-*.exe and retry the update.");
            }
        }
    }

    /// <summary>True for the Velopack one-click Setup (per-user, clap-style
    /// flags). Everything else the updater downloads is the Inno wizard.</summary>
    internal static bool IsVelopackSetup(string installerPath)
    {
        string fileName = Path.GetFileName(installerPath ?? "");
        return fileName.StartsWith("WDM-User-Setup", StringComparison.OrdinalIgnoreCase)
            || fileName.StartsWith("WDM-Full-Setup", StringComparison.OrdinalIgnoreCase)
            || fileName.StartsWith("WDM-Velopack", StringComparison.OrdinalIgnoreCase)
            || fileName.Equals("WDM-win-Setup.exe", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Silent flags per installer family. Inno uses /SILENT (not
    /// /VERYSILENT) so a failed install still shows its error instead of
    /// vanishing without a trace.</summary>
    internal static string InstallerArgs(string installerPath, bool silent)
    {
        if (!silent)
            return "";
        return IsVelopackSetup(installerPath) ? "--silent" : "/SILENT /NORESTART";
    }

    /// <summary>Runs the downloaded installer. Inno is per-machine and needs
    /// elevation, but Setup must raise the UAC prompt itself: pre-elevating it
    /// with the runas verb hides the signed-in user's original token, so the
    /// installer's [Run] relaunch (runasoriginaluser) comes back with
    /// administrator rights instead of the user's own. Velopack one-click is
    /// per-user and must not elevate at all (breaks its %LocalAppData%
    /// targeting). Inno-style flags break Velopack's clap-style parsing and
    /// drop it back to the interactive "already installed" dialog, hence
    /// per-family args.</summary>
    public static Process? LaunchInstaller(string installerPath, bool silent = false, bool signalRestart = true)
    {
        ProcessStartInfo psi = BuildInstallerStartInfo(installerPath, silent);
        if (silent && signalRestart)
        {
            // The installer replaces the running exe (taskkill on install):
            // signal the main window to set _exiting first, or the
            // MinimizeToTray guard cancels the shutdown and the app lingers
            // hidden while its binaries are replaced underneath it. Same
            // signal VelopackUpdateService.ApplyAndRestart sends (BUG-038:
            // OptionsControl/About/UpdateAvailable paths all funnel here).
            try { ViewModels.MainViewModel.Restarting?.Invoke(); } catch { }
        }
        return Process.Start(psi);
    }

    /// <summary>Start info for a downloaded installer: shell-execute with the
    /// family's silent flags and no runas verb, so the installer owns its own
    /// elevation and can relaunch WDM as the logged-in user afterwards.</summary>
    internal static ProcessStartInfo BuildInstallerStartInfo(string installerPath, bool silent)
    {
        if (string.IsNullOrWhiteSpace(installerPath) || !File.Exists(installerPath) ||
            !installerPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Invalid installer path.");
        string full = Path.GetFullPath(installerPath);
        string temp = Path.GetFullPath(Path.GetTempPath());
        if (!full.StartsWith(temp, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Installer must be inside the temp folder.");
        return new ProcessStartInfo(full, InstallerArgs(full, silent)) { UseShellExecute = true };
    }

    /// <summary>Launches the installer silently and reports whether it is
    /// actually running. An installer that exits immediately with a failure
    /// code (UAC accepted but setup aborted, disk full, wrong flavor) must NOT
    /// be treated as success: callers keep WDM open instead of vanishing.</summary>
    public static async Task<InstallerLaunchResult> LaunchInstallerAndWaitForStart(string installerPath, int timeoutMs = 3000)
    {
        // Do not signal the restart until the installer is confirmed running.
        // A declined UAC prompt surfaces as Win32Exception from Process.Start;
        // report "did not start" instead of letting callers show a
        // download-oriented error for a file that downloaded fine.
        Process? proc;
        try
        {
            proc = LaunchInstaller(installerPath, silent: true, signalRestart: false);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return new InstallerLaunchResult(InstallerLaunchStatus.NotStarted, null, null);
        }
        if (proc is null)
            return new InstallerLaunchResult(InstallerLaunchStatus.NotStarted, null, null);

        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            try
            {
                if (proc.HasExited)
                {
                    int? code = TryGetExitCode(proc);
                    return new InstallerLaunchResult(
                        code == 0 ? InstallerLaunchStatus.Completed : InstallerLaunchStatus.Failed,
                        code, proc);
                }
            }
            catch { }
            await Task.Delay(100);
        }

        // Still running at the deadline: setup is progressing (UAC/wizard).
        try
        {
            if (!proc.HasExited)
                return new InstallerLaunchResult(InstallerLaunchStatus.Running, null, proc);
            int? code = TryGetExitCode(proc);
            return new InstallerLaunchResult(code == 0 ? InstallerLaunchStatus.Completed : InstallerLaunchStatus.Failed, code, proc);
        }
        catch
        {
            return new InstallerLaunchResult(InstallerLaunchStatus.Running, null, proc);
        }
    }

    private static int? TryGetExitCode(Process proc)
    {
        try { return proc.ExitCode; } catch { return null; }
    }

    /// <summary>Launches installer silently (no wizard): truly silent, bypasses the "already installed" prompt.</summary>
    public static void LaunchInstallerSilent(string installerPath)
        => LaunchInstaller(installerPath, silent: true);

    public static void OpenReleasesPage(string? url = null)
    {
        try
        {
            string target = ReleasesPage;
            if (!string.IsNullOrWhiteSpace(url) &&
                Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
                uri.Scheme == Uri.UriSchemeHttps &&
                (uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) ||
                 uri.Host.EndsWith(".github.com", StringComparison.OrdinalIgnoreCase)))
            {
                // Only allow links inside our own repo.
                if (uri.AbsolutePath.StartsWith($"/{RepositoryOwner}/{RepositoryName}", StringComparison.OrdinalIgnoreCase))
                    target = uri.ToString();
            }
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch
        {
            // Ignore failures to open the browser.
        }
    }

    /// <summary>Returns true if Velopack delta updates are available on this install.</summary>
    public static bool IsVelopackAvailable => VelopackUpdateService.IsVelopackInstalled;

    /// <summary>
    /// Unified update check: tries Velopack delta first, falls back to GitHub Release check.
    /// Returns a ReleaseInfo for GitHub full-installer path, or null if Velopack handles it.
    /// UI should prefer Velopack when <see cref="IsVelopackAvailable"/> is true.
    /// </summary>
    public static async Task<(ReleaseInfo? GithubRelease, object? VelopackUpdate)> CheckUnifiedAsync(CancellationToken ct = default)
    {
        if (IsVelopackAvailable)
        {
            var velopack = await VelopackUpdateService.CheckForUpdatesAsync(ct).ConfigureAwait(false);
            if (velopack is not null)
                return (null, velopack);
        }
        var github = await CheckLatestAsync(ct).ConfigureAwait(false);
        return (github, null);
    }

    /// <summary>Parses a tag like "v1.2.0", "1.2", or "1.2.0.0-beta" into a Version.</summary>
    private static Version? ParseVersion(string tag)
    {
        var match = Regex.Match(tag.TrimStart('v', 'V'), @"^(\d+(\.\d+){0,3})");
        return match.Success && Version.TryParse(match.Groups[1].Value, out var version) ? version : null;
    }
}