using System.Reflection;
using WDM.Services;

namespace WDM.Tests;

public sealed class CaptureSecurityTests
{
    private static bool IsBlocked(string url)
    {
        var m = typeof(CaptureServer).GetMethod("IsBlockedResolveTarget",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        return (bool)m.Invoke(null, new object[] { url })!;
    }

    [Theory]
    [InlineData("http://127.0.0.1/video.mp4")]
    [InlineData("http://localhost:8080/x")]
    [InlineData("http://10.0.0.5/f")]
    [InlineData("http://192.168.1.1/f")]
    [InlineData("http://172.16.5.4/f")]
    [InlineData("http://169.254.169.254/latest/meta-data/")]
    [InlineData("http://metadata.google.internal/")]
    public void PrivateTargets_AreBlocked(string url)
    {
        Assert.True(IsBlocked(url), url);
    }

    [Theory]
    [InlineData("https://example.com/video.mp4")]
    [InlineData("https://cdn.example.org/a/b.m3u8")]
    public void PublicTargets_AreAllowed(string url)
    {
        Assert.False(IsBlocked(url), url);
    }

    [Fact]
    public void SanitizeCaptureUrl_RejectsBlockedTargets()
    {
        var m = typeof(CaptureServer).GetMethod("SanitizeCaptureUrl",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        var blocked = (string?)m.Invoke(null, new object?[] { "http://127.0.0.1/x", 2048, false });
        var ok = (string?)m.Invoke(null, new object?[] { "https://example.com/x", 2048, false });
        Assert.Null(blocked);
        Assert.Equal("https://example.com/x", ok);
    }
}
