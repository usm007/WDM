using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;

namespace WDM.Services;

/// <summary>Result of a quick capability probe on a candidate download URL.</summary>
public sealed record UrlProbeResult
{
    public bool IsFile { get; init; }
    public string FileName { get; init; } = "";
    public long SizeBytes { get; init; }
    public bool SupportsRanges { get; init; }
    public string ContentType { get; init; } = "";
}

/// <summary>
/// Lightweight HEAD + range probe that decides whether a copied URL points at a
/// real downloadable file (vs. a web page, an expired link, or a dead endpoint).
/// </summary>
public static class UrlProbe
{
    public static async Task<UrlProbeResult?> ProbeAsync(string url, CancellationToken ct = default)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(6) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) WDM/1.0");

            var head = new HttpRequestMessage(HttpMethod.Head, url);
            using var headResp = await http.SendAsync(head, HttpCompletionOption.ResponseHeadersRead, ct);

            long totalBytes = headResp.Content.Headers.ContentLength ?? -1;
            bool supportsRanges = headResp.Headers.AcceptRanges.Any(r => r.Equals("bytes", StringComparison.OrdinalIgnoreCase));
            string contentType = headResp.Content.Headers.ContentType?.MediaType ?? "";
            string? contentDisposition = headResp.Content.Headers.ContentDisposition?.FileName;

            if (totalBytes <= 0 || !supportsRanges)
            {
                // Fallback probe: single-byte range GET reveals size + range support.
                var range = new HttpRequestMessage(HttpMethod.Get, url);
                range.Headers.Range = new RangeHeaderValue(0, 0);
                using var rangeResp = await http.SendAsync(range, HttpCompletionOption.ResponseHeadersRead, ct);
                supportsRanges = rangeResp.StatusCode == HttpStatusCode.PartialContent;
                if (totalBytes <= 0 && rangeResp.Content.Headers.ContentRange?.Length is long len)
                    totalBytes = len;
                if (string.IsNullOrEmpty(contentType))
                    contentType = rangeResp.Content.Headers.ContentType?.MediaType ?? "";
                if (string.IsNullOrEmpty(contentDisposition))
                    contentDisposition = rangeResp.Content.Headers.ContentDisposition?.FileName;
            }

            if (ct.IsCancellationRequested)
                return null;

            string fileName = DeriveFileName(url, contentDisposition, contentType);
            bool isFile = LooksLikeDownloadableFile(contentType, totalBytes, fileName);

            return new UrlProbeResult
            {
                IsFile = isFile,
                FileName = fileName,
                SizeBytes = Math.Max(0, totalBytes),
                SupportsRanges = supportsRanges,
                ContentType = contentType,
            };
        }
        catch
        {
            return null;
        }
    }

    private static string DeriveFileName(string url, string? dispositionName, string contentType)
    {
        string? name = null;

        // 1) Content-Disposition filename (RFC 2231 / plain).
        if (!string.IsNullOrWhiteSpace(dispositionName))
            name = dispositionName.Trim('"');
        if (string.IsNullOrWhiteSpace(name))
            name = FileNameHelper.ParseDispositionFileName(dispositionName);

        // 2) S3 / signed-URL query param (response-content-disposition=...).
        if (string.IsNullOrWhiteSpace(name))
            name = FileNameHelper.FileNameFromS3Query(url);

        // 3) URL path basename.
        if (string.IsNullOrWhiteSpace(name)
            && Uri.TryCreate(url, UriKind.Absolute, out var uri)
            && !string.IsNullOrWhiteSpace(uri.LocalPath))
        {
            name = Path.GetFileName(Uri.UnescapeDataString(uri.LocalPath));
        }

        // 4) Content-Type fallback: give the file a proper extension instead of .bin.
        string ext = FileNameHelper.ExtensionFromMime(contentType);
        name = FileNameHelper.EnsureExtension(name ?? "", ext, DateTime.Now);

        if (string.IsNullOrWhiteSpace(name))
            name = $"download_{DateTime.Now:yyyyMMdd_HHmmss}.bin";

        return DownloadEngine.SanitizeFileName(name);
    }

    private static bool LooksLikeDownloadableFile(string contentType, long totalBytes, string fileName)
    {
        string ct = contentType.ToLowerInvariant();
        bool isWebPage = ct.Contains("text/html") || ct.Contains("application/xhtml") || ct.Contains("text/plain");
        bool hasFileExt = Path.GetExtension(fileName).Length >= 2;
        bool knownSize = totalBytes > 0;
        bool binaryType = ct.Contains("octet-stream") || ct.Contains("video") || ct.Contains("audio")
            || ct.Contains("image") || ct.Contains("zip") || ct.Contains("pdf") || ct.Contains("rar")
            || ct.Contains("7z") || ct.Contains("tar") || ct.Contains("application/x-");
        return !isWebPage && (hasFileExt || knownSize || binaryType);
    }
}
