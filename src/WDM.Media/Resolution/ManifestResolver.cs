using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using WDM.Services;

namespace WDM.Media;

/// <summary>LEVEL 3 — manifest resolution. Turns a manifest URL into structured
/// variants using the shared HLS parser; DASH yields one best-effort candidate
/// (full variant enumeration comes with the Engine split). DRM is detected and
/// reported, never bypassed.</summary>
internal static class ManifestResolver
{
    private const int ManifestCapBytes = 512 * 1024;

    internal static async Task<(List<MediaVariant> Variants, bool DrmProtected)> ExpandAsync(
        HttpClient http, string manifestUrl, bool dash, string? referer,
        Dictionary<string, string>? headers, CancellationToken ct)
    {
        var variants = new List<MediaVariant>();
        if (NetworkGuard.IsBlockedResolveTarget(manifestUrl))
            return (variants, false);

        try
        {
            if (!dash)
            {
                var parsed = await HlsDownloader.ParseMasterVariantsAsync(http, manifestUrl, referer, headers, ct)
                    .ConfigureAwait(false);
                foreach (var v in parsed)
                {
                    variants.Add(new MediaVariant
                    {
                        Label = string.IsNullOrWhiteSpace(v.Label) ? "HLS stream" : v.Label,
                        MediaUrl = v.VariantUrl,
                        ManifestUrl = manifestUrl,
                        RequiresHls = true,
                        Container = "ts",
                        Height = v.Height,
                        Confidence = 0.85,
                        Evidence = new List<string> { "HLS master playlist: " + parsed.Count + " variant(s)" },
                    });
                }
                if (variants.Count > 0)
                    return (variants, false);

                // Not a master playlist: single media playlist or raw segments.
                string text = await GetCappedTextAsync(http, manifestUrl, referer, headers, ct).ConfigureAwait(false);
                if (IsSampleAes(text))
                    return (variants, true);
                if (text.Contains("#EXTM3U"))
                {
                    variants.Add(new MediaVariant
                    {
                        Label = "HLS stream",
                        MediaUrl = manifestUrl,
                        ManifestUrl = manifestUrl,
                        RequiresHls = true,
                        Container = "ts",
                        Confidence = 0.8,
                        Evidence = new List<string> { "HLS media playlist" },
                    });
                }
                return (variants, false);
            }

            string mpd = await GetCappedTextAsync(http, manifestUrl, referer, headers, ct).ConfigureAwait(false);
            if (mpd.Contains("<ContentProtection", StringComparison.Ordinal))
                return (variants, true);
            if (mpd.Contains("<MPD", StringComparison.Ordinal) || mpd.Contains("<mpd", StringComparison.OrdinalIgnoreCase))
            {
                variants.Add(new MediaVariant
                {
                    Label = "DASH stream",
                    MediaUrl = manifestUrl,
                    ManifestUrl = manifestUrl,
                    RequiresDash = true,
                    Container = "mpd",
                    Confidence = 0.7,
                    Evidence = new List<string> { "DASH manifest" },
                });
            }
            return (variants, false);
        }
        catch
        {
            return (variants, false);
        }
    }

    private static bool IsSampleAes(string text) =>
        text.Contains("SAMPLE-AES", StringComparison.Ordinal);

    private static async Task<string> GetCappedTextAsync(HttpClient http, string url, string? referer,
        Dictionary<string, string>? headers, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        if (!string.IsNullOrWhiteSpace(referer))
        {
            try { req.Headers.Referrer = new Uri(referer); } catch { }
        }
        if (headers is not null)
        {
            foreach (var kv in headers)
            {
                if (kv.Key.StartsWith("X-WDM-", StringComparison.OrdinalIgnoreCase))
                    continue;
                try { req.Headers.TryAddWithoutValidation(kv.Key, kv.Value); } catch { }
            }
        }
        using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            return "";
        using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var ms = new System.IO.MemoryStream();
        var buf = new byte[16 * 1024];
        int read, total = 0;
        while ((read = await stream.ReadAsync(buf, 0, buf.Length, ct).ConfigureAwait(false)) > 0)
        {
            int take = Math.Min(read, ManifestCapBytes - total);
            ms.Write(buf, 0, take);
            total += take;
            if (total >= ManifestCapBytes)
                break;
        }
        return System.Text.Encoding.UTF8.GetString(ms.ToArray());
    }
}
