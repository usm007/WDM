using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace WDM.Services;

/// <summary>Details about the newest GitHub release.</summary>
public sealed record ReleaseInfo(string TagName, Version? Version, string Name, string Url, string? Body, DateTime? PublishedAt);

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

    /// <summary>Fetches the latest release. Returns null when the repo is unreachable,
    /// the request fails, or there is no tagged release yet.</summary>
    public static async Task<ReleaseInfo?> CheckLatestAsync(CancellationToken ct = default)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd($"WDM/{CurrentVersion}");
            http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            string json = await http.GetStringAsync(LatestReleaseApi, ct);

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            string? tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() : null;
            string? name = root.TryGetProperty("name", out var n) ? n.GetString() : null;
            string? url = root.TryGetProperty("html_url", out var u) ? u.GetString() : null;
            string? body = root.TryGetProperty("body", out var b) ? b.GetString() : null;
            DateTime? published = root.TryGetProperty("published_at", out var p) && p.TryGetDateTime(out var dt) ? dt : null;
            if (string.IsNullOrWhiteSpace(tag) || string.IsNullOrWhiteSpace(url))
                return null;

            return new ReleaseInfo(tag, ParseVersion(tag), name ?? tag, url, body, published);
        }
        catch
        {
            return null;
        }
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