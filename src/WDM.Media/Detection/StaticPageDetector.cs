using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using WDM.Services;

namespace WDM.Media;

/// <summary>LEVEL 1 — static webpage inspection. One GET (2MB cap), no
/// JavaScript: OpenGraph, JSON-LD Video/AudioObject, video/audio/source tags.
/// Shallow by design — the embed extractor (L2) owns frame/player chasing.</summary>
internal static class StaticPageDetector
{
    private const int FetchCapBytes = 2 * 1024 * 1024;
    private const int MaxCandidates = 20;

    internal sealed record StaticHit(string? Title, List<MediaVariant> Candidates);

    internal static async Task<StaticHit> DetectAsync(HttpClient http, string pageUrl, CancellationToken ct)
    {
        var found = new List<MediaVariant>();
        string? title = null;
        try
        {
            if (NetworkGuard.IsBlockedResolveTarget(pageUrl))
                return new StaticHit(null, found);
            string html = await GetCappedTextAsync(http, pageUrl, ct).ConfigureAwait(false);
            if (string.IsNullOrEmpty(html))
                return new StaticHit(null, found);

            title = ExtractTitle(html);

            foreach (var u in ExtractMetaUrls(html, "og:video").Concat(ExtractMetaUrls(html, "og:video:secure_url")))
                AddUrl(found, pageUrl, u, "og:video tag", 0.7);
            foreach (var u in ExtractMetaUrls(html, "og:audio").Concat(ExtractMetaUrls(html, "og:audio:secure_url")))
                AddUrl(found, pageUrl, u, "og:audio tag", 0.7);

            foreach (var (u, kind) in ExtractJsonLdMedia(html))
                AddUrl(found, pageUrl, u, "JSON-LD " + kind, 0.65);

            foreach (Match m in Regex.Matches(html, @"<(video|audio)[^>]*>", RegexOptions.IgnoreCase))
            {
                string? src = Attr(m.Value, "src");
                if (src is not null)
                    AddUrl(found, pageUrl, src, "<" + m.Groups[1].Value.ToLowerInvariant() + "> tag", 0.55,
                        Width: AttrInt(m.Value, "width"), Height: AttrInt(m.Value, "height"));
            }
            foreach (Match m in Regex.Matches(html, @"<source[^>]+>", RegexOptions.IgnoreCase))
            {
                string? src = Attr(m.Value, "src");
                if (src is not null)
                    AddUrl(found, pageUrl, src, "<source> tag", 0.55);
            }

            return new StaticHit(title, found);
        }
        catch
        {
            return new StaticHit(title, found);
        }
    }

    private static void AddUrl(List<MediaVariant> found, string pageUrl, string raw, string evidence,
        double confidence, int? Width = null, int? Height = null)
    {
        if (found.Count >= MaxCandidates || string.IsNullOrWhiteSpace(raw))
            return;
        string? abs = MakeAbsolute(pageUrl, raw.Trim());
        if (abs is null || found.Any(v => v.MediaUrl.Equals(abs, StringComparison.OrdinalIgnoreCase)))
            return;
        bool dash = abs.EndsWith(".mpd");
        bool hls = abs.EndsWith(".m3u8");
        string label;
        try { label = Path.GetFileName(new Uri(abs).AbsolutePath); } catch { label = abs; }
        if (string.IsNullOrWhiteSpace(label))
            label = dash ? "DASH stream" : hls ? "HLS stream" : "Page media";
        found.Add(new MediaVariant
        {
            Label = label,
            MediaUrl = abs,
            ManifestUrl = (hls || dash) ? abs : null,
            RequiresHls = hls,
            RequiresDash = dash,
            Width = Width,
            Height = Height,
            Confidence = confidence,
            Evidence = new List<string> { evidence },
        });
    }

    private static async Task<string> GetCappedTextAsync(HttpClient http, string url, CancellationToken ct)
    {
        using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            return "";
        var mediaType = resp.Content.Headers.ContentType?.MediaType?.ToLowerInvariant() ?? "";
        if (mediaType.Contains("video") || mediaType.Contains("audio") || mediaType.Contains("mpegurl") || mediaType.Contains("mpd"))
            return "";
        using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var ms = new MemoryStream();
        var buf = new byte[16 * 1024];
        int read, total = 0;
        while ((read = await stream.ReadAsync(buf, 0, buf.Length, ct).ConfigureAwait(false)) > 0)
        {
            int take = Math.Min(read, FetchCapBytes - total);
            ms.Write(buf, 0, take);
            total += take;
            if (total >= FetchCapBytes)
                break;
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private static IEnumerable<string> ExtractMetaUrls(string html, string property)
    {
        // og:video / og:video:secure_url, property-first or content-first order.
        var rx = new Regex(@"<meta[^>]+property\s*=\s*[""']" + Regex.Escape(property) + @"[""'][^>]*>",
            RegexOptions.IgnoreCase);
        foreach (Match m in rx.Matches(html))
        {
            string? c = Attr(m.Value, "content");
            if (!string.IsNullOrWhiteSpace(c))
                yield return c;
        }
    }

    private static IEnumerable<(string url, string kind)> ExtractJsonLdMedia(string html)
    {
        var rx = new Regex(@"<script[^>]+type\s*=\s*[""']application/ld\+json[""'][^>]*>(.*?)</script>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        foreach (Match m in rx.Matches(html))
        {
            JsonDocument? doc = null;
            try { doc = JsonDocument.Parse(m.Groups[1].Value); }
            catch { continue; }
            using (doc)
            {
                foreach (var el in Flatten(doc.RootElement))
                {
                    if (el.ValueKind != JsonValueKind.Object)
                        continue;
                    string? type = el.TryGetProperty("@type", out var t) ? t.GetString() : null;
                    if (type is null || (!type.Contains("VideoObject") && !type.Contains("AudioObject")))
                        continue;
                    string kind = type.Contains("AudioObject") ? "AudioObject" : "VideoObject";
                    foreach (string prop in new[] { "contentUrl", "embedUrl" })
                    {
                        if (el.TryGetProperty(prop, out var u))
                        {
                            if (u.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(u.GetString()))
                                yield return (u.GetString()!, kind);
                            else if (u.ValueKind == JsonValueKind.Array)
                            {
                                foreach (var item in u.EnumerateArray())
                                {
                                    if (item.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(item.GetString()))
                                        yield return (item.GetString()!, kind);
                                }
                            }
                        }
                    }
                }
            }
        }
    }

    private static IEnumerable<JsonElement> Flatten(JsonElement root)
    {
        yield return root;
        if (root.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in root.EnumerateArray())
                foreach (var sub in Flatten(item))
                    yield return sub;
        }
        else if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("@graph", out var graph))
        {
            foreach (var sub in Flatten(graph))
                yield return sub;
        }
    }

    private static string? ExtractTitle(string html)
    {
        foreach (Match m in Regex.Matches(html,
            @"<meta[^>]+property\s*=\s*[""']og:title[""'][^>]*>", RegexOptions.IgnoreCase))
        {
            string? c = Attr(m.Value, "content");
            if (!string.IsNullOrWhiteSpace(c))
                return c.Trim().Length > 200 ? c.Trim()[..200] : c.Trim();
        }
        Match t = Regex.Match(html, @"<title[^>]*>(.*?)</title>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (t.Success)
        {
            string s = Regex.Replace(t.Groups[1].Value, @"\s+", " ").Trim();
            if (!string.IsNullOrWhiteSpace(s))
                return s.Length > 200 ? s[..200] : s;
        }
        return null;
    }

    private static string? Attr(string tag, string name)
    {
        Match m = Regex.Match(tag, name + @"\s*=\s*(""([^""]*)""|'([^']*)'|([^\s>]+))",
            RegexOptions.IgnoreCase);
        if (!m.Success)
            return null;
        return m.Groups[2].Success ? m.Groups[2].Value
            : m.Groups[3].Success ? m.Groups[3].Value
            : m.Groups[4].Value;
    }

    private static int? AttrInt(string tag, string name)
    {
        string? v = Attr(tag, name);
        return int.TryParse(v, out int n) && n > 0 ? n : null;
    }

    internal static string? MakeAbsolute(string pageUrl, string raw)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(raw) || raw.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
                || raw.StartsWith("blob:", StringComparison.OrdinalIgnoreCase)
                || raw.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase))
                return null;
            var abs = new Uri(new Uri(pageUrl), raw);
            if (abs.Scheme != Uri.UriSchemeHttp && abs.Scheme != Uri.UriSchemeHttps)
                return null;
            return abs.ToString();
        }
        catch
        {
            return null;
        }
    }
}
