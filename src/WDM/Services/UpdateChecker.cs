using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace WDM.Services;

/// <summary>Details about the newest GitHub release.</summary>
public sealed record ReleaseInfo(string TagName, Version? Version, string Name, string Url, string? Body, DateTime? PublishedAt, string? InstallerUrl, string? UpdatePackageUrl = null);

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

    /// <summary>Shared HttpClient — creating one per call causes socket exhaustion under
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

            return new ReleaseInfo(tag, ParseVersion(tag), name ?? tag, url, body, published, installerUrl, updatePackageUrl);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Picks the browser_download_url of the WDM installer .exe from the release assets.</summary>
    private static string? FindInstallerUrl(JsonElement release)
    {
        if (!release.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
            return null;

        foreach (var asset in assets.EnumerateArray())
        {
            if (!asset.TryGetProperty("name", out var n) || n.GetString() is not string name)
                continue;
            // Strict naming: only our own setup asset, not any random .exe.
            if (!name.StartsWith("WDM_Setup_", StringComparison.OrdinalIgnoreCase) ||
                !name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                continue;
            if (asset.TryGetProperty("browser_download_url", out var u) &&
                u.GetString() is string dl && IsTrustedDownloadUrl(dl))
            {
                return dl;
            }
        }
        return null;
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

    /// <summary>Downloads the latest installer to the temp folder and returns its path.
    /// <paramref name="onProgress"/> reports 0..1 as bytes arrive.
    /// Verifies the downloaded file is a valid PE executable before returning.</summary>
    public static async Task<string> DownloadInstallerAsync(ReleaseInfo release, Action<double>? onProgress = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(release.InstallerUrl) || !IsTrustedDownloadUrl(release.InstallerUrl))
            throw new InvalidOperationException("The latest release has no trusted installer asset.");

        string fileName = $"WDM_Setup_{release.Version}_{Guid.NewGuid():N}.exe";
        string target = Path.Combine(Path.GetTempPath(), fileName);
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"WDM/{CurrentVersion}");

        using var response = await http.GetAsync(release.InstallerUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        long total = response.Content.Headers.ContentLength ?? -1;
        if (total > 500 * 1024 * 1024)
            throw new InvalidOperationException("Installer too large — refusing download.");
        using var source = await response.Content.ReadAsStreamAsync(ct);
        using var file = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true);

        var buffer = new byte[81920];
        long read = 0;
        while (true)
        {
            int n = await source.ReadAsync(buffer, ct);
            if (n <= 0)
                break;
            await file.WriteAsync(buffer.AsMemory(0, n), ct);
            read += n;
            if (read > 500 * 1024 * 1024)
                throw new InvalidOperationException("Installer exceeded size limit during download.");
            if (total > 0)
                onProgress?.Invoke((double)read / total);
        }
        await file.FlushAsync(ct);
        await file.DisposeAsync();

        VerifyInstallerIntegrity(target);

        return target;
    }

    private static bool IsTrustedDownloadUrl(string url)
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

    /// <summary>Verifies a downloaded installer is a valid PE executable and logs its SHA-256 hash.
    /// Throws if the file is corrupt, too small, or not a valid Windows executable.</summary>
    private static void VerifyInstallerIntegrity(string path)
    {
        var info = new FileInfo(path);
        if (info.Length < 1024 * 1024)
            throw new InvalidOperationException($"Downloaded installer is suspiciously small ({info.Length} bytes) — likely corrupt.");

        // Check MZ + PE signature via e_lfanew.
        using var fs = File.OpenRead(path);
        var header = new byte[64];
        if (fs.Read(header, 0, header.Length) != header.Length || header[0] != 'M' || header[1] != 'Z')
            throw new InvalidOperationException("Downloaded file is not a valid Windows executable (missing MZ header).");
        int peOffset = BitConverter.ToInt32(header, 0x3C);
        if (peOffset < 0 || peOffset > info.Length - 6)
            throw new InvalidOperationException("Downloaded file has a corrupt PE header.");
        fs.Seek(peOffset, SeekOrigin.Begin);
        var pe = new byte[6];
        if (fs.Read(pe, 0, pe.Length) != pe.Length || pe[0] != 'P' || pe[1] != 'E' || pe[2] != 0 || pe[3] != 0)
            throw new InvalidOperationException("Downloaded file is not a valid PE executable.");
        fs.Close();

        // Compute SHA-256 for audit trail
        using var sha = SHA256.Create();
        using var stream = File.OpenRead(path);
        var hash = sha.ComputeHash(stream);
        var hashStr = Convert.ToHexString(hash);
        Debug.WriteLine($"[Update] Installer SHA-256: {hashStr} ({info.Length} bytes)");
    }

    /// <summary>Runs the downloaded installer. Supports both Velopack (--silent) and Inno (/VERYSILENT) so the
    /// "WDM is already installed" modal in the screenshot never appears during a silent auto-update.</summary>
    public static Process? LaunchInstaller(string installerPath, bool silent = false)
    {
        if (string.IsNullOrWhiteSpace(installerPath) || !File.Exists(installerPath) ||
            !installerPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Invalid installer path.");
        string full = Path.GetFullPath(installerPath);
        string temp = Path.GetFullPath(Path.GetTempPath());
        if (!full.StartsWith(temp, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Installer must be inside the temp folder.");
        string args = silent ? "/VERYSILENT /SUPPRESSMSGBOXES --silent" : "";
        var psi = new ProcessStartInfo(full, args) { UseShellExecute = true };
        return Process.Start(psi);
    }

    /// <summary>Launches installer silently and waits for the process to start before returning.
    /// Retries for up to <paramref name="timeoutMs"/> milliseconds to handle slow UAC prompts.</summary>
    public static async Task<Process?> LaunchInstallerAndWaitForStart(string installerPath, int timeoutMs = 3000)
    {
        var proc = LaunchInstaller(installerPath, silent: true);
        if (proc is null) return null;

        // Wait for the process to appear (handles UAC delay, disk spin-up, etc.)
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            try
            {
                // Refresh process info — Process.Start returns immediately but the OS may not
                // have fully launched the process yet
                if (!proc.HasExited)
                    return proc;
            }
            catch { }
            await Task.Delay(100);
        }
        return proc;
    }

    /// <summary>Launches installer silently (no wizard) — truly silent, bypasses the "already installed" prompt.</summary>
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