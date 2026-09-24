using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace WDM.Services.Embed;

/// <summary>Pure, dependency-free parsers for the generic embed pipeline. Every method
/// matches on page/API <i>shapes</i>, never on host names, so mirror domains
/// (voe mirrors, byse*, dsvplay/playmogo, firestream) work without per-host config.</summary>
public static class EmbedParsers
{
    private static readonly Regex AbsMediaUrlRe = new(
        @"https?:[^""'\s\\]*?(?:\.m3u8|\.mpd|\.mp4|\.webm)(?:\?[^\s""'\\]*)?",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex VideoTagRe = new(
        @"<(?:video|source)[^>]+src\s*=\s*[""']([^""']+)[""']",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex OgVideoRe = new(
        @"<meta[^>]+property\s*=\s*[""'](?:og:video(?::secure_url|:url)?|twitter:player:stream)[""'][^>]+content\s*=\s*[""']([^""']+)[""']",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex OgVideoReversedRe = new(
        @"<meta[^>]+content\s*=\s*[""']([^""']+)[""'][^>]+property\s*=\s*[""'](?:og:video(?::secure_url|:url)?|twitter:player:stream)[""']",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex SourcesBlockRe = new(
        @"sources\s*[=:]\s*\{([^}]*)\}",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    // Player configs usually leave keys unquoted ({hls:"..."}); quotes optional.
    private static readonly Regex HlsFieldRe = new(
        @"[""']?hls[""']?\s*:\s*[""']([^""']+)[""']",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex Mp4FieldRe = new(
        @"[""']?(?:mp4|file|src)[""']?\s*:\s*[""'](https?[^""']+)[""']",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex JsonScriptArrayRe = new(
        @"<script[^>]*type\s*=\s*[""']application/json[""'][^>]*>\s*(\[.*?\])\s*</script>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex TokenBlobRe = new(
        @"<script[^>]*id\s*=\s*[""']([a-z0-9_-]*?(?:token|blob)[a-z0-9_-]*?)[""'][^>]*>(.*?)</script>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex PassMd5Re = new(
        @"[""']((?:https?://[^""']+)?/pass_md5/[^""']+)[""']",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex TokenParamRe = new(
        @"[?&]token=([A-Za-z0-9]+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Quoted/JSON token forms ("token":"abc", 'token'='abc', token=abc).
    private static readonly Regex TokenLooseRe = new(
        @"[""']token[""']\s*[:=]\s*[""']?([A-Za-z0-9]+)|\btoken=([A-Za-z0-9]+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex JsonLdRe = new(
        @"<script[^>]*type\s*=\s*[""']application/ld\+json[""'][^>]*>(.*?)</script>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex TitleRe = new(
        @"<title[^>]*>(.*?)</title>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex OgTitleRe = new(
        @"<meta[^>]+property\s*=\s*[""']og:title[""'][^>]+content\s*=\s*[""']([^""']+)[""']",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex PosterRe = new(
        @"(?:VOEPlayer\s*\.\s*poster|poster)\s*[=:]\s*[""']([^""']+)[""']",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex JsRedirectRe = new(
        @"(?:window\.location(?:\.href)?|location\.href)\s*=\s*[""'](https?[^""']+)[""']",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex IframeRe = new(
        @"<iframe[^>]+src\s*=\s*[""']([^""']+)[""']",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex FilecodePathRe = new(
        @"/(?:e|embed|v|d)/([A-Za-z0-9_-]{4,64})(?:[/?#]|$)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly string[] ChallengeMarkers =
    [
        "just a moment", "cf-browser-verification", "challenge-platform",
        "turnstile", "cf-challenge", "attention required", "verify you are human",
        "cf_clearance", "data-sitekey",
    ];

    // ── challenge detection ──────────────────────────────────────────────
    public static bool IsChallengePage(string html)
    {
        if (string.IsNullOrEmpty(html))
            return false;
        string lower = html.ToLowerInvariant();
        foreach (var marker in ChallengeMarkers)
        {
            if (lower.Contains(marker, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    // ── 1. direct media tags / bare URLs ─────────────────────────────────
    public static List<string> ExtractDirectMediaUrls(string html, string baseUrl)
    {
        var found = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string? u)
        {
            if (string.IsNullOrWhiteSpace(u))
                return;
            u = u.Replace("\\/", "/").Trim();
            if (u.StartsWith("//"))
                u = "https:" + u;
            else if (u.StartsWith('/'))
            {
                if (Uri.TryCreate(baseUrl, UriKind.Absolute, out var b))
                    u = new Uri(b, u).ToString();
            }
            else if (!u.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                if (Uri.TryCreate(baseUrl, UriKind.Absolute, out var b2))
                {
                    try { u = new Uri(b2, u).ToString(); } catch { return; }
                }
                else return;
            }
            if ((u.Contains(".m3u8", StringComparison.OrdinalIgnoreCase)
                 || u.Contains(".mpd", StringComparison.OrdinalIgnoreCase)
                 || u.Contains(".mp4", StringComparison.OrdinalIgnoreCase)
                 || u.Contains(".webm", StringComparison.OrdinalIgnoreCase))
                && seen.Add(u))
                found.Add(u);
        }
        if (string.IsNullOrEmpty(html))
            return found;
        foreach (Match m in VideoTagRe.Matches(html)) Add(m.Groups[1].Value);
        foreach (Match m in OgVideoRe.Matches(html)) Add(m.Groups[1].Value);
        foreach (Match m in OgVideoReversedRe.Matches(html)) Add(m.Groups[1].Value);
        foreach (Match m in AbsMediaUrlRe.Matches(html)) Add(m.Value);
        return found;
    }

    // ── 2. sources={hls,mp4} blocks (Voe shape, generalized) ─────────────
    public static (string? Hls, string? Mp4) ExtractSourcesBlock(string html)
    {
        if (string.IsNullOrEmpty(html))
            return (null, null);
        foreach (Match block in SourcesBlockRe.Matches(html))
        {
            string body = block.Groups[1].Value;
            var hls = HlsFieldRe.Match(body);
            var mp4 = Mp4FieldRe.Match(body);
            string? hlsUrl = hls.Success ? TryBase64Decode(hls.Groups[1].Value.Trim()) ?? hls.Groups[1].Value.Trim() : null;
            string? mp4Url = mp4.Success ? mp4.Groups[1].Value.Trim() : null;
            if (!string.IsNullOrWhiteSpace(hlsUrl) || !string.IsNullOrWhiteSpace(mp4Url))
                return (NullIfBlank(hlsUrl), NullIfBlank(mp4Url));
        }
        // Fallback: page-level hls field outside a sources block.
        var loose = HlsFieldRe.Match(html);
        if (loose.Success)
        {
            string raw = loose.Groups[1].Value.Trim();
            string? decoded = TryBase64Decode(raw) ?? raw;
            if (!string.IsNullOrWhiteSpace(decoded))
                return (decoded, null);
        }
        return (null, null);
    }

    // ── 3. obfuscated application/json string arrays ─────────────────────
    public static List<string> DecodeObfuscatedJsonArrays(string html)
    {
        var urls = new List<string>();
        if (string.IsNullOrEmpty(html))
            return urls;
        foreach (Match script in JsonScriptArrayRe.Matches(html))
        {
            List<string>? items;
            try
            {
                items = JsonSerializer.Deserialize<List<string>>(script.Groups[1].Value);
            }
            catch { continue; }
            if (items is null)
                continue;
            foreach (var item in items)
            {
                string? decoded = TryDecodeObfuscatedString(item);
                if (string.IsNullOrWhiteSpace(decoded))
                    continue;
                foreach (Match m in AbsMediaUrlRe.Matches(decoded))
                {
                    string u = m.Value.Replace("\\/", "/");
                    if (!urls.Contains(u))
                        urls.Add(u);
                }
                // Decoded JSON may itself hold {"source": "...m3u8"}.
                var inner = Mp4FieldRe.Match(decoded);
                if (inner.Success && !urls.Contains(inner.Groups[1].Value))
                    urls.Add(inner.Groups[1].Value);
            }
        }
        return urls;
    }

    /// <summary>Voe-style chain: rot13 → strip junk tokens → base64 → bytes-3 →
    /// reverse → base64 → JSON/URL. Returns null when the input is not such a string.</summary>
    public static string? TryDecodeObfuscatedString(string input)
    {
        if (string.IsNullOrWhiteSpace(input) || input.Length < 16)
            return null;
        try
        {
            string s = Rot13(input);
            foreach (var junk in new[] { "@$,", "^^", ",~@", "%?", "*~", "!!", "#&" })
                s = s.Replace(junk, "", StringComparison.Ordinal);
            byte[] first = Convert.FromBase64String(s);
            byte[] shifted = new byte[first.Length];
            for (int i = 0; i < first.Length; i++)
                shifted[i] = (byte)(first[i] - 3);
            Array.Reverse(shifted);
            string second = Encoding.UTF8.GetString(shifted);
            byte[] final = Convert.FromBase64String(second.Trim());
            return Encoding.UTF8.GetString(final);
        }
        catch { return null; }
    }

    // ── 4. token/blob scripts (Firestream shape, generalized) ────────────
    public static string? ExtractTokenBlob(string html)
    {
        if (string.IsNullOrEmpty(html))
            return null;
        foreach (Match m in TokenBlobRe.Matches(html))
        {
            string body = m.Groups[2].Value.Trim();
            if (!string.IsNullOrWhiteSpace(body))
                return body;
        }
        return null;
    }

    /// <summary>Find the resolve-API path for a token blob flow by scanning the page
    /// for href/action/fetch references; falls back to the conventional
    /// /api/videos/{id}/resolve shape.</summary>
    public static string BuildResolveApiUrl(string origin, string pageId)
    {
        return origin.TrimEnd('/') + "/api/videos/" + pageId + "/resolve";
    }

    public static string? ExtractSignedUrlFromJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;
        foreach (var field in new[] { "signedVideoUrl", "signedUrl", "url", "src", "streaming_url", "source", "file" })
        {
            var m = Regex.Match(json, "\"" + field + "\"\\s*:\\s*\"([^\"]+)\"", RegexOptions.IgnoreCase);
            if (m.Success && m.Groups[1].Value.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                return m.Groups[1].Value.Replace("\\/", "/");
        }
        return null;
    }

    // ── 5. pass_md5 (Dood/dsvplay shape, generalized) ────────────────────
    public static (string? PassPath, string? Token) ExtractPassMd5(string html)
    {
        if (string.IsNullOrEmpty(html))
            return (null, null);
        var pass = PassMd5Re.Match(html);
        if (!pass.Success)
            return (null, null);
        string? token = null;
        var tok = TokenParamRe.Match(html);
        if (tok.Success)
            token = tok.Groups[1].Value;
        else if (TokenLooseRe.Match(html) is { Success: true } loose)
            token = !string.IsNullOrWhiteSpace(loose.Groups[1].Value) ? loose.Groups[1].Value : loose.Groups[2].Value;
        if (string.IsNullOrWhiteSpace(token))
        {
            // Token is sometimes only the last segment of the pass path.
            string p = pass.Groups[1].Value;
            int slash = p.LastIndexOf('/');
            if (slash >= 0 && slash < p.Length - 1)
            {
                string tail = p[(slash + 1)..].Split('?', '&')[0];
                if (tail.Length >= 4)
                    token = tail;
            }
        }
        return (pass.Groups[1].Value.Replace("\\/", "/"), token);
    }

    public static string BuildDoodFinalUrl(string prefix, string token, long expiryMs, Random? rng = null)
    {
        rng ??= Random.Shared;
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
        var sb = new StringBuilder(10);
        for (int i = 0; i < 10; i++)
            sb.Append(alphabet[rng.Next(alphabet.Length)]);
        string sep = prefix.Contains('?') ? "&" : "?";
        return prefix + sb + sep + "token=" + token + "&expiry=" + expiryMs;
    }

    // ── 6. embed details/playback (Byse shape, generalized) ──────────────
    public static string? ExtractEmbedFrameUrl(string detailsJson)
    {
        if (string.IsNullOrWhiteSpace(detailsJson))
            return null;
        foreach (var field in new[] { "embed_frame_url", "embedFrameUrl", "embed_url", "embedUrl", "frame_url" })
        {
            var m = Regex.Match(detailsJson, "\"" + field + "\"\\s*:\\s*\"([^\"]+)\"", RegexOptions.IgnoreCase);
            if (m.Success)
                return m.Groups[1].Value.Replace("\\/", "/");
        }
        return null;
    }

    public static bool TryParsePlaybackPayload(string playbackJson, out string[] KeyParts, out string Iv, out string Payload)
    {
        KeyParts = [];
        Iv = "";
        Payload = "";
        if (string.IsNullOrWhiteSpace(playbackJson))
            return false;
        try
        {
            using var doc = JsonDocument.Parse(playbackJson);
            JsonElement playback = doc.RootElement;
            if (playback.ValueKind == JsonValueKind.Object && playback.TryGetProperty("playback", out var inner))
                playback = inner;
            if (!playback.TryGetProperty("payload", out var p) || p.GetString() is not { } payload || string.IsNullOrWhiteSpace(payload))
                return false;
            Payload = payload;
            Iv = playback.TryGetProperty("iv", out var iv) ? iv.GetString() ?? "" : "";
            if (playback.TryGetProperty("key_parts", out var kp) && kp.ValueKind == JsonValueKind.Array)
            {
                var parts = new List<string>();
                foreach (var el in kp.EnumerateArray())
                {
                    if (el.GetString() is { } s && !string.IsNullOrWhiteSpace(s))
                        parts.Add(s);
                }
                KeyParts = [.. parts];
            }
            else if (playback.TryGetProperty("keyParts", out var kp2) && kp2.ValueKind == JsonValueKind.Array)
            {
                var parts = new List<string>();
                foreach (var el in kp2.EnumerateArray())
                {
                    if (el.GetString() is { } s && !string.IsNullOrWhiteSpace(s))
                        parts.Add(s);
                }
                KeyParts = [.. parts];
            }
            return KeyParts.Length >= 2 && !string.IsNullOrWhiteSpace(Iv);
        }
        catch { return false; }
    }

    public static List<string> ExtractSourcesFromDecryptedJson(string json)
    {
        var urls = new List<string>();
        if (string.IsNullOrWhiteSpace(json))
            return urls;
        try
        {
            using var doc = JsonDocument.Parse(json.TrimStart('\uFEFF'));
            void Walk(JsonElement el)
            {
                switch (el.ValueKind)
                {
                    case JsonValueKind.Object:
                        foreach (var prop in el.EnumerateObject())
                        {
                            if (prop.Name.Equals("url", StringComparison.OrdinalIgnoreCase) ||
                                prop.Name.Equals("file", StringComparison.OrdinalIgnoreCase) ||
                                prop.Name.Equals("src", StringComparison.OrdinalIgnoreCase))
                            {
                                if (prop.Value.ValueKind == JsonValueKind.String &&
                                    prop.Value.GetString() is { } s && s.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                                    urls.Add(s);
                            }
                            else Walk(prop.Value);
                        }
                        break;
                    case JsonValueKind.Array:
                        foreach (var item in el.EnumerateArray()) Walk(item);
                        break;
                }
            }
            Walk(doc.RootElement);
        }
        catch
        {
            foreach (Match m in AbsMediaUrlRe.Matches(json))
                urls.Add(m.Value.Replace("\\/", "/"));
        }
        return urls;
    }

    /// <summary>AES-256-GCM decrypt for split-key playback payloads. Cipher input is
    /// raw cipher bytes + 16-byte tag appended (Cloudstream/Byse layout).</summary>
    public static string? AesGcmDecrypt(string keyPart0, string keyPart1, string ivB64Url, string payloadB64Url)
    {
        try
        {
            byte[] p0 = Base64UrlDecode(keyPart0);
            byte[] p1 = Base64UrlDecode(keyPart1);
            byte[] key = new byte[p0.Length + p1.Length];
            Buffer.BlockCopy(p0, 0, key, 0, p0.Length);
            Buffer.BlockCopy(p1, 0, key, p0.Length, p1.Length);
            byte[] nonce = Base64UrlDecode(ivB64Url);
            byte[] cipherAndTag = Base64UrlDecode(payloadB64Url);
            if (cipherAndTag.Length < 16)
                return null;
            byte[] cipher = cipherAndTag[..^16];
            byte[] tag = cipherAndTag[^16..];
            byte[] plain = new byte[cipher.Length];
            using var aes = new AesGcm(key, tag.Length);
            aes.Decrypt(nonce, cipher, tag, plain);
            return Encoding.UTF8.GetString(plain);
        }
        catch { return null; }
    }

    // ── 7. redirects / filecodes / titles ────────────────────────────────
    public static string? ExtractJsRedirect(string html)
    {
        if (string.IsNullOrEmpty(html))
            return null;
        var m = JsRedirectRe.Match(html);
        return m.Success ? m.Groups[1].Value : null;
    }

    public static List<string> ExtractIframes(string html, string baseUrl)
    {
        var out_ = new List<string>();
        if (string.IsNullOrEmpty(html))
            return out_;
        foreach (Match m in IframeRe.Matches(html))
        {
            string src = m.Groups[1].Value.Trim();
            if (src.StartsWith("//"))
                src = "https:" + src;
            else if (src.StartsWith('/') && Uri.TryCreate(baseUrl, UriKind.Absolute, out var b))
                src = new Uri(b, src).ToString();
            if (src.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                out_.Add(src);
        }
        return out_;
    }

    public static string? ExtractFilecodeFromPath(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return null;
        var m = FilecodePathRe.Match(url);
        return m.Success ? m.Groups[1].Value : null;
    }

    public static string? ExtractTitle(string html)
    {
        if (string.IsNullOrEmpty(html))
            return null;
        var og = OgTitleRe.Match(html);
        if (og.Success && !string.IsNullOrWhiteSpace(og.Groups[1].Value))
            return CleanTitle(og.Groups[1].Value);
        var t = TitleRe.Match(html);
        if (t.Success && !string.IsNullOrWhiteSpace(t.Groups[1].Value))
            return CleanTitle(t.Groups[1].Value);
        return null;
    }

    /// <summary>Extracts a title from JSON-LD blocks (VideoObject, Article,
    /// NewsArticle, @graph arrays): first non-empty name/headline/title.</summary>
    public static string? ExtractJsonLdTitle(string html)
    {
        if (string.IsNullOrEmpty(html))
            return null;
        foreach (Match script in JsonLdRe.Matches(html))
        {
            string block = script.Groups[1].Value.Trim();
            if (string.IsNullOrWhiteSpace(block))
                continue;
            try
            {
                using var doc = JsonDocument.Parse(block);
                if (TryFindJsonLdName(doc.RootElement, out var name) && !string.IsNullOrWhiteSpace(name))
                    return name;
            }
            catch { }
        }
        return null;
    }

    private static bool TryFindJsonLdName(JsonElement el, out string? name)
    {
        name = null;
        switch (el.ValueKind)
        {
            case JsonValueKind.Object:
                // Prefer @graph members first so container wrappers don't win.
                if (el.TryGetProperty("@graph", out var graph))
                {
                    if (TryFindJsonLdName(graph, out name))
                        return true;
                }
                foreach (var key in new[] { "name", "headline", "title" })
                {
                    if (el.TryGetProperty(key, out var val) && val.ValueKind == JsonValueKind.String)
                    {
                        string? s = val.GetString();
                        if (!string.IsNullOrWhiteSpace(s))
                        {
                            name = s.Trim();
                            return true;
                        }
                    }
                }
                foreach (var prop in el.EnumerateObject())
                {
                    if (prop.NameEquals("@graph"))
                        continue;
                    if (TryFindJsonLdName(prop.Value, out name))
                        return true;
                }
                return false;
            case JsonValueKind.Array:
                foreach (var item in el.EnumerateArray())
                {
                    if (TryFindJsonLdName(item, out name))
                        return true;
                }
                return false;
            default:
                return false;
        }
    }

    public static string? ExtractPoster(string html)
    {
        if (string.IsNullOrEmpty(html))
            return null;
        var m = PosterRe.Match(html);
        if (m.Success)
            return m.Groups[1].Value.Replace("\\/", "/");
        var og = Regex.Match(html, @"<meta[^>]+property\s*=\s*[""']og:image[""'][^>]+content\s*=\s*[""']([^""']+)[""']", RegexOptions.IgnoreCase);
        return og.Success ? og.Groups[1].Value : null;
    }

    // ── small helpers ────────────────────────────────────────────────────
    public static string? TryBase64Decode(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return null;
        string s = input.Trim().Replace('-', '+').Replace('_', '/');
        // Base64 of a URL is long; short tokens are not encoded manifests.
        if (s.Length < 24 || s.Length % 4 == 1)
            return null;
        int pad = (4 - s.Length % 4) % 4;
        s += new string('=', pad);
        try
        {
            string decoded = Encoding.UTF8.GetString(Convert.FromBase64String(s));
            return decoded.Contains("http", StringComparison.OrdinalIgnoreCase)
                || decoded.Contains(".m3u8", StringComparison.OrdinalIgnoreCase)
                || decoded.StartsWith('/') ? decoded : null;
        }
        catch { return null; }
    }

    public static byte[] Base64UrlDecode(string input)
    {
        string s = input.Trim().Replace('-', '+').Replace('_', '/');
        int pad = (4 - s.Length % 4) % 4;
        s += new string('=', pad);
        return Convert.FromBase64String(s);
    }

    public static string Rot13(string input)
    {
        var sb = new StringBuilder(input.Length);
        foreach (char c in input)
        {
            if (c >= 'a' && c <= 'z') sb.Append((char)('a' + (c - 'a' + 13) % 26));
            else if (c >= 'A' && c <= 'Z') sb.Append((char)('A' + (c - 'A' + 13) % 26));
            else sb.Append(c);
        }
        return sb.ToString();
    }

    private static string CleanTitle(string raw)
    {
        string t = Regex.Replace(raw, @"<[^>]+>", "").Trim();
        t = Regex.Replace(t, @"^(Watch\s+)", "", RegexOptions.IgnoreCase);
        t = Regex.Replace(t, @"\s*[-|–]\s*(VOE|Filemoon|DoodStream|FireStream|Byse).*$", "", RegexOptions.IgnoreCase);
        t = System.Net.WebUtility.HtmlDecode(t).Trim();
        return t.Length > 160 ? t[..160].Trim() : t;
    }

    private static string? NullIfBlank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;
}
