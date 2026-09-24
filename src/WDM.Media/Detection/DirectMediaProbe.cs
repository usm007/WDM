using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using WDM.Services;

namespace WDM.Media;

/// <summary>LEVEL 0 — direct media URL. Evidence-based, never extension-only:
/// a HEAD (GET-range fallback) must confirm a media/manifest content-type.
/// Manifest hits are reported for L3 expansion, not as raw downloads.</summary>
internal static class DirectMediaProbe
{
    internal static async Task<MediaVariant?> ProbeAsync(HttpClient http, string url, CancellationToken ct)
    {
        if (NetworkGuard.IsBlockedResolveTarget(url))
            return null;

        HttpResponseMessage? resp = null;
        try
        {
            using var head = new HttpRequestMessage(HttpMethod.Head, url);
            resp = await http.SendAsync(head, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (resp.StatusCode == HttpStatusCode.MethodNotAllowed
                || resp.StatusCode == HttpStatusCode.NotImplemented
                || resp.StatusCode == HttpStatusCode.BadRequest
                || resp.Content.Headers.ContentType is null)
            {
                resp.Dispose();
                var get = new HttpRequestMessage(HttpMethod.Get, url);
                get.Headers.Range = new RangeHeaderValue(0, 1);
                resp = await http.SendAsync(get, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            }

            string contentType = resp.Content.Headers.ContentType?.MediaType?.Trim().ToLowerInvariant() ?? "";
            if (!resp.IsSuccessStatusCode || string.IsNullOrEmpty(contentType))
                return null;

            string? path = null;
            try { path = new Uri(url).AbsolutePath.ToLowerInvariant(); } catch { }

            if (IsManifestContentType(contentType) || IsManifestPath(path))
                return ManifestVariant(url, contentType, resp);

            if (IsDirectContentType(contentType))
                return DirectVariant(url, contentType, resp);

            return null;
        }
        catch
        {
            return null;
        }
        finally
        {
            resp?.Dispose();
        }
    }

    private static bool IsManifestContentType(string ct) =>
        ct.Contains("mpegurl") || ct.Contains("m3u8") || ct.Contains("x-mpegurl")
        || ct.Contains("dash+xml") || ct.Contains("ms-sstr");

    private static bool IsManifestPath(string? path) =>
        path is not null && (path.EndsWith(".m3u8") || path.EndsWith(".mpd"));

    private static bool IsDirectContentType(string ct) =>
        (ct.StartsWith("video/") || ct.StartsWith("audio/"))
        && !IsManifestContentType(ct);

    private static MediaVariant ManifestVariant(string url, string contentType, HttpResponseMessage resp)
    {
        bool dash = contentType.Contains("dash") || contentType.Contains("ms-sstr") || url.EndsWith(".mpd");
        var evidence = new List<string> { "manifest content-type: " + contentType };
        AddTransportEvidence(evidence, resp);
        return new MediaVariant
        {
            Label = dash ? "DASH stream" : "HLS stream",
            MediaUrl = url,
            ManifestUrl = url,
            RequiresHls = !dash,
            RequiresDash = dash,
            Container = dash ? "mpd" : "m3u8",
            Confidence = 0.8,
            Evidence = evidence,
        };
    }

    private static MediaVariant DirectVariant(string url, string contentType, HttpResponseMessage resp)
    {
        var evidence = new List<string> { "direct media content-type: " + contentType };
        AddTransportEvidence(evidence, resp);
        string? name = null;
        try
        {
            name = System.IO.Path.GetFileName(new Uri(url).AbsolutePath);
            if (!string.IsNullOrWhiteSpace(name))
                evidence.Add("path filename: " + name);
        }
        catch { }
        return new MediaVariant
        {
            Label = string.IsNullOrWhiteSpace(name) ? "Direct media" : name,
            MediaUrl = url,
            Container = contentType.Contains('/') ? contentType.Split('/').Last() : null,
            Confidence = RangeSupported(resp) ? 0.95 : 0.9,
            Evidence = evidence,
            EstimatedBytes = resp.Content.Headers.ContentLength is long len && len > 0 ? len : null,
        };
    }

    private static bool RangeSupported(HttpResponseMessage resp) =>
        resp.Headers.AcceptRanges.Any(v => v.Equals("bytes", StringComparison.OrdinalIgnoreCase));

    private static void AddTransportEvidence(List<string> evidence, HttpResponseMessage resp)
    {
        if (resp.Content.Headers.ContentLength is long len && len > 0)
            evidence.Add("content-length: " + len);
        if (RangeSupported(resp))
            evidence.Add("range supported");
        var disposition = resp.Content.Headers.ContentDisposition;
        if (disposition is not null && !string.IsNullOrWhiteSpace(disposition.FileNameStar ?? disposition.FileName))
            evidence.Add("content-disposition: " + (disposition.FileNameStar ?? disposition.FileName));
    }
}
