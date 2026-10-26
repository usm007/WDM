using System.Net;
using System.Net.Http.Headers;

namespace WDM.Services;

/// <summary>What a link turned out to be.</summary>
public enum LinkProbeOutcome
{
    /// <summary>A downloadable file (size may still be unknown).</summary>
    File,
    /// <summary>An HTML page, not a file. Never carry a size.</summary>
    Page,
    /// <summary>A playlist/manifest. Its byte length is not the media size.</summary>
    Stream,
    /// <summary>The server demands a session the probe did not have.</summary>
    AuthRequired,
    /// <summary>The server refused the probe (rate limit / bot wall).</summary>
    Blocked,
    /// <summary>No answer inside the probe budget.</summary>
    Timeout,
    /// <summary>Nothing usable came back.</summary>
    Failed,
}

/// <summary>Inputs for <see cref="LinkResolver.ProbeAsync"/>.</summary>
public sealed record LinkProbeRequest(
    string Url,
    IReadOnlyDictionary<string, string>? Headers = null,
    string? Referer = null,
    long MaxBytes = 0);

/// <summary>The negotiated truth about a link, plus why it is what it is.</summary>
public sealed record LinkProbe(
    string Url,
    string? FinalUrl,
    int StatusCode,
    long TotalBytes,
    bool SupportsRanges,
    string? FileName,
    string? ContentType,
    LinkProbeOutcome Outcome,
    string? Detail);

/// <summary>
/// Negotiates a link's filename, size and transfer mode the way IDM does:
/// headers are authority, a refused HEAD is retried as a one-byte ranged GET,
/// and every failure is named instead of being shown as "unknown".
/// </summary>
public static class LinkResolver
{
    /// <summary>Upper bound for any adopted total (64 GiB). Bigger values stay unknown.</summary>
    public const long DefaultMaxBytes = 64L * 1024 * 1024 * 1024;

    /// <summary>Used only when the caller captured no browser User-Agent.</summary>
    public const string DefaultUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/125.0 Safari/537.36";

    public static HttpClient CreateClient(IWebProxy? proxy, TimeSpan timeout)
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            UseCookies = false,
            Proxy = proxy,
            UseProxy = proxy is not null,
        };
        return new HttpClient(handler) { Timeout = timeout };
    }

    public static async Task<LinkProbe> ProbeAsync(
        HttpClient http,
        LinkProbeRequest request,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        long max = request.MaxBytes > 0 ? request.MaxBytes : DefaultMaxBytes;
        var headers = NormalizeHeaders(request);

        HttpResponseMessage? head = null;
        HttpResponseMessage? range = null;
        bool timedOut = false;

        try
        {
            head = await SendAsync(http, HttpMethod.Head, request.Url, headers, null, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            timedOut = true;
        }
        catch (HttpRequestException)
        {
            // Some origins reset HEAD; the ranged GET below is the fallback.
        }

        bool headIsHtml = head is not null && IsHtml(head);
        bool headIsStream = head is not null && IsStream(head, request.Url);
        long headTotal = head is null ? -1 : UsableLength(head, max);
        bool headRanges = head is not null && AcceptsRanges(head);
        string? headName = head is null ? null : NameFrom(head);

        // A HEAD that already answers everything needs no second request.
        bool headComplete = head is not null
            && IsSuccess(head.StatusCode)
            && !headIsHtml
            && !headIsStream
            && headTotal > 0
            && headRanges;

        if (!headComplete && !headIsStream)
        {
            try
            {
                range = await SendAsync(
                    http,
                    HttpMethod.Get,
                    request.Url,
                    headers,
                    new RangeHeaderValue(0, 0),
                    ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                timedOut = true;
            }
            catch (HttpRequestException)
            {
            }
        }

        HttpResponseMessage? last = range ?? head;
        int status = last is null ? 0 : (int)last.StatusCode;

        bool isStream = headIsStream || (range is not null && IsStream(range, request.Url));
        bool isHtml = !isStream && (headIsHtml && (range is null || IsHtml(range)));
        string? contentType = last?.Content?.Headers?.ContentType?.ToString()
            ?? head?.Content?.Headers?.ContentType?.ToString();

        long total = ResolveTotal(head, range, max);
        bool supportsRanges = (range is not null && range.StatusCode == HttpStatusCode.PartialContent)
            || headRanges;

        string? fileName = headName ?? (range is null ? null : NameFrom(range));
        fileName ??= FileNameHelper.FileNameFromS3Query(request.Url);

        string? finalUrl = last?.RequestMessage?.RequestUri?.ToString();

        LinkProbeOutcome outcome;
        string? detail;

        if (isStream)
        {
            total = -1;
            outcome = LinkProbeOutcome.Stream;
            detail = "Playlist/manifest: the media size is resolved on download.";
        }
        else if (isHtml)
        {
            total = -1;
            supportsRanges = false;
            outcome = LinkProbeOutcome.Page;
            detail = "The server returned an HTML page, not a file.";
        }
        else if (timedOut && last is null)
        {
            outcome = LinkProbeOutcome.Timeout;
            detail = "The server did not answer in time.";
        }
        else if (total <= 0 && (status is 401 or 403 or 407))
        {
            supportsRanges = false;
            outcome = LinkProbeOutcome.AuthRequired;
            detail = $"The server needs the browser session (HTTP {status}).";
        }
        else if (total <= 0 && status == 429)
        {
            supportsRanges = false;
            outcome = LinkProbeOutcome.Blocked;
            detail = "The server refused the probe (HTTP 429). Retry shortly.";
        }
        else if (total > 0 || fileName is not null)
        {
            outcome = LinkProbeOutcome.File;
            detail = total > 0 ? null : "The server did not reveal a size.";
        }
        else if (last is not null && IsSuccess(last.StatusCode))
        {
            outcome = LinkProbeOutcome.File;
            detail = "The server did not reveal a size.";
        }
        else
        {
            outcome = LinkProbeOutcome.Failed;
            detail = status == 0
                ? "The server could not be reached."
                : $"The server answered HTTP {status}.";
        }

        head?.Dispose();
        if (!ReferenceEquals(range, head))
            range?.Dispose();

        return new LinkProbe(
            request.Url,
            finalUrl,
            status,
            total,
            supportsRanges,
            fileName,
            contentType,
            outcome,
            detail);
    }

    private static Dictionary<string, string> NormalizeHeaders(LinkProbeRequest request)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (request.Headers is not null)
        {
            foreach (var pair in request.Headers)
            {
                if (!string.IsNullOrWhiteSpace(pair.Key) && !string.IsNullOrWhiteSpace(pair.Value))
                    headers[pair.Key] = pair.Value;
            }
        }

        // One User-Agent, always. A captured browser UA wins; the default is a
        // last resort. Sending both is what makes Cloudflare reject the probe.
        if (!headers.ContainsKey("User-Agent"))
            headers["User-Agent"] = DefaultUserAgent;

        if (!string.IsNullOrWhiteSpace(request.Referer) && !headers.ContainsKey("Referer"))
            headers["Referer"] = request.Referer;

        return headers;
    }

    private static async Task<HttpResponseMessage> SendAsync(
        HttpClient http,
        HttpMethod method,
        string url,
        IReadOnlyDictionary<string, string> headers,
        RangeHeaderValue? range,
        CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, url);
        if (range is not null)
            request.Headers.Range = range;

        foreach (var pair in headers)
        {
            // Framing headers are the transport's business, never the caller's.
            if (pair.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)
                || pair.Key.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase)
                || pair.Key.Equals("Host", StringComparison.OrdinalIgnoreCase)
                || pair.Key.Equals("Range", StringComparison.OrdinalIgnoreCase)
                || pair.Key.Equals("Connection", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            request.Headers.TryAddWithoutValidation(pair.Key, pair.Value);
        }

        return await http
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);
    }

    private static long ResolveTotal(HttpResponseMessage? head, HttpResponseMessage? range, long max)
    {
        if (range is not null)
        {
            if (range.StatusCode == HttpStatusCode.PartialContent)
            {
                long fromRange = ContentRangeTotal(range, max);
                if (fromRange > 0)
                    return fromRange;
            }
            else if (range.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
            {
                long fromStar = ContentRangeTotal(range, max);
                if (fromStar > 0)
                    return fromStar;
            }
            else if (IsSuccess(range.StatusCode))
            {
                long fromLength = UsableLength(range, max);
                if (fromLength > 0)
                    return fromLength;
            }
        }

        return head is null ? -1 : UsableLength(head, max);
    }

    private static long UsableLength(HttpResponseMessage response, long max)
    {
        if (!IsSuccess(response.StatusCode) && response.StatusCode != HttpStatusCode.PartialContent)
            return -1;
        long length = response.Content?.Headers?.ContentLength ?? -1;
        return length > 0 && length <= max ? length : -1;
    }

    private static long ContentRangeTotal(HttpResponseMessage response, long max)
    {
        try
        {
            var cr = response.Content?.Headers?.ContentRange;
            if (cr is null || !cr.HasLength)
                return -1;
            long total = cr.Length ?? -1;
            return total > 0 && total <= max ? total : -1;
        }
        catch
        {
            return -1;
        }
    }

    private static bool AcceptsRanges(HttpResponseMessage response)
    {
        try
        {
            return response.Headers.AcceptRanges.Any(
                value => value.Equals("bytes", StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return false;
        }
    }

    private static string? NameFrom(HttpResponseMessage response)
    {
        try
        {
            if (response.Content is null
                || !response.Content.Headers.TryGetValues("Content-Disposition", out var values))
            {
                return null;
            }

            foreach (var raw in values)
            {
                string? parsed = FileNameHelper.ParseDispositionFileName(raw);
                if (!string.IsNullOrWhiteSpace(parsed))
                    return parsed;
            }
        }
        catch
        {
        }

        return null;
    }

    private static bool IsSuccess(HttpStatusCode status) => (int)status is >= 200 and < 300;

    private static bool IsHtml(HttpResponseMessage response)
    {
        string? mediaType = MediaTypeOf(response);
        return mediaType is not null
            && (mediaType.Equals("text/html", StringComparison.OrdinalIgnoreCase)
                || mediaType.Equals("application/xhtml+xml", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsStream(HttpResponseMessage response, string url)
    {
        string? mediaType = MediaTypeOf(response);
        if (mediaType is not null)
        {
            if (mediaType.Contains("mpegurl", StringComparison.OrdinalIgnoreCase)
                || mediaType.Contains("m3u8", StringComparison.OrdinalIgnoreCase)
                || mediaType.Contains("dash+xml", StringComparison.OrdinalIgnoreCase)
                || mediaType.Equals("application/mpd", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        try
        {
            string path = new Uri(url).AbsolutePath;
            return path.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(".mpd", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static string? MediaTypeOf(HttpResponseMessage response)
    {
        try
        {
            return response.Content?.Headers?.ContentType?.MediaType;
        }
        catch
        {
            return null;
        }
    }
}
