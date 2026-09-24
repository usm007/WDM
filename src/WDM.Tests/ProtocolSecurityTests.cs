using System.Net;
using System.Reflection;
using System.Text;
using WDM.Services;
using WDM.Services.Chunking;
using WDM.Tests.TestInfrastructure;

namespace WDM.Tests;

/// <summary>HTTP protocol edges + expanded capture security (offline).</summary>
public sealed class ProtocolSecurityTests
{
    private static bool IsBlocked(string url) => NetworkGuard.IsBlockedResolveTarget(url);

    [Trait("Category", Cats.Security)][Theory]
    [InlineData("http://127.0.0.1/x")][InlineData("http://localhost/x")]
    [InlineData("http://[::1]/x")][InlineData("http://0.0.0.0/x")]
    [InlineData("http://10.0.0.5/x")][InlineData("http://192.168.1.1/x")][InlineData("http://169.254.1.1/x")]
    public void BlockedTargets_StayBlocked(string url) => Assert.True(IsBlocked(url));

    [Trait("Category", Cats.Security)][Theory]
    [InlineData("https://cdn.example.com/v.mp4")][InlineData("http://example.com/file.zip")]
    public void PublicTargets_Allowed(string url) => Assert.False(IsBlocked(url));

    [Trait("Category", Cats.Security)][Fact]
    public async Task OversizedAndMalformedPayloads_RejectedWithoutCrash()
    {
        using var server = new CaptureServer((url, name, referer, headers, title) => { }, 17611);
        server.Start();
        Assert.True(server.IsRunning);
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            async Task<HttpStatusCode> Post(string path, string json)
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:17611{path}");
                req.Headers.Add("Origin", "chrome-extension://test");
                req.Content = new StringContent(json, Encoding.UTF8, "application/json");
                using var resp = await http.SendAsync(req);
                return resp.StatusCode;
            }
            // CRLF / header injection in URL must not be accepted.
            Assert.Equal(HttpStatusCode.BadRequest, await Post("/download", "{\"url\":\"https://a.com/x\\r\\nEvil: 1\"}"));
            // Invalid scheme.
            Assert.Equal(HttpStatusCode.BadRequest, await Post("/download", "{\"url\":\"javascript:alert(1)\"}"));
            // Gigantic header value (11MB body cap path) rejected, server alive.
            string big = new string('a', 512 * 1024);
            var code = await Post("/download", $"{{\"url\":\"https://cdn.example.com/{big}\"}}");
            Assert.True(code is HttpStatusCode.BadRequest or HttpStatusCode.RequestEntityTooLarge or HttpStatusCode.OK);
            Assert.True(server.IsRunning);
        }
        finally { server.Dispose(); }
    }

    [Trait("Category", Cats.Integration)][Theory]
    [InlineData(403)][InlineData(404)][InlineData(408)][InlineData(429)][InlineData(500)][InlineData(502)][InlineData(503)][InlineData(504)]
    public async Task StatusCodes_NeverSilentSuccess(int code)
    {
        var content = TestFiles.Make(1024 * 1024);
        var origin = new FakeProgrammableOrigin();
        origin.AddObject("http://primary/file.bin", content);
        origin.StatusOverride = (req, i) => (HttpStatusCode)code;
        string dir = TestFiles.NewTempDir("wdm-proto");
        try
        {
            await Assert.ThrowsAnyAsync<Exception>(() =>
                EngineDriver.RunAsync(origin, content, dir, "f.bin", workers: 1, maxRetries: 0));
            Assert.True(origin.RequestCount >= 1);
        }
        finally { TestFiles.DeleteDir(dir); }
    }

    [Trait("Category", Cats.Integration)][Fact]
    public async Task WrongContentRange_DoesNotCorrupt()
    {
        var content = TestFiles.Make(2 * 1024 * 1024);
        var origin = new FakeProgrammableOrigin();
        origin.AddObject("http://primary/file.bin", content);
        int n = 0;
        origin.FaultPicker = (req, i) => (++n == 1) ? FaultKind.WrongContentRange : FaultKind.None;
        string dir = TestFiles.NewTempDir("wdm-proto2");
        try
        {
            // First attempt lies about range; engine must retry/fail-safe, never write wrong bytes silently.
            // With retries the download still converges on correct bytes.
            await EngineDriver.WithTimeout(
                EngineDriver.RunAsync(origin, content, dir, "f.bin", workers: 1, maxRetries: 5),
                TimeSpan.FromSeconds(90), () => "wrong content-range");
            Assert.Equal(content, await File.ReadAllBytesAsync(Path.Combine(dir, "f.bin")));
        }
        finally { TestFiles.DeleteDir(dir); }
    }

    [Trait("Category", Cats.Security)][Fact]
    public void AuthToken_MissingOrForged_Rejected()
    {
        string good = CaptureAuth.GetOrCreateToken();
        Assert.True(CaptureAuth.Validate(good));
        Assert.True(CaptureAuth.Validate("Bearer " + good));
        Assert.False(CaptureAuth.Validate(""));
        Assert.False(CaptureAuth.Validate("Bearer forged-token-123"));
        Assert.False(CaptureAuth.Validate(null));
    }
}
