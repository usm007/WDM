namespace Redm.Core;

/// <summary>Bounded same-host crawl that collects downloadable links.</summary>
public static class GrabberRun
{
    public static async Task<List<string>> CrawlAsync(HttpClient http, GrabberProject project,
        IProgress<int>? found = null, CancellationToken ct = default)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { project.StartUrl };
        var pages = new Queue<(string url, int depth)>();
        pages.Enqueue((project.StartUrl, 0));
        var hits = new List<string>();
        string host;
        try { host = new Uri(project.StartUrl).Host; } catch { return hits; }
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(30)); // budget like the reference design
        while (pages.Count > 0)
        {
            cts.Token.ThrowIfCancellationRequested();
            var (url, depth) = pages.Dequeue();
            string html;
            try
            {
                using var res = await http.GetAsync(url, cts.Token);
                if (!res.IsSuccessStatusCode) continue;
                var mt = res.Content.Headers.ContentType?.MediaType ?? "";
                if (!mt.Contains("html")) continue;
                html = await res.Content.ReadAsStringAsync(cts.Token);
            }
            catch { continue; }
            foreach (var link in GrabberCrawl.ExtractLinks(html, url))
            {
                if (GrabberCrawl.MatchesFilter(link, project.FileFilter, project.MinSizeBytes))
                {
                    if (!hits.Contains(link, StringComparer.OrdinalIgnoreCase))
                    {
                        hits.Add(link);
                        found?.Report(hits.Count);
                    }
                    continue;
                }
                if (depth < project.MaxDepth - 1)
                {
                    try
                    {
                        var u = new Uri(link);
                        if (u.Host.Equals(host, StringComparison.OrdinalIgnoreCase) && seen.Add(u.AbsoluteUri))
                            pages.Enqueue((u.AbsoluteUri, depth + 1));
                    }
                    catch { }
                }
            }
        }
        return hits;
    }
}
