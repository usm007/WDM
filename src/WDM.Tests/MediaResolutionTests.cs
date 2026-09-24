// MediaResolutionTests — deterministic pipeline coverage (no network):
// L0 direct probe, L1 static detection, L3 manifest expansion + DRM,
// request mapping and sign-in classification. HTTP is stubbed at the
// handler level; public-looking example.com URLs satisfy NetworkGuard
// without DNS dependence (guard fails open on DNS errors).
using System.Net;
using System.Text;
using WDM.Media;
using WDM.Services;
using WDM.Tests.TestInfrastructure;
using Xunit;

namespace WDM.Tests;

public sealed class MediaResolutionTests
{
    private sealed class StubOrigin : HttpMessageHandler
    {
        public Func<HttpRequestMessage, HttpResponseMessage> Responder =
            _ => new HttpResponseMessage(HttpStatusCode.NotFound);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(Responder(request));
    }

    private static HttpResponseMessage Head(string contentType, long length = 0, bool ranges = false)
    {
        var r = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Array.Empty<byte>()) };
        r.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
        if (length > 0)
            r.Content.Headers.ContentLength = length;
        if (ranges)
            r.Headers.AcceptRanges.Add("bytes");
        return r;
    }

    private static HttpResponseMessage Text(string s, string contentType = "text/html")
    {
        var r = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(s, Encoding.UTF8, contentType)
        };
        return r;
    }

    [Trait("Category", Cats.Unit)][Fact]
    public async Task L0_DirectMp4_ResolvesWithEvidence()
    {
        var stub = new StubOrigin();
        stub.Responder = _ => Head("video/mp4", 1_000_000, ranges: true);
        using var http = new HttpClient(stub);
        var v = await DirectMediaProbe.ProbeAsync(http, "https://cdn.example.com/v.mp4", CancellationToken.None);
        Assert.NotNull(v);
        Assert.Equal("https://cdn.example.com/v.mp4", v.MediaUrl);
        Assert.True(v.Confidence >= 0.9);
        Assert.Contains(v.Evidence, e => e.Contains("video/mp4"));
        Assert.Contains(v.Evidence, e => e.Contains("range"));
        Assert.Equal(1_000_000, v.EstimatedBytes);
    }

    [Trait("Category", Cats.Unit)][Fact]
    public async Task L0_HeadRefused_FallsBackToRangeGet()
    {
        var stub = new StubOrigin();
        stub.Responder = req => req.Method == HttpMethod.Head
            ? new HttpResponseMessage(HttpStatusCode.MethodNotAllowed)
            : Head("video/webm", 500);
        using var http = new HttpClient(stub);
        var v = await DirectMediaProbe.ProbeAsync(http, "https://cdn.example.com/v.webm", CancellationToken.None);
        Assert.NotNull(v);
        Assert.False(v.RequiresHls);
    }

    [Trait("Category", Cats.Unit)][Fact]
    public async Task L0_HtmlPage_ReturnsNull()
    {
        var stub = new StubOrigin();
        stub.Responder = _ => Head("text/html", 300);
        using var http = new HttpClient(stub);
        Assert.Null(await DirectMediaProbe.ProbeAsync(http, "https://example.com/page", CancellationToken.None));
    }

    [Trait("Category", Cats.Unit)][Fact]
    public async Task L0_ManifestContentType_ReportsManifestForExpansion()
    {
        var stub = new StubOrigin();
        stub.Responder = _ => Head("application/vnd.apple.mpegurl", 200);
        using var http = new HttpClient(stub);
        var v = await DirectMediaProbe.ProbeAsync(http, "https://cdn.example.com/master.m3u8", CancellationToken.None);
        Assert.NotNull(v);
        Assert.True(v.RequiresHls);
        Assert.Equal("https://cdn.example.com/master.m3u8", v.ManifestUrl);
    }

    private const string PageHtml = """
        <html><head>
        <meta property="og:title" content="Test Film" />
        <meta property="og:video" content="/media/og.mp4" />
        <script type="application/ld+json">{"@type":"VideoObject","contentUrl":"https://cdn.example.com/ld.mp4"}</script>
        </head><body>
        <video src="https://cdn.example.com/tag.mp4" width="1280"></video>
        </body></html>
        """;

    [Trait("Category", Cats.Unit)][Fact]
    public async Task L1_StaticPage_FindsOgJsonLdAndTags()
    {
        var stub = new StubOrigin();
        stub.Responder = _ => Text(PageHtml);
        using var http = new HttpClient(stub);
        var hit = await StaticPageDetector.DetectAsync(http, "https://example.com/watch/1", CancellationToken.None);
        Assert.Equal("Test Film", hit.Title);
        Assert.Equal(3, hit.Candidates.Count);
        Assert.Contains(hit.Candidates, c => c.MediaUrl == "https://example.com/media/og.mp4");
        Assert.Contains(hit.Candidates, c => c.MediaUrl == "https://cdn.example.com/ld.mp4");
        var tag = hit.Candidates.First(c => c.MediaUrl == "https://cdn.example.com/tag.mp4");
        Assert.Equal(1280, tag.Width);
    }

    [Trait("Category", Cats.Unit)][Fact]
    public async Task L1_NonHtml_BodyIgnored()
    {
        var stub = new StubOrigin();
        stub.Responder = _ => Text("junk", "application/octet-stream");
        using var http = new HttpClient(stub);
        var hit = await StaticPageDetector.DetectAsync(http, "https://example.com/f.bin", CancellationToken.None);
        Assert.Empty(hit.Candidates);
    }

    private const string MasterM3U8 = """
        #EXTM3U
        #EXT-X-STREAM-INF:BANDWIDTH=2800000,RESOLUTION=1280x720
        720p.m3u8
        #EXT-X-STREAM-INF:BANDWIDTH=800000,RESOLUTION=640x360
        360p.m3u8
        """;

    [Trait("Category", Cats.Unit)][Fact]
    public async Task L3_HlsMaster_ExpandsVariants()
    {
        var stub = new StubOrigin();
        stub.Responder = _ => Text(MasterM3U8, "application/vnd.apple.mpegurl");
        using var http = new HttpClient(stub);
        var (variants, drm) = await ManifestResolver.ExpandAsync(
            http, "https://cdn.example.com/master.m3u8", false, null, null, CancellationToken.None);
        Assert.False(drm);
        Assert.Equal(2, variants.Count);
        Assert.All(variants, v => Assert.True(v.RequiresHls));
        Assert.Contains(variants, v => v.Height == 720);
    }

    [Trait("Category", Cats.Unit)][Fact]
    public async Task L3_SampleAes_ReportsDrm()
    {
        const string media = "#EXTM3U\n#EXT-X-KEY:METHOD=SAMPLE-AES,URI=\"key.bin\"\n#EXTINF:6,\nseg.ts\n";
        var stub = new StubOrigin();
        stub.Responder = _ => Text(media, "application/vnd.apple.mpegurl");
        using var http = new HttpClient(stub);
        var (variants, drm) = await ManifestResolver.ExpandAsync(
            http, "https://cdn.example.com/media.m3u8", false, null, null, CancellationToken.None);
        Assert.True(drm);
        Assert.Empty(variants);
    }

    [Trait("Category", Cats.Unit)][Fact]
    public async Task L3_DashContentProtection_ReportsDrm()
    {
        const string mpd = "<MPD><Period><AdaptationSet><ContentProtection schemeIdUri=\"urn:uuid:test\"/></AdaptationSet></Period></MPD>";
        var stub = new StubOrigin();
        stub.Responder = _ => Text(mpd, "application/dash+xml");
        using var http = new HttpClient(stub);
        var (variants, drm) = await ManifestResolver.ExpandAsync(
            http, "https://cdn.example.com/manifest.mpd", true, null, null, CancellationToken.None);
        Assert.True(drm);
        Assert.Empty(variants);
    }

    [Trait("Category", Cats.Unit)][Fact]
    public async Task L3_PlainDash_YieldsSingleCandidate()
    {
        const string mpd = "<MPD><Period><AdaptationSet mimeType=\"video/mp4\"></AdaptationSet></Period></MPD>";
        var stub = new StubOrigin();
        stub.Responder = _ => Text(mpd, "application/dash+xml");
        using var http = new HttpClient(stub);
        var (variants, drm) = await ManifestResolver.ExpandAsync(
            http, "https://cdn.example.com/manifest.mpd", true, null, null, CancellationToken.None);
        Assert.False(drm);
        Assert.Single(variants);
        Assert.True(variants[0].RequiresDash);
    }

    [Trait("Category", Cats.Unit)][Fact]
    public void ToDownloadRequest_MapsBestVariant()
    {
        var res = new MediaResolution
        {
            SourceUrl = "https://example.com/watch/1",
            Status = ResolutionStatus.Resolved,
            Title = "Film",
            Referer = "https://example.com/",
            Headers = new Dictionary<string, string> { ["Cookie"] = "a=b" },
            Variants = new List<MediaVariant>
            {
                new() { Label = "720p", MediaUrl = "https://cdn.example.com/720.mp4", Height = 720, Confidence = 0.9 },
            },
        };
        var req = ResolutionPipeline.ToDownloadRequest(res);
        Assert.Equal("https://cdn.example.com/720.mp4", req.MediaUrl);
        Assert.Equal("Film", req.Title);
        Assert.Equal("https://example.com/", req.Referer);
        Assert.Equal("a=b", req.Headers["Cookie"]);
        Assert.Equal(MediaKind.Video, req.MediaKind);
    }

    [Trait("Category", Cats.Unit)][Theory]
    [InlineData("Sign in to confirm you're not a bot", true)]
    [InlineData("use --cookies-from-browser chrome", true)]
    [InlineData("Video unavailable", false)]
    public void SignInKeywords_Classified(string msg, bool login)
    {
        Assert.Equal(login, ResolutionPipeline.IsSignInError(msg));
    }
}
