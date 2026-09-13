using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace WDM.Services.Embed;

/// <summary>Generic embed/player-page → direct-stream resolver. All strategies match on
/// page/API <i>shapes</i> (token blobs, pass_md5, sources.hls, embed details/playback,
/// filecode stream APIs); origins are always derived from the live response URL so
/// mirror rotations (voe mirrors, byse*, dsvplay/playmogo) need no per-host config.</summary>
public static class EmbedResolver
{
    private const string ChromeUa = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/125.0 Safari/537.36";

    private static readonly Regex EmbedPathSimpleRe = new(
        @"/(e|embed|v|d|player)(/|$|\?)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Narrow gate so normal file URLs never pay the resolver cost.
    /// YouTube embeds are excluded by the caller (handled by yt-dlp).</summary>
    public static bool IsEmbedCandidate(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return false;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return false;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            return false;
        string path = uri.AbsolutePath;
        // A real filename at the end means it is already a direct link.
        string last = path.Split('/').LastOrDefault() ?? "";
        if (last.Contains('.') && last.Length < 64)
        {
            string ext = last[(last.LastIndexOf('.') + 1)..].Split('?', '&')[0].ToLowerInvariant();
            if (ext is "mp4" or "webm" or "mkv" or "mp3" or "m4a" or "m3u8" or "mpd" or "avi" or "mov")
                return false;
        }
        return EmbedPathSimpleRe.IsMatch(path)
            || (!string.IsNullOrEmpty(uri.Query) && uri.Query.Contains("filecode", StringComparison.OrdinalIgnoreCase));
    }

    public static async Task<ResolvedEmbed?> TryResolveAsync(
        string pageUrl,
        string? referer,
        IDictionary<string, string>? headers,
        CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var token = timeout.Token;

        var jar = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? incomingCookie = null;
        if (headers != null && headers.TryGetValue("Cookie", out var ck) && !string.IsNullOrWhiteSpace(ck))
            incomingCookie = ck;

        using var handler = new SocketsHttpHandler { AllowAutoRedirect = true, UseCookies = false };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };

        string currentUrl = pageUrl;
        string html = "";
        // Follow JS/iframe hops before resolving (voe mirrors, f75s frames).
        for (int hop = 0; hop < 4; hop++)
        {
            html = await GetHtmlAsync(http, currentUrl, referer ?? pageUrl, incomingCookie, jar, token);
            ThrowIfChallenge(html, currentUrl);
            string? redirect = EmbedParsers.ExtractJsRedirect(html);
            if (!string.IsNullOrWhiteSpace(redirect) && !SamePage(redirect, currentUrl))
            {
                currentUrl = AbsoluteUrl(currentUrl, redirect);
                continue;
            }
            var frames = EmbedParsers.ExtractIframes(html, currentUrl);
            string? player = frames.FirstOrDefault(f =>
                f.Contains("/e/", StringComparison.OrdinalIgnoreCase) ||
                f.Contains("/embed/", StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(player) && !SamePage(player, currentUrl) && hop < 3)
            {
                currentUrl = player;
                continue;
            }
            break;
        }

        string origin = OriginOf(currentUrl);
        string? code = EmbedParsers.ExtractFilecodeFromPath(currentUrl)
            ?? EmbedParsers.ExtractFilecodeFromPath(pageUrl);
        string? title = EmbedParsers.ExtractTitle(html);
        string? poster = EmbedParsers.ExtractPoster(html);

        var candidates = new List<StreamCandidate>();

        // 1) Bare/direct media in HTML.
        foreach (var u in EmbedParsers.ExtractDirectMediaUrls(html, currentUrl))
            candidates.Add(new StreamCandidate { Url = u, Kind = KindOf(u), Score = 40 });

        // 2) sources={hls,mp4} (+base64).
        var (hls, mp4) = EmbedParsers.ExtractSourcesBlock(html);
        if (!string.IsNullOrWhiteSpace(hls)) candidates.Add(new StreamCandidate { Url = hls, Kind = "HLS", Score = 80 });
        if (!string.IsNullOrWhiteSpace(mp4)) candidates.Add(new StreamCandidate { Url = mp4, Kind = "Video", Score = 70 });

        // 3) Obfuscated JSON arrays.
        foreach (var u in EmbedParsers.DecodeObfuscatedJsonArrays(html))
            candidates.Add(new StreamCandidate { Url = u, Kind = KindOf(u), Score = 75 });

        // 4) Token-blob → same-origin resolve API (firestream shape, generalized).
        string? blob = EmbedParsers.ExtractTokenBlob(html);
        if (!string.IsNullOrWhiteSpace(blob) && !string.IsNullOrWhiteSpace(code))
        {
            string? signed = await TryTokenResolveAsync(http, origin, code, blob, currentUrl, incomingCookie, jar, token);
            if (!string.IsNullOrWhiteSpace(signed))
                candidates.Add(new StreamCandidate { Url = signed, Kind = KindOf(signed), Score = 90 });
        }

        // 5) pass_md5 handshake (dood/dsvplay shape, generalized).
        var (passPath, passToken) = EmbedParsers.ExtractPassMd5(html);
        if (!string.IsNullOrWhiteSpace(passPath) && !string.IsNullOrWhiteSpace(passToken))
        {
            string? fin = await TryPassMd5Async(http, origin, passPath, passToken, currentUrl, token);
            if (!string.IsNullOrWhiteSpace(fin))
                candidates.Add(new StreamCandidate { Url = fin, Kind = "Video", Score = 90 });
        }

        // 6) embed details → playback (+AES-GCM) (byse shape, generalized).
        if (!string.IsNullOrWhiteSpace(code))
        {
            string? byse = await TryEmbedPlaybackAsync(http, origin, code, currentUrl, pageUrl, token);
            if (!string.IsNullOrWhiteSpace(byse))
                candidates.Add(new StreamCandidate { Url = byse, Kind = KindOf(byse), Score = 90 });
        }

        // 7) filecode stream API (vidwara shape, generalized to common templates).
        if (!string.IsNullOrWhiteSpace(code))
        {
            foreach (var u in await TryStreamApisAsync(http, origin, code, currentUrl, incomingCookie, jar, token))
                candidates.Add(new StreamCandidate { Url = u, Kind = KindOf(u), Score = 85 });
        }

        // 8) Playmate-style best effort: generic same-origin API probes around the video id.
        if (candidates.Count == 0 && !string.IsNullOrWhiteSpace(code))
        {
            foreach (var u in await TryGenericApiProbesAsync(http, origin, code, currentUrl, token))
                candidates.Add(new StreamCandidate { Url = u, Kind = KindOf(u), Score = 50 });
        }

        // Verify (best-first) so expiring/tokenized links are confirmed live.
        foreach (var c in candidates.OrderByDescending(c => c.Score))
        {
            if (await VerifyCandidateAsync(http, c.Url, currentUrl, token))
            {
                var refererOut = origin + "/";
                var outHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Referer"] = refererOut,
                    ["Origin"] = origin,
                };
                if (!string.IsNullOrWhiteSpace(incomingCookie))
                    outHeaders["Cookie"] = incomingCookie;
                else if (jar.Count > 0)
                    outHeaders["Cookie"] = string.Join("; ", jar.Select(kv => kv.Key + "=" + kv.Value));
                return new ResolvedEmbed
                {
                    DirectUrl = c.Url,
                    SourcePageUrl = pageUrl,
                    Title = title,
                    ThumbnailUrl = poster,
                    IsHls = c.Kind == "HLS" || c.Url.Contains(".m3u8", StringComparison.OrdinalIgnoreCase),
                    Referer = refererOut,
                    Headers = outHeaders,
                };
            }
        }
        return null;
    }

    // ── network steps ────────────────────────────────────────────────────
    private static async Task<string> GetHtmlAsync(HttpClient http, string url, string referer,
        string? incomingCookie, Dictionary<string, string> jar, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation("User-Agent", ChromeUa);
        req.Headers.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
        req.Headers.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9");
        if (Uri.TryCreate(referer, UriKind.Absolute, out var r)) req.Headers.Referrer = r;
        AttachCookies(req, incomingCookie, jar);
        using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        CollectCookies(resp, jar);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsStringAsync(ct);
    }

    private static async Task<string?> TryTokenResolveAsync(HttpClient http, string origin, string code,
        string blob, string referer, string? incomingCookie, Dictionary<string, string> jar, CancellationToken ct)
    {
        try
        {
            string api = EmbedParsers.BuildResolveApiUrl(origin, code);
            using var req = new HttpRequestMessage(HttpMethod.Post, api);
            req.Headers.TryAddWithoutValidation("User-Agent", ChromeUa);
            req.Headers.TryAddWithoutValidation("Accept", "application/json");
            if (Uri.TryCreate(referer, UriKind.Absolute, out var r)) req.Headers.Referrer = r;
            req.Headers.TryAddWithoutValidation("Origin", origin);
            AttachCookies(req, incomingCookie, jar);
            req.Content = new StringContent("{\"blob\":" + JsonSerializer.Serialize(blob) + "}", Encoding.UTF8, "application/json");
            using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            CollectCookies(resp, jar);
            if (!resp.IsSuccessStatusCode) return null;
            string body = await resp.Content.ReadAsStringAsync(ct);
            return EmbedParsers.ExtractSignedUrlFromJson(body);
        }
        catch { return null; }
    }

    private static async Task<string?> TryPassMd5Async(HttpClient http, string origin, string passPath,
        string token, string referer, CancellationToken ct)
    {
        try
        {
            string url = passPath.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? passPath : origin + passPath;
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("User-Agent", ChromeUa);
            if (Uri.TryCreate(referer, UriKind.Absolute, out var r)) req.Headers.Referrer = r;
            using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!resp.IsSuccessStatusCode) return null;
            string prefix = (await resp.Content.ReadAsStringAsync(ct)).Trim();
            if (string.IsNullOrWhiteSpace(prefix) || prefix.Contains("RELOAD", StringComparison.OrdinalIgnoreCase))
                return null;
            if (!prefix.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                return null;
            // Some mirrors return the final CDN URL directly (time-limited download link).
            if (prefix.Contains(".mp4", StringComparison.OrdinalIgnoreCase) && prefix.Contains("token=", StringComparison.OrdinalIgnoreCase))
                return prefix;
            long expiryMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var m = Regex.Match(prefix + " " + referer, @"expiry=(\d{10,})");
            if (m.Success && long.TryParse(m.Groups[1].Value, out var ex)) expiryMs = ex;
            return EmbedParsers.BuildDoodFinalUrl(prefix, token, expiryMs);
        }
        catch { return null; }
    }

    private static async Task<string?> TryEmbedPlaybackAsync(HttpClient http, string origin, string code,
        string currentUrl, string pageUrl, CancellationToken ct)
    {
        try
        {
            // details
            string detailsUrl = origin + "/api/videos/" + code + "/embed/details";
            using var dReq = new HttpRequestMessage(HttpMethod.Get, detailsUrl);
            dReq.Headers.TryAddWithoutValidation("User-Agent", ChromeUa);
            dReq.Headers.TryAddWithoutValidation("Accept", "application/json");
            if (Uri.TryCreate(currentUrl, UriKind.Absolute, out var dr)) dReq.Headers.Referrer = dr;
            using var dResp = await http.SendAsync(dReq, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!dResp.IsSuccessStatusCode) return null;
            string dBody = await dResp.Content.ReadAsStringAsync(ct);
            string? frame = EmbedParsers.ExtractEmbedFrameUrl(dBody);
            if (string.IsNullOrWhiteSpace(frame)) return null;

            string frameOrigin = OriginOf(frame);
            string frameCode = EmbedParsers.ExtractFilecodeFromPath(frame) ?? code;
            string playbackUrl = frameOrigin + "/api/videos/" + frameCode + "/embed/playback";
            using var pReq = new HttpRequestMessage(HttpMethod.Get, playbackUrl);
            pReq.Headers.TryAddWithoutValidation("User-Agent", ChromeUa);
            pReq.Headers.TryAddWithoutValidation("Accept", "*/*");
            if (Uri.TryCreate(frame, UriKind.Absolute, out var fr)) pReq.Headers.Referrer = fr;
            pReq.Headers.TryAddWithoutValidation("X-Embed-Parent", pageUrl);
            using var pResp = await http.SendAsync(pReq, HttpCompletionOption.ResponseHeadersRead, ct);
            if (pResp.StatusCode == HttpStatusCode.Unauthorized || pResp.StatusCode == HttpStatusCode.Forbidden)
                throw new EmbedInteractionRequiredException(pageUrl, "This host requires a browser check (device attestation) before the stream is released.");
            if (!pResp.IsSuccessStatusCode) return null;
            string pBody = await pResp.Content.ReadAsStringAsync(ct);
            if (pBody.Contains("attest", StringComparison.OrdinalIgnoreCase) && !pBody.Contains("payload", StringComparison.OrdinalIgnoreCase))
                throw new EmbedInteractionRequiredException(pageUrl, "This host requires a browser check before the stream is released.");
            // Plain (unencrypted) variant.
            var plain = EmbedParsers.ExtractSourcesFromDecryptedJson(pBody);
            var direct = plain.FirstOrDefault(u => u.Contains(".m3u8", StringComparison.OrdinalIgnoreCase))
                ?? plain.FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(direct)) return direct;
            // Encrypted variant.
            if (EmbedParsers.TryParsePlaybackPayload(pBody, out var parts, out var iv, out var payload) && parts.Length >= 2)
            {
                string? json = EmbedParsers.AesGcmDecrypt(parts[0], parts[1], iv, payload);
                if (!string.IsNullOrWhiteSpace(json))
                {
                    var urls = EmbedParsers.ExtractSourcesFromDecryptedJson(json);
                    return urls.FirstOrDefault(u => u.Contains(".m3u8", StringComparison.OrdinalIgnoreCase))
                        ?? urls.FirstOrDefault();
                }
            }
            return null;
        }
        catch (EmbedInteractionRequiredException) { throw; }
        catch { return null; }
    }

    private static async Task<List<string>> TryStreamApisAsync(HttpClient http, string origin, string code,
        string referer, string? incomingCookie, Dictionary<string, string> jar, CancellationToken ct)
    {
        var found = new List<string>();
        // Vidwara shape first, then common siblings — same-origin only.
        var posts = new[]
        {
            origin + "/api/stream",
            origin + "/api/videos/stream",
            origin + "/api/video/stream",
        };
        foreach (var api in posts)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, api);
                req.Headers.TryAddWithoutValidation("User-Agent", ChromeUa);
                req.Headers.TryAddWithoutValidation("Accept", "application/json");
                if (Uri.TryCreate(referer, UriKind.Absolute, out var r)) req.Headers.Referrer = r;
                req.Headers.TryAddWithoutValidation("Origin", origin);
                AttachCookies(req, incomingCookie, jar);
                req.Content = new StringContent(
                    "{\"filecode\":" + JsonSerializer.Serialize(code) + ",\"device\":\"web\"}",
                    Encoding.UTF8, "application/json");
                using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
                CollectCookies(resp, jar);
                if (!resp.IsSuccessStatusCode) continue;
                string body = await resp.Content.ReadAsStringAsync(ct);
                string? direct = EmbedParsers.ExtractSignedUrlFromJson(body);
                if (!string.IsNullOrWhiteSpace(direct)) { found.Add(direct); break; }
                foreach (var u in EmbedParsers.ExtractDirectMediaUrls(body, origin))
                    if (!found.Contains(u)) found.Add(u);
                if (found.Count > 0) break;
            }
            catch { }
        }
        return found;
    }

    private static async Task<List<string>> TryGenericApiProbesAsync(HttpClient http, string origin,
        string code, string referer, CancellationToken ct)
    {
        var found = new List<string>();
        // Best-effort probes for obfuscated players (playmate-style). Responses are
        // scanned for stream shapes; misses simply return nothing (capture stays primary).
        string[] gets =
        [
            origin + "/api/videos/" + code,
            origin + "/api/video/" + code,
            origin + "/api/source/" + code,
            origin + "/api/stream/" + code,
        ];
        foreach (var api in gets)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, api);
                req.Headers.TryAddWithoutValidation("User-Agent", ChromeUa);
                req.Headers.TryAddWithoutValidation("Accept", "application/json");
                if (Uri.TryCreate(referer, UriKind.Absolute, out var r)) req.Headers.Referrer = r;
                using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
                if (!resp.IsSuccessStatusCode) continue;
                string body = await resp.Content.ReadAsStringAsync(ct);
                string? direct = EmbedParsers.ExtractSignedUrlFromJson(body);
                if (!string.IsNullOrWhiteSpace(direct)) { found.Add(direct); break; }
                var (hls, mp4) = EmbedParsers.ExtractSourcesBlock(body);
                if (!string.IsNullOrWhiteSpace(hls)) found.Add(hls);
                if (!string.IsNullOrWhiteSpace(mp4)) found.Add(mp4);
                foreach (var u in EmbedParsers.ExtractDirectMediaUrls(body, origin))
                    if (!found.Contains(u)) found.Add(u);
                if (found.Count > 0) break;
            }
            catch { }
        }
        return found;
    }

    private static async Task<bool> VerifyCandidateAsync(HttpClient http, string url, string referer, CancellationToken ct)
    {
        try
        {
            using var head = new HttpRequestMessage(HttpMethod.Head, url);
            head.Headers.TryAddWithoutValidation("User-Agent", ChromeUa);
            if (Uri.TryCreate(referer, UriKind.Absolute, out var r)) head.Headers.Referrer = r;
            using var resp = await http.SendAsync(head, HttpCompletionOption.ResponseHeadersRead, ct);
            if (resp.IsSuccessStatusCode || resp.StatusCode == HttpStatusCode.PartialContent)
            {
                string ctStr = resp.Content.Headers.ContentType?.ToString() ?? "";
                if (ctStr.Contains("video", StringComparison.OrdinalIgnoreCase)
                    || ctStr.Contains("mpegurl", StringComparison.OrdinalIgnoreCase)
                    || ctStr.Contains("m3u8", StringComparison.OrdinalIgnoreCase)
                    || ctStr.Contains("mp4", StringComparison.OrdinalIgnoreCase)
                    || ctStr.Contains("dash", StringComparison.OrdinalIgnoreCase)
                    || url.Contains(".m3u8", StringComparison.OrdinalIgnoreCase)
                    || url.Contains(".mp4", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            // HEAD-shy CDNs: ranged GET probe.
            using var get = new HttpRequestMessage(HttpMethod.Get, url);
            get.Headers.TryAddWithoutValidation("User-Agent", ChromeUa);
            if (Uri.TryCreate(referer, UriKind.Absolute, out var r2)) get.Headers.Referrer = r2;
            get.Headers.Range = new RangeHeaderValue(0, 0);
            using var gResp = await http.SendAsync(get, HttpCompletionOption.ResponseHeadersRead, ct);
            return gResp.IsSuccessStatusCode || gResp.StatusCode == HttpStatusCode.PartialContent;
        }
        catch { return false; }
    }

    // ── small utilities ──────────────────────────────────────────────────
    private static void ThrowIfChallenge(string html, string url)
    {
        if (EmbedParsers.IsChallengePage(html))
            throw new EmbedInteractionRequiredException(url, "This page shows a bot check (captcha/Cloudflare). Complete it once in the WDM browser window, then retry.");
    }

    private static void AttachCookies(HttpRequestMessage req, string? incoming, Dictionary<string, string> jar)
    {
        if (!string.IsNullOrWhiteSpace(incoming))
            req.Headers.TryAddWithoutValidation("Cookie", incoming);
        else if (jar.Count > 0)
            req.Headers.TryAddWithoutValidation("Cookie", string.Join("; ", jar.Select(kv => kv.Key + "=" + kv.Value)));
    }

    private static void CollectCookies(HttpResponseMessage resp, Dictionary<string, string> jar)
    {
        try
        {
            if (!resp.Headers.TryGetValues("Set-Cookie", out var values))
                return;
            foreach (var v in values)
            {
                string pair = v.Split(';')[0].Trim();
                int eq = pair.IndexOf('=');
                if (eq > 0)
                    jar[pair[..eq].Trim()] = pair[(eq + 1)..].Trim();
            }
        }
        catch { }
    }

    private static string OriginOf(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var u))
            return u.GetLeftPart(UriPartial.Authority);
        return url;
    }

    private static string AbsoluteUrl(string baseUrl, string relative)
    {
        if (relative.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            return relative;
        if (Uri.TryCreate(baseUrl, UriKind.Absolute, out var b))
            return new Uri(b, relative).ToString();
        return relative;
    }

    private static bool SamePage(string a, string b) =>
        string.Equals(a.TrimEnd('/'), b.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);

    private static string KindOf(string url) =>
        url.Contains(".m3u8", StringComparison.OrdinalIgnoreCase) ? "HLS"
        : url.Contains(".mpd", StringComparison.OrdinalIgnoreCase) ? "DASH" : "Video";
}
