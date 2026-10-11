// Size-pipeline regression tests (no network): confirmed vs observed vs
// unknown size semantics for the remote file-size detection pipeline.
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using WDM.Services;
using WDM.Tests.TestInfrastructure;
using Xunit;

namespace WDM.Tests;

public sealed class SizePipelineTests
{
    private static HttpResponseMessage RangeResponse(HttpStatusCode status, string? contentRange, long? contentLength = null)
    {
        var r = new HttpResponseMessage(status) { Content = new ByteArrayContent(Array.Empty<byte>()) };
        if (contentRange is not null)
            r.Content.Headers.TryAddWithoutValidation("Content-Range", contentRange);
        if (contentLength.HasValue)
            r.Content.Headers.ContentLength = contentLength.Value;
        return r;
    }

    [Trait("Category", Cats.Unit)][Fact]
    public void ParseContentRangeTotal_UsesTotalAfterSlash()
    {
        Assert.Equal(2848208, DownloadEngine.ParseContentRangeTotal("bytes 0-0/2848208"));
    }

    [Trait("Category", Cats.Unit)][Fact]
    public void ParseContentRangeTotal_416StarForm_AcceptsValidTotal()
    {
        Assert.Equal(1234, DownloadEngine.ParseContentRangeTotal("bytes */1234"));
    }

    [Trait("Category", Cats.Unit)][Theory]
    [InlineData("bytes 0-0/*")]
    [InlineData("bytes */*")]
    [InlineData("garbage")]
    [InlineData("")]
    [InlineData("bytes 0-0/-5")]
    [InlineData("bytes 0-0/0")]
    [InlineData("bytes 0-0/99999999999999999999")]
    [InlineData("bytes 0-99/50")]
    public void ParseContentRangeTotal_Invalid_StaysUnknown(string? raw)
    {
        Assert.Null(DownloadEngine.ParseContentRangeTotal(raw));
    }

    [Trait("Category", Cats.Unit)][Fact]
    public void GetContentRangeTotal_206_UsesRangeTotal_NotPartialLength()
    {
        using var r = RangeResponse(HttpStatusCode.PartialContent, "bytes 0-0/2848208", contentLength: 1);
        Assert.Equal(2848208, DownloadEngine.GetContentRangeTotal(r));
    }

    [Trait("Category", Cats.Unit)][Fact]
    public void GetContentRangeTotal_WildcardTotal_StaysUnknown()
    {
        using var r = RangeResponse(HttpStatusCode.PartialContent, "bytes 0-0/*", contentLength: 1);
        Assert.Null(DownloadEngine.GetContentRangeTotal(r));
    }

    [Trait("Category", Cats.Unit)][Fact]
    public void GetContentRangeTotal_MissingHeader_StaysUnknown()
    {
        using var r = RangeResponse(HttpStatusCode.PartialContent, null, contentLength: 1);
        Assert.Null(DownloadEngine.GetContentRangeTotal(r));
    }

    [Trait("Category", Cats.Unit)][Fact]
    public void ProbeSizeContentType_Html_NotUsable()
    {
        Assert.False(DownloadEngine.IsProbeSizeContentType("text/html"));
        Assert.False(DownloadEngine.IsProbeSizeContentType("application/xhtml+xml; charset=utf-8"));
        Assert.False(DownloadEngine.IsProbeSizeContentType("application/x-bittorrent"));
    }

    [Trait("Category", Cats.Unit)][Fact]
    public void ProbeSizeContentType_FileTypes_Usable()
    {
        Assert.True(DownloadEngine.IsProbeSizeContentType("video/mp4"));
        Assert.True(DownloadEngine.IsProbeSizeContentType("application/octet-stream"));
        Assert.True(DownloadEngine.IsProbeSizeContentType(null));
    }

    [Trait("Category", Cats.Unit)][Fact]
    public void SanitizeObservedSize_Validation()
    {
        Assert.Null(CaptureServer.SanitizeObservedSize(null));
        Assert.Null(CaptureServer.SanitizeObservedSize(0));
        Assert.Null(CaptureServer.SanitizeObservedSize(-5));
        Assert.Null(CaptureServer.SanitizeObservedSize(64L * 1024 * 1024 * 1024 + 1));
        Assert.Equal(1234, CaptureServer.SanitizeObservedSize(1234));
    }

    [Trait("Category", Cats.Unit)][Fact]
    public void CaptureItem_ObservedHint_SurvivesRoundTrip()
    {
        var item = new CaptureServer.BatchCaptureItem
        {
            Url = "https://cdn.example.com/f.mp4",
            ObservedSizeBytes = 2848208,
        };
        string json = JsonSerializer.Serialize(item);
        Assert.Contains("ObservedSizeBytes", json, StringComparison.OrdinalIgnoreCase);
        using var doc = JsonDocument.Parse(json);
        long roundTripped = -1;
        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            if (prop.NameEquals("observedSizeBytes") || prop.NameEquals("ObservedSizeBytes"))
                roundTripped = prop.Value.GetInt64();
        }
        Assert.Equal(2848208, roundTripped);
    }

    [Trait("Category", Cats.Unit)][Fact]
    public void CaptureItem_MissingHint_StaysUnknown()
    {
        var item = new CaptureServer.BatchCaptureItem { Url = "https://cdn.example.com/f.mp4" };
        Assert.Null(item.ObservedSizeBytes);
        string json = JsonSerializer.Serialize(item);
        // Optional: absent or null both mean unknown to older/newer readers.
        Assert.DoesNotContain("2848208", json, StringComparison.Ordinal);
    }

    [Trait("Category", Cats.Unit)][Fact]
    public void UnknownSize_DistinctFromEmpty()
    {
        // -1 (unknown) must never collapse to 0 (known-empty).
        Assert.Equal(-1, new WDM.Models.DownloadTask().TotalBytes);
    }
}
