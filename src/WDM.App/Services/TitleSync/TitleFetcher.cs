using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace WDM.Services.TitleSync;

/// <summary>Fetches a human video title from the internet for downloads whose
/// filename carries no title (master.m3u8, download_*.bin, signed-URL soup).
/// Order: oEmbed provider endpoints, then OpenGraph/JSON-LD/&lt;title&gt; scrape
/// of the source page reusing the capture's Cookie/UA/Referer session.
/// Never throws, never blocks the download: failure simply yields null.</summary>
public static class TitleFetcher
{
    private const string ChromeUa = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/125.0 Safari/537.36";

    private static readonly (string HostFragment, string Endpoint)[] OEmbedProviders =
    [
        ("vimeo.com", "https://vimeo.com/api/oembed.json?url="),
        ("tiktok.com", "https://www.tiktok.com/oembed?url="),
        ("twitter.com", "https://publish.twitter.com/oembed?url="),
        ("x.com", "https://publish.twitter.com/oembed?url="),
        ("dailymotion.com", "https://www.dailymotion.com/services/oembed?url="),
        ("dai.ly", "https://www.dailymotion.com/services/oembed?url="),
    ];

    /// <summary>True when a fetch is worthwhile: an http(s) source page exists and
    /// the current name is empty, generic, or a titless manifest stem.</summary>
    public static bool ShouldAttempt(string? currentFileName, string? pageUrl)
    {
        if (string.IsNullOrWhiteSpace(pageUrl) || !IsHttpUrl(pageUrl))
            return false;
        if (string.IsNullOrWhiteSpace(currentFileName))
            return true;
        string name = currentFileName.Trim();
        if (FileNameHelper.IsManifestStem(System.IO.Path.GetFileNameWithoutExtension(name)))
            return true;
        return name.StartsWith("download_", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("download.", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("file_", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".bin", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase);
    }

    public static HttpClient CreateClient()
    {
        // No auto-redirect: callers follow redirects manually so captured
        // Cookie headers can never ride along cross-origin (BUG-031). The
        // shared handler forwards custom headers on redirect otherwise.
        var handler = new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false };
        var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
        http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", ChromeUa);
        return http;
    }

    public static async Task<string?> TryFetchTitleAsync(
        string? pageUrl,
        string? referer,
        IDictionary<string, string>? headers,
        HttpClient http,
        CancellationToken ct)
    {
        string? page = FirstHttpUrl(pageUrl, referer);
        if (page is null)
            return null;
        try
        {
            string? endpoint = OEmbedEndpointFor(page);
            if (endpoint is not null)
            {
                string? oembed = await TryOEmbedAsync(http, endpoint + Uri.EscapeDataString(page), ct).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(oembed))
                    return oembed.Trim();
            }
            return await TryPageMetaAsync(http, page, referer, headers, ct).ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
    }

    internal static string? OEmbedEndpointFor(string pageUrl)
    {
        if (!Uri.TryCreate(pageUrl, UriKind.Absolute, out var uri))
            return null;
        string host = uri.Host.ToLowerInvariant();
        foreach (var (fragment, endpoint) in OEmbedProviders)
        {
            if (host.Equals(fragment, StringComparison.OrdinalIgnoreCase) ||
                host.EndsWith("." + fragment, StringComparison.OrdinalIgnoreCase))
                return endpoint;
        }
        return null;
    }

    private static async Task<string?> TryOEmbedAsync(HttpClient http, string apiUrl, CancellationToken ct)
    {
        try
        {
            // Provider endpoints carry no cookies, so following up to 2
            // redirects here is safe (handler no longer auto-redirects).
            string target = apiUrl;
            for (int hop = 0; hop < 3; hop++)
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, target);
                req.Headers.TryAddWithoutValidation("Accept", "application/json");
                using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                if (IsRedirect(resp.StatusCode) && resp.Headers.Location is Uri loc)
                {
                    target = loc.IsAbsoluteUri ? loc.ToString() : new Uri(new Uri(target), loc).ToString();
                    // Provider open-redirects must not become intranet probes:
                    // stop following when the chain leaves public space.
                    if (WDM.Services.CaptureServer.IsBlockedResolveTarget(target))
                        return null;
                    continue;
                }
                if (!resp.IsSuccessStatusCode)
                    return null;
                string body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                    doc.RootElement.TryGetProperty("title", out var title) &&
                    title.ValueKind == JsonValueKind.String)
                {
                    string? t = title.GetString();
                    return string.IsNullOrWhiteSpace(t) ? null : t;
                }
                return null;
            }
            return null;
        }
        catch
        {
            return null;
        }
    }

    private static async Task<string?> TryPageMetaAsync(
        HttpClient http, string page, string? referer,
        IDictionary<string, string>? headers, CancellationToken ct)
    {
        try
        {
            // Captured session cookies ride along only to the host they were
            // captured for — never to a third-party page or a cross-host
            // redirect target (BUG-031).
            string? cookieHost = HostOf(page);
            if (headers != null && headers.TryGetValue("Cookie", out var cookie) && !string.IsNullOrWhiteSpace(cookie))
            {
                string? refHost = HostOf(referer);
                if (refHost is not null && !string.Equals(refHost, cookieHost, StringComparison.OrdinalIgnoreCase))
                    cookieHost = refHost;
            }
            string target = page;
            HttpResponseMessage? resp = null;
            for (int hop = 0; hop < 4; hop++)
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, target);
                req.Headers.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
                req.Headers.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9");
                if (Uri.TryCreate(referer ?? page, UriKind.Absolute, out var r))
                    req.Headers.Referrer = r;
                if (headers != null && headers.TryGetValue("Cookie", out var ck) && !string.IsNullOrWhiteSpace(ck) &&
                    string.Equals(HostOf(target), cookieHost, StringComparison.OrdinalIgnoreCase))
                    req.Headers.TryAddWithoutValidation("Cookie", ck);
                // No auto-redirect: follow same-host redirects manually so the
                // Cookie header above can never leak cross-origin (the shared
                // handler forwards custom headers on redirect).
                resp?.Dispose();
                resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                if (!IsRedirect(resp.StatusCode) || resp.Headers.Location is not Uri loc)
                    break;
                string next = loc.IsAbsoluteUri ? loc.ToString() : new Uri(new Uri(target), loc).ToString();
                if (!string.Equals(HostOf(next), HostOf(target), StringComparison.OrdinalIgnoreCase))
                    break; // cross-host redirect: stop with cookies withheld
                target = next;
            }
            if (resp is null)
                return null;
            using (resp)
            {
                if (!resp.IsSuccessStatusCode)
                    return null;
                const int maxHtmlBytes = 2 * 1024 * 1024;
                using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                using var ms = new System.IO.MemoryStream();
                var tmp = new byte[81920];
                int n;
                int total = 0;
                while ((n = await stream.ReadAsync(tmp.AsMemory(0, tmp.Length), ct).ConfigureAwait(false)) > 0)
                {
                    total += n;
                    if (total > maxHtmlBytes)
                        return null;
                    ms.Write(tmp, 0, n);
                }
                string html = System.Text.Encoding.UTF8.GetString(ms.ToArray());
                if (string.IsNullOrWhiteSpace(html) || Embed.EmbedParsers.IsChallengePage(html))
                    return null;
                // og:title / twitter:title / <title> first (highest signal), then JSON-LD.
                string? title = Embed.EmbedParsers.ExtractTitle(html);
                if (!string.IsNullOrWhiteSpace(title))
                    return title;
                return Embed.EmbedParsers.ExtractJsonLdTitle(html);
            }
        }
        catch
        {
            return null;
        }
    }

    private static string? FirstHttpUrl(string? a, string? b)
    {
        if (IsHttpUrl(a)) return a;
        if (IsHttpUrl(b)) return b;
        return null;
    }

    private static bool IsHttpUrl(string? url) =>
        !string.IsNullOrWhiteSpace(url)
        && Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    private static string? HostOf(string? url) =>
        string.IsNullOrWhiteSpace(url) ? null
        : Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Host : null;

    private static bool IsRedirect(System.Net.HttpStatusCode code) =>
        code is System.Net.HttpStatusCode.MovedPermanently
            or System.Net.HttpStatusCode.Found
            or System.Net.HttpStatusCode.SeeOther
            or System.Net.HttpStatusCode.TemporaryRedirect
            or (System.Net.HttpStatusCode)308;
}
