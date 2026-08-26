using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace WDM.Services;

/// <summary>Details about the newest GitHub release.</summary>
public sealed record ReleaseInfo(string TagName, Version? Version, string Name, string Url, string? Body, DateTime? PublishedAt, string? InstallerUrl);

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
            if (string.IsNullOrWhiteSpace(tag) || string.IsNullOrWhiteSpace(url))
                return null;

            return new ReleaseInfo(tag, ParseVersion(tag), name ?? tag, url, body, published, installerUrl);
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
            if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                && asset.TryGetProperty("browser_download_url", out var u))
            {
                return u.GetString();
            }
        }
        return null;
    }

    /// <summary>Downloads the latest installer to the temp folder and returns its path.
    /// <paramref name="onProgress"/> reports 0..1 as bytes arrive.</summary>
    public static async Task<string> DownloadInstallerAsync(ReleaseInfo release, Action<double>? onProgress = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(release.InstallerUrl))
            throw new InvalidOperationException("The latest release has no installer asset.");

        string target = Path.Combine(Path.GetTempPath(), $"WDM_Setup_{release.Version}.exe");
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"WDM/{CurrentVersion}");

        using var response = await http.GetAsync(release.InstallerUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        long total = response.Content.Headers.ContentLength ?? -1;
        using var source = await response.Content.ReadAsStreamAsync(ct);
        using var file = File.Create(target);

        var buffer = new byte[81920];
        long read = 0;
        while (true)
        {
            int n = await source.ReadAsync(buffer, ct);
            if (n <= 0)
                break;
            await file.WriteAsync(buffer.AsMemory(0, n), ct);
            read += n;
            if (total > 0)
                onProgress?.Invoke((double)read / total);
        }

        return target;
    }

    /// <summary>Runs the downloaded installer (UAC-per-user install; WDM restarts after).</summary>
    public static void LaunchInstaller(string installerPath)
    {
        Process.Start(new ProcessStartInfo(installerPath) { UseShellExecute = true });
    }

    public static void OpenReleasesPage(string? url = null)
    {
        try
        {
            Process.Start(new ProcessStartInfo(string.IsNullOrWhiteSpace(url) ? ReleasesPage : url) { UseShellExecute = true });
        }
        catch
        {
            // Ignore failures to open the browser.
        }
    }

    /// <summary>Parses a tag like "v1.2.0", "1.2", or "1.2.0.0-beta" into a Version.</summary>
    private static Version? ParseVersion(string tag)
    {
        var match = Regex.Match(tag.TrimStart('v', 'V'), @"^(\d+(\.\d+){0,3})");
        return match.Success && Version.TryParse(match.Groups[1].Value, out var version) ? version : null;
    }
}