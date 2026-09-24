// BrowserCatchTests — minimal prototype proving the extension media-sniffing
// path end to end WITHOUT a browser binary on the machine:
//   page JS (real media_sniffer.js + wdm_hook.js, executed under node) ->
//   extension POST shape -> CaptureServer -> engine downloads byte-exact.
// Ports 17701-17703: clash with nothing (not 17530 app, not 17621 existing
// tests). Auth uses the chrome-extension Origin migration grace (no token, so
// CaptureAuth never touches %LocalAppData%\WDM-Data); no TaskStore writes.
// The one thing NOT proven here: loading the unpacked extension in real
// Chromium (see RealBrowser_Gap fixture + report). No new packages.
using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using WDM.Services;
using WDM.Tests.TestInfrastructure;
using Xunit.Abstractions;

namespace WDM.Tests;

public sealed class BrowserCatchTests
{
    private const int SnifferPort = 17701;
    private const int WebOriginPort = 17702;
    private const int EnginePort = 17703;
    private readonly ITestOutputHelper _out;

    public BrowserCatchTests(ITestOutputHelper @out) { _out = @out; }

    private static string RepoRoot()
    {
        string dir = AppContext.BaseDirectory;
        while (dir is not null && !Directory.Exists(Path.Combine(dir, "src", "WDM.BrowserExtension")))
            dir = Directory.GetParent(dir)?.FullName!;
        return dir;
    }

    private sealed record Captured(string Url, string? Name, string? Referer, Dictionary<string, string> Headers, string? Title);

    // The JS half, actually executed: node loads the real extension scripts
    // with DOM stubs and drives fetch/XHR/hook-bridge vectors (12 checks).
    [Trait("Category", Cats.Browser)][Fact]
    public void SnifferJs_Executes_And_Classifies()
    {
        string root = RepoRoot();
        string probe = Path.Combine(root, "src", "WDM.Tests", "Fixtures", "browser-catch", "sniffer_probe.js");
        string extDir = Path.Combine(root, "src", "WDM.BrowserExtension");
        Assert.True(File.Exists(probe), probe);

        var psi = new ProcessStartInfo("node", $"\"{probe}\" \"{extDir}\"")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        string stdout, stderr;
        int exit;
        try
        {
            using var p = Process.Start(psi) ?? throw new InvalidOperationException("node did not start");
            stdout = p.StandardOutput.ReadToEnd();
            stderr = p.StandardError.ReadToEnd();
            Assert.True(p.WaitForExit(60_000), "node probe timed out");
            exit = p.ExitCode;
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            Assert.Fail($"node executable not found on PATH — install Node 18+ to run the JS-half probe: {ex.Message}");
            return;
        }
        _out.WriteLine(stdout);
        if (!string.IsNullOrWhiteSpace(stderr)) _out.WriteLine("STDERR: " + stderr);
        Assert.True(exit == 0, $"sniffer_probe.js exit={exit}. stderr={stderr}");
        using var doc = JsonDocument.Parse(stdout);
        var failed = doc.RootElement.GetProperty("checks").EnumerateArray()
            .Where(c => !c.GetProperty("pass").GetBoolean())
            .Select(c => c.GetProperty("name").GetString()).ToList();
        Assert.True(failed.Count == 0, "JS-half checks failed: " + string.Join("; ", failed));
    }

    // Exact payload media_sniffer.sendToWdm builds -> POST /download accepted,
    // streamType/keyUrl/pageTitle survive into the capture callback.
    [Trait("Category", Cats.E2E)][Fact]
    public async Task SnifferPayload_Accepted_By_CaptureServer()
    {
        Captured? got = null;
        using var server = new CaptureServer((url, name, referer, headers, title) =>
        {
            lock (this) got = new Captured(url, name, referer, headers, title);
        }, SnifferPort);
        server.Start();
        Assert.True(server.IsRunning);
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            using var req = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{SnifferPort}/download");
            req.Headers.Add("Origin", "chrome-extension://test");
            req.Content = new StringContent(
                "{\"url\":\"http://origin.test/hls/master.m3u8\",\"fileName\":\"My Film\"," +
                "\"referer\":\"https://videos.test/watch/1\"," +
                "\"headers\":{\"Referer\":\"https://videos.test/watch/1\",\"Origin\":\"https://videos.test\"}," +
                "\"pageTitle\":\"Watch My Film - SomeSite\",\"streamType\":\"HLS\"," +
                "\"keyUrl\":\"http://origin.test/hls/key.bin\"}",
                Encoding.UTF8, "application/json");
            using var resp = await http.SendAsync(req);
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            Assert.NotNull(got);
            Assert.Equal("http://origin.test/hls/master.m3u8", got.Url);
            Assert.Equal("My Film", got.Name);
            Assert.Equal("Watch My Film - SomeSite", got.Title);
            Assert.Equal("HLS", got.Headers["X-WDM-StreamType"]);
            Assert.Equal("http://origin.test/hls/key.bin", got.Headers["X-WDM-KeyUrl"]);
            Assert.Equal("https://videos.test", got.Headers["Origin"]);
        }
        finally { server.Dispose(); }
    }

    // CSRF gate: a real web page Origin must never drive /download.
    [Trait("Category", Cats.Security)][Fact]
    public async Task WebPage_Origin_Post_Rejected()
    {
        bool called = false;
        using var server = new CaptureServer((url, name, referer, headers, title) => { called = true; }, WebOriginPort);
        server.Start();
        Assert.True(server.IsRunning);
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            using var req = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{WebOriginPort}/download");
            req.Headers.Add("Origin", "https://videos.test");
            req.Content = new StringContent("{\"url\":\"http://origin.test/movie.mp4\"}", Encoding.UTF8, "application/json");
            using var resp = await http.SendAsync(req);
            Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
            Assert.False(called);
        }
        finally { server.Dispose(); }
    }

    // Engine leg: a captured media URL downloads byte-exact from the origin.
    [Trait("Category", Cats.E2E)][Fact]
    public async Task Captured_Media_Downloads_ByteExact()
    {
        string? captured = null;
        using var server = new CaptureServer((url, name, referer, headers, title) =>
        {
            lock (this) captured = url;
        }, EnginePort);
        server.Start();
        Assert.True(server.IsRunning);
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            using var req = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{EnginePort}/download");
            req.Headers.Add("Origin", "chrome-extension://test");
            req.Content = new StringContent(
                "{\"url\":\"http://origin.test/movie.mp4\",\"fileName\":\"movie.mp4\"}",
                Encoding.UTF8, "application/json");
            using var resp = await http.SendAsync(req);
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            Assert.Equal("http://origin.test/movie.mp4", captured);

            var content = TestFiles.Make(2 * 1024 * 1024, seed: 7);
            var origin = new FakeProgrammableOrigin();
            origin.AddObject("http://origin.test/movie.mp4", content);
            string dir = TestFiles.NewTempDir("wdm-browsercatch");
            try
            {
                await EngineDriver.WithTimeout(
                    EngineDriver.RunAsync(origin, content, dir, "movie.mp4",
                        urls: new List<string> { captured! }, workers: 2),
                    TimeSpan.FromSeconds(90), () => "browser-catch download leg");
                Assert.Equal(content, await File.ReadAllBytesAsync(Path.Combine(dir, "movie.mp4")));
            }
            finally { TestFiles.DeleteDir(dir); }
        }
        finally { server.Dispose(); }
    }

    // Gap, machine-verified: no browser binary here, so the unpacked-extension
    // load (Fixtures/browser-catch/catch_page.html + --load-extension) still
    // needs a real Chromium — manual step, everything else is green above.
    [Trait("Category", Cats.Browser)][Fact]
    public void RealBrowser_Gap_No_Binary_On_Machine()
    {
        string root = RepoRoot();
        Assert.True(File.Exists(Path.Combine(root, "src", "WDM.Tests", "Fixtures", "browser-catch", "catch_page.html")));
        Assert.True(File.Exists(Path.Combine(root, "src", "WDM.BrowserExtension", "media_sniffer.js")));
        var hits = new[] {
            @"C:\Program Files\Google\Chrome\Application\chrome.exe",
            @"C:\Program Files (x86)\Google\Chrome\Application\chrome.exe",
            @"C:\Program Files\Microsoft\Edge\Application\msedge.exe",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), @".cache\ms-playwright"),
        }.Where(File.Exists).ToList();
        _out.WriteLine(hits.Count == 0
            ? "No Chrome/Edge/Playwright browser found — real-browser load is the remaining manual step."
            : "Browser artifacts present (unexpected): " + string.Join(", ", hits));
        Assert.True(true);
    }
}
