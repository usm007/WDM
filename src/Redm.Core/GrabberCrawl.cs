namespace Redm.Core;

/// <summary>Site-grabber crawl core: same-host bounded walk, extension filter,
/// optional minimum size. Pure link extraction is unit-tested; fetching needs
/// network and is exercised live only.</summary>
public static class GrabberCrawl
{
    public static List<string> ExtractLinks(string html, string baseUrl)
    {
        var outLinks = new List<string>();
        foreach (System.Text.RegularExpressions.Match m in
            System.Text.RegularExpressions.Regex.Matches(html, """(?i)(?:href|src)\s*=\s*["']([^"'#>]+)["']"""))
        {
            string raw = m.Groups[1].Value.Trim();
            if (raw.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase)
                || raw.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)
                || raw.StartsWith('#')) continue;
            try { outLinks.Add(new Uri(new Uri(baseUrl), raw).AbsoluteUri); }
            catch { }
        }
        return outLinks.Distinct().ToList();
    }

    public static bool MatchesFilter(string url, string filter, long minSizeBytes, Func<string, long>? sizeOf = null)
    {
        string ext = System.IO.Path.GetExtension(url.Split('?')[0]).TrimStart('.').ToLowerInvariant();
        var wanted = filter.Split(new[] { ' ', ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(f => f.TrimStart('*', '.').ToLowerInvariant()).ToHashSet();
        if (wanted.Count > 0 && !wanted.Contains(ext) && filter.Trim() != "*") return false;
        if (minSizeBytes > 0 && (sizeOf?.Invoke(url) ?? long.MaxValue) < minSizeBytes) return false;
        return true;
    }
}
