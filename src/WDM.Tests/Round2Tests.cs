using System.IO;
using System.Net;
using WDM.Services;

namespace WDM.Tests;

public sealed class Round2Tests
{
    // ── BUG-019: Auth header sticky-cap (first non-empty wins) ────────────
    // Covered by AuthAndIntegrityTests; CanNotOverwriteExistingAuth tested there.

    // ── BUG-020: Token-auth carve-out for LAN/private URLs ────────────────
    // Covered by CaptureSecurityTests; tested via IsBlockedResolveTarget parity.

    // ── BUG-021: IPv6 ULA + numeric-IP SSRF bypass ───────────────────────
    [Theory]
    [InlineData("http://[::1]/video.mp4")]        // IPv6 loopback
    [InlineData("http://[::ffff:127.0.0.1]/x")]    // IPv4-mapped IPv6 loopback
    public void IPv6Loopback_IsBlocked(string url)
    {
        Assert.True(CaptureServer.IsBlockedResolveTarget(url), url);
    }

    [Theory]
    [InlineData("http://[2001:db8::1]/video.mp4")] // public IPv6 doc-range
    [InlineData("https://example.com/video.mp4")]
    public void IPv6NonPrivate_IsAllowed(string url)
    {
        Assert.False(CaptureServer.IsBlockedResolveTarget(url), url);
    }

    // ── BUG-025: IsFatalDiskError ────────────────────────────────────────
    [Fact]
    public void IsFatalDiskError_UnauthorizedAccess_ReturnsTrue()
    {
        Assert.True(DownloadEngine.IsFatalDiskError(
            new UnauthorizedAccessException("denied")));
    }

    [Fact]
    public void IsFatalDiskError_PathTooLong_ReturnsTrue()
    {
        Assert.True(DownloadEngine.IsFatalDiskError(
            new PathTooLongException("path too long")));
    }

    [Fact]
    public void IsFatalDiskError_DiskFullIOException_ReturnsTrue()
    {
        // HRESULT 0x80070070 → low word 0x70 = ERROR_DISK_FULL
        var ex = new IOException("disk full") { HResult = unchecked((int)0x80070070) };
        Assert.True(DownloadEngine.IsFatalDiskError(ex));
    }

    [Fact]
    public void IsFatalDiskError_TransientIOError_ReturnsFalse()
    {
        // HRESULT 0x80070021 (locked by another process) — retryable
        var ex = new IOException("locked") { HResult = unchecked((int)0x80070021) };
        Assert.False(DownloadEngine.IsFatalDiskError(ex));
    }

    [Fact]
    public void IsFatalDiskError_GenericException_ReturnsFalse()
    {
        Assert.False(DownloadEngine.IsFatalDiskError(
            new InvalidOperationException("random")));
    }

    // ── BUG-027: IsYtDlpResultCandidate ──────────────────────────────────
    [Theory]
    [InlineData("video.mp4", true)]
    [InlineData("track.mkv", true)]
    [InlineData("sub.vtt", true)]
    [InlineData("thumb.jpg", true)]
    [InlineData("song.flac", true)]
    [InlineData("video.info.json", true)]
    [InlineData("description", true)]
    public void YtDlpResultCandidate_MediaExtensions_ReturnsTrue(string name, bool expected)
    {
        Assert.Equal(expected, DownloadEngine.IsYtDlpResultCandidate(name));
    }

    [Theory]
    [InlineData("video.wdmstate")]
    [InlineData("chunk.part")]
    [InlineData("dl.tmp")]
    [InlineData("dl.ytdl")]
    [InlineData(".wdmseg_abc123")]
    [InlineData("video.exe")]
    [InlineData("readme.txt")]
    [InlineData("subtitles.info.txt")]
    [InlineData("")]
    public void YtDlpResultCandidate_NonMediaExtensions_ReturnsFalse(string name)
    {
        Assert.False(DownloadEngine.IsYtDlpResultCandidate(name));
    }

    // ── BUG-029: SplitAttributes (quote-aware comma split) ────────────────
    [Fact]
    public void SplitAttributes_SimpleAttributes()
    {
        var parts = HlsDownloader.SplitAttributes(
            "BANDWIDTH=1000000,CODECS=\"avc1.640028,mp4a.40.2\",RESOLUTION=1920x1080");
        Assert.Equal(3, parts.Count());
        Assert.Equal("BANDWIDTH=1000000", parts.ElementAt(0));
        Assert.Equal("CODECS=\"avc1.640028,mp4a.40.2\"", parts.ElementAt(1));
        Assert.Equal("RESOLUTION=1920x1080", parts.ElementAt(2));
    }

    [Fact]
    public void SplitAttributes_EmptyOrNull()
    {
        Assert.Empty(HlsDownloader.SplitAttributes(""));
        Assert.Empty(HlsDownloader.SplitAttributes(null!));
    }

    [Fact]
    public void SplitAttributes_SingleAttribute()
    {
        var parts = HlsDownloader.SplitAttributes("BANDWIDTH=500000").ToList();
        Assert.Single(parts);
        Assert.Equal("BANDWIDTH=500000", parts[0]);
    }

    [Fact]
    public void SplitAttributes_MultipleQuotedCommas()
    {
        var parts = HlsDownloader.SplitAttributes(
            "CODECS=\"avc1.640028,mp4a.40.2\",AUDIO=\"aac-track\",SUBTITLES=\"subs\"").ToList();
        Assert.Equal(3, parts.Count);
        Assert.Equal("CODECS=\"avc1.640028,mp4a.40.2\"", parts[0]);
        Assert.Equal("AUDIO=\"aac-track\"", parts[1]);
        Assert.Equal("SUBTITLES=\"subs\"", parts[2]);
    }

    // ── BUG-030: Cookie scoping — AttachCookies only sends same-host ────
    // Tested via integration (EmbedResolver internal methods via reflection).

    // ── BUG-032: Resume identity — ETag + Last-Modified independent check ─
    // Tested via integration in DownloadEngine; code changes verified by build.
}
