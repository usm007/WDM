// PostReplayTests — form POST replay + proxy mirror through the loopback API:
//   extension payload shape -> CaptureServer validation -> structured item.
// Ports 17711-17714 (clear of 17530 app, 17531/2 capture, 17621, 17701-17703).
// Auth via chrome-extension Origin grace (no token touches user data).
using System.Net;
using System.Text;
using WDM.Services;
using WDM.Tests.TestInfrastructure;
using Xunit.Abstractions;

namespace WDM.Tests;

public sealed class PostReplayTests
{
    private const int BodyPort = 17711;
    private const int OversizePort = 17712;
    private const int BadTypePort = 17713;
    private const int ProxyPort = 17714;
    private readonly ITestOutputHelper _out;

    public PostReplayTests(ITestOutputHelper @out) { _out = @out; }

    private static StringContent Json(string s) => new(s, Encoding.UTF8, "application/json");

    private static async Task<HttpStatusCode> PostAsync(int port, string json)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        using var req = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{port}/download");
        // Pinned extension ID (see CaptureServer.WdmChromeExtensionOrigin).
        req.Headers.Add("Origin", "chrome-extension://jehagbjolooaohcbmlhegpmjeaakonof");
        req.Content = Json(json);
        using var resp = await http.SendAsync(req);
        return resp.StatusCode;
    }

    // Valid body + proxy descriptor land on the structured item; the legacy
    // tuple delegate stays silent (exclusive — no double dialogs).
    [Trait("Category", Cats.E2E)][Fact]
    public async Task PostBody_Accepted_Into_Structured_Item()
    {
        string body = Convert.ToBase64String(Encoding.UTF8.GetBytes("id=42&token=abc"));
        CaptureServer.BatchCaptureItem? got = null;
        bool legacyCalled = false;
        using var server = new CaptureServer((url, name, referer, headers, title) => { legacyCalled = true; }, BodyPort);
        server.OnCaptureItem = item => { got = item; };
        server.Start();
        Assert.True(server.IsRunning);
        try
        {
            var code = await PostAsync(BodyPort,
                "{\"url\":\"http://origin.test/form/download\",\"fileName\":\"report.pdf\"," +
                "\"postData\":\"" + body + "\",\"postContentType\":\"application/x-www-form-urlencoded\"," +
                "\"proxy\":{\"mode\":\"fixed\",\"host\":\"proxy.test\",\"port\":8080,\"type\":\"http\"}}");
            Assert.Equal(HttpStatusCode.OK, code);
            Assert.NotNull(got);
            Assert.Equal("http://origin.test/form/download", got.Url);
            Assert.Equal(body, got.PostData);
            Assert.Equal("application/x-www-form-urlencoded", got.PostContentType);
            Assert.Equal("proxy.test", got.ProxyHost);
            Assert.Equal(8080, got.ProxyPort);
            Assert.Equal("http", got.ProxyType);
            Assert.False(legacyCalled);
        }
        finally { server.Dispose(); }
    }

    // Oversized bodies fail loudly (a form download without its body would
    // corrupt downstream — never silently strip).
    [Trait("Category", Cats.Security)][Fact]
    public async Task PostBody_Oversized_Rejected()
    {
        bool called = false;
        using var server = new CaptureServer((url, name, referer, headers, title) => { called = true; }, OversizePort);
        server.OnCaptureItem = _ => { called = true; };
        server.Start();
        try
        {
            string big = new string('A', 360 * 1024);
            var code = await PostAsync(OversizePort,
                "{\"url\":\"http://origin.test/form/download\",\"postData\":\"" + big + "\"}");
            Assert.Equal(HttpStatusCode.BadRequest, code);
            Assert.False(called);
        }
        finally { server.Dispose(); }
    }

    // Non-form content types are rejected, not replayed.
    [Trait("Category", Cats.Security)][Fact]
    public async Task PostBody_BadContentType_Rejected()
    {
        bool called = false;
        using var server = new CaptureServer((url, name, referer, headers, title) => { called = true; }, BadTypePort);
        server.OnCaptureItem = _ => { called = true; };
        server.Start();
        try
        {
            string body = Convert.ToBase64String(Encoding.UTF8.GetBytes("<html></html>"));
            var code = await PostAsync(BadTypePort,
                "{\"url\":\"http://origin.test/form/download\",\"postData\":\"" + body + "\"," +
                "\"postContentType\":\"text/html\"}");
            Assert.Equal(HttpStatusCode.BadRequest, code);
            Assert.False(called);
        }
        finally { server.Dispose(); }
    }

    // Malformed proxy descriptors are dropped leniently — the item survives.
    [Trait("Category", Cats.E2E)][Fact]
    public async Task BadProxy_Dropped_Item_Accepted()
    {
        CaptureServer.BatchCaptureItem? got = null;
        using var server = new CaptureServer((url, name, referer, headers, title) => { }, ProxyPort);
        server.OnCaptureItem = item => { got = item; };
        server.Start();
        try
        {
            var code = await PostAsync(ProxyPort,
                "{\"url\":\"http://origin.test/file.zip\"," +
                "\"proxy\":{\"mode\":\"fixed\",\"host\":\"http://evil/x\",\"port\":99999,\"type\":\"http\"}}");
            Assert.Equal(HttpStatusCode.OK, code);
            Assert.NotNull(got);
            Assert.Null(got.ProxyHost);
            Assert.Equal(0, got.ProxyPort);
            Assert.Null(got.PostData);
        }
        finally { server.Dispose(); }
    }
}
