using System.Net;
using System.Net.Http.Headers;
using WDM.Services;
using WDM.Tests.TestInfrastructure;
using Xunit;

namespace WDM.Tests;

/// <summary>
/// Contract tests for the link resolver: name, size and transfer mode must be
/// negotiated from the server's actual answers, and failures must be named.
/// </summary>
[Trait("Category", Cats.Unit)]
public sealed class LinkResolverTests
{
    private sealed class ProbeOrigin : HttpMessageHandler
    {
        public Func<HttpRequestMessage, int, HttpResponseMessage> Respond =
            (_, _) => new HttpResponseMessage(HttpStatusCode.OK);
        public List<HttpRequestMessage> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request);
            var response = Respond(request, Requests.Count - 1);
            response.RequestMessage ??= request;
            return Task.FromResult(response);
        }
    }

    private static HttpResponseMessage Body(
        HttpStatusCode status,
        long? length = null,
        string? disposition = null,
        string? contentType = null,
        long? rangeTotal = null)
    {
        var response = new HttpResponseMessage(status);
        response.Content = new ByteArrayContent(Array.Empty<byte>());
        if (length is > 0)
            response.Content.Headers.ContentLength = length;
        if (disposition is not null)
            response.Content.Headers.TryAddWithoutValidation("Content-Disposition", disposition);
        if (contentType is not null)
            response.Content.Headers.TryAddWithoutValidation("Content-Type", contentType);
        if (rangeTotal is > 0)
            response.Content.Headers.ContentRange = new ContentRangeHeaderValue(0, 0, rangeTotal.Value);
        return response;
    }

    private static async Task<LinkProbe> ProbeAsync(
        ProbeOrigin origin,
        string url = "https://files.example.com/get",
        IReadOnlyDictionary<string, string>? headers = null,
        TimeSpan? timeout = null)
    {
        using var http = new HttpClient(origin)
        {
            Timeout = timeout ?? TimeSpan.FromSeconds(10),
        };
        return await LinkResolver.ProbeAsync(
            http,
            new LinkProbeRequest(url, headers),
            CancellationToken.None);
    }

    [Fact]
    public async Task HeadBlocked_RangeGet_ReportsSizeAndRanges()
    {
        var origin = new ProbeOrigin();
        origin.Respond = (request, _) => request.Method == HttpMethod.Head
            ? Body(HttpStatusCode.MethodNotAllowed)
            : Body(HttpStatusCode.PartialContent, rangeTotal: 5_000);

        var probe = await ProbeAsync(origin);

        Assert.Equal(5_000, probe.TotalBytes);
        Assert.True(probe.SupportsRanges);
        Assert.Equal(LinkProbeOutcome.File, probe.Outcome);
        Assert.Equal(2, origin.Requests.Count);
        Assert.NotNull(origin.Requests[1].Headers.Range);
    }

    [Fact]
    public async Task RangeIgnored_200_UsesContentLength_NoRanges()
    {
        var origin = new ProbeOrigin();
        origin.Respond = (_, _) => Body(HttpStatusCode.OK, length: 4_200);

        var probe = await ProbeAsync(origin);

        Assert.Equal(4_200, probe.TotalBytes);
        Assert.False(probe.SupportsRanges);
        Assert.Equal(LinkProbeOutcome.File, probe.Outcome);
    }

    [Fact]
    public async Task Range416_ReadsTotalFromContentRangeStar()
    {
        var origin = new ProbeOrigin();
        origin.Respond = (request, _) =>
        {
            if (request.Method == HttpMethod.Head)
                return Body(HttpStatusCode.OK, length: 9_000, contentType: "application/octet-stream");
            var response = new HttpResponseMessage(HttpStatusCode.RequestedRangeNotSatisfiable);
            response.Content = new ByteArrayContent(Array.Empty<byte>());
            response.Content.Headers.ContentRange = new ContentRangeHeaderValue(9_000);
            return response;
        };

        var probe = await ProbeAsync(origin);

        Assert.Equal(9_000, probe.TotalBytes);
    }

    [Fact]
    public async Task ServerFilename_Rfc5987_IsParsed()
    {
        var origin = new ProbeOrigin();
        origin.Respond = (_, _) => Body(
            HttpStatusCode.OK,
            length: 10,
            disposition: "attachment; filename*=UTF-8''%E2%82%AC%20rates.pdf");

        var probe = await ProbeAsync(origin);

        Assert.Equal("€ rates.pdf", probe.FileName);
    }

    [Fact]
    public async Task HtmlResponse_IsPage_NotFile()
    {
        var origin = new ProbeOrigin();
        origin.Respond = (_, _) => Body(
            HttpStatusCode.OK,
            length: 8_000,
            contentType: "text/html; charset=utf-8");

        var probe = await ProbeAsync(origin);

        Assert.Equal(LinkProbeOutcome.Page, probe.Outcome);
        Assert.Equal(-1, probe.TotalBytes);
    }

    [Fact]
    public async Task CapturedUserAgent_SentExactlyOnce()
    {
        var origin = new ProbeOrigin();
        origin.Respond = (_, _) => Body(HttpStatusCode.OK, length: 100);

        await ProbeAsync(origin, headers: new Dictionary<string, string>
        {
            ["User-Agent"] = "CapturedBrowser/9.9",
        });

        foreach (var request in origin.Requests)
        {
            Assert.True(request.Headers.TryGetValues("User-Agent", out var values));
            Assert.Single(values!);
            Assert.Equal("CapturedBrowser/9.9", values!.Single());
        }
    }

    [Fact]
    public async Task Forbidden_ReportsAuthRequired_NotUnknown()
    {
        var origin = new ProbeOrigin();
        origin.Respond = (_, _) => new HttpResponseMessage(HttpStatusCode.Forbidden);

        var probe = await ProbeAsync(origin);

        Assert.Equal(LinkProbeOutcome.AuthRequired, probe.Outcome);
        Assert.Equal(403, probe.StatusCode);
        Assert.Equal(-1, probe.TotalBytes);
    }

    [Fact]
    public async Task Playlist_ReportsStream_WithoutMediaSize()
    {
        var origin = new ProbeOrigin();
        origin.Respond = (_, _) => Body(
            HttpStatusCode.OK,
            length: 300,
            contentType: "application/vnd.apple.mpegurl");

        var probe = await ProbeAsync(origin, "https://cdn.example.com/master.m3u8");

        Assert.Equal(LinkProbeOutcome.Stream, probe.Outcome);
        Assert.Equal(-1, probe.TotalBytes);
    }

    [Fact]
    public async Task Timeout_ReportsTimeout()
    {
        var origin = new ProbeOrigin();
        origin.Respond = (_, _) => throw new TaskCanceledException("probe timeout");

        var probe = await ProbeAsync(origin, timeout: TimeSpan.FromMilliseconds(50));

        Assert.Equal(LinkProbeOutcome.Timeout, probe.Outcome);
    }

    [Fact]
    public async Task ChunkedHead_NoLength_FallsBackToRange()
    {
        var origin = new ProbeOrigin();
        origin.Respond = (request, _) => request.Method == HttpMethod.Head
            ? Body(HttpStatusCode.OK, contentType: "application/zip")
            : Body(HttpStatusCode.PartialContent, rangeTotal: 777);

        var probe = await ProbeAsync(origin);

        Assert.Equal(777, probe.TotalBytes);
        Assert.True(probe.SupportsRanges);
    }

    [Fact]
    public async Task NameInQuery_UsedWhenNoDisposition()
    {
        var origin = new ProbeOrigin();
        origin.Respond = (_, _) => Body(HttpStatusCode.OK, length: 12);

        var probe = await ProbeAsync(
            origin,
            "https://storage.example.com/0?response-content-disposition=attachment%3B%20filename%3D%22report.pdf%22");

        Assert.Equal("report.pdf", probe.FileName);
    }

    [Fact]
    public async Task FinalUrl_ComesFromTheAnsweredRequest()
    {
        var origin = new ProbeOrigin();
        var finalUri = new Uri("https://cdn.example.com/real/file.zip");
        origin.Respond = (_, _) =>
        {
            var response = Body(HttpStatusCode.OK, length: 5, disposition: "attachment; filename=file.zip");
            response.RequestMessage = new HttpRequestMessage(HttpMethod.Head, finalUri);
            return response;
        };

        var probe = await ProbeAsync(origin);

        Assert.Equal(finalUri.ToString(), probe.FinalUrl);
    }
}
