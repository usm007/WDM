using System.Reflection;
using WDM.Services;

namespace WDM.Tests;

public sealed class CaptureSecurityTests
{
    private static bool IsBlocked(string url) => NetworkGuard.IsBlockedResolveTarget(url);

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

    // Spoof matrix (BUG-016 repro as regression tests): only the pinned
    // extension ID or a versioned token-era client passes without a token.
    private static async Task<System.Net.HttpStatusCode> PostDownloadAsync(
        int port, string? origin, string? token, string? extVersion, string body = "{\"url\":\"https://example.com/f.mp4\"}")
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        using var req = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{port}/download");
        if (origin is not null) req.Headers.Add("Origin", origin);
        if (token is not null) req.Headers.Add("X-WDM-Token", token);
        if (extVersion is not null) req.Headers.Add("X-WDM-ExtVersion", extVersion);
        req.Content = new System.Net.Http.StringContent(body, System.Text.Encoding.UTF8, "application/json");
        using var resp = await http.SendAsync(req);
        return resp.StatusCode;
    }

    private static CaptureServer StableServer(int port, Action onCalled)
    {
        var server = new CaptureServer((url, name, referer, headers, title) => onCalled(), port);
        server.Start();
        return server;
    }

    [Fact]
    public async Task SpoofedExtensionOrigin_WithoutToken_Rejected()
    {
        bool called = false;
        using var server = StableServer(17721, () => { called = true; });
        try
        {
            Assert.Equal(System.Net.HttpStatusCode.Unauthorized,
                await PostDownloadAsync(17721, "chrome-extension://evil-id", null, null));
            Assert.False(called);
        }
        finally { server.Dispose(); }
    }

    [Fact]
    public async Task CurlLike_NoOriginNoToken_Rejected()
    {
        bool called = false;
        using var server = StableServer(17722, () => { called = true; });
        try
        {
            Assert.Equal(System.Net.HttpStatusCode.Unauthorized, await PostDownloadAsync(17722, null, null, null));
            Assert.False(called);
        }
        finally { server.Dispose(); }
    }

    [Fact]
    public async Task WrongToken_NeverPasses()
    {
        bool called = false;
        using var server = StableServer(17723, () => { called = true; });
        try
        {
            Assert.Equal(System.Net.HttpStatusCode.Unauthorized,
                await PostDownloadAsync(17723, "chrome-extension://jehagbjolooaohcbmlhegpmjeaakonof", "wrong-token", "1.2.8"));
            Assert.False(called);
        }
        finally { server.Dispose(); }
    }

    [Fact]
    public async Task PinnedId_WithoutToken_Passes()
    {
        bool called = false;
        using var server = StableServer(17724, () => { called = true; });
        try
        {
            Assert.Equal(System.Net.HttpStatusCode.OK,
                await PostDownloadAsync(17724, "chrome-extension://jehagbjolooaohcbmlhegpmjeaakonof", null, "1.2.8"));
            Assert.True(called);
        }
        finally { server.Dispose(); }
    }

    [Theory]
    [InlineData("1.2.8", System.Net.HttpStatusCode.OK)]
    [InlineData("1.2.7", System.Net.HttpStatusCode.Unauthorized)]
    [InlineData(null, System.Net.HttpStatusCode.Unauthorized)]
    public async Task FirefoxGrace_RequiresTokenEraVersion(string? version, System.Net.HttpStatusCode expected)
    {
        bool called = false;
        using var server = StableServer(17725, () => { called = true; });
        try
        {
            Assert.Equal(expected, await PostDownloadAsync(17725, "moz-extension://some-uuid-here", null, version));
            Assert.Equal(expected == System.Net.HttpStatusCode.OK, called);
        }
        finally { server.Dispose(); }
    }

    [Fact]
    public void ExtVersion_Compares_Numerically()
    {
        var m = typeof(CaptureServer).GetMethod("ExtVersionAtLeast",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        bool AtLeast(string? v) => (bool)m.Invoke(null, new object?[] { v, "1.2.8" })!;
        Assert.True(AtLeast("1.2.8"));
        Assert.True(AtLeast("1.2.10"));
        Assert.True(AtLeast("1.3.0"));
        Assert.False(AtLeast("1.2.7"));
        Assert.False(AtLeast("1.0.0"));
        Assert.False(AtLeast(null));
        Assert.False(AtLeast(""));
    }
}
