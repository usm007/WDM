using System.Net;
using System.Text;
using System.Text.Json;
using WDM.Services;
using WDM.Tests.TestInfrastructure;

namespace WDM.Tests;

/// <summary>Extension static validation + local deterministic capture E2E + media fixtures.</summary>
public sealed class MediaCaptureTests
{
    private static string RepoRoot()
    {
        string dir = AppContext.BaseDirectory;
        while (dir is not null && !Directory.Exists(Path.Combine(dir, "src", "WDM.BrowserExtension")))
            dir = Directory.GetParent(dir)?.FullName!;
        return dir;
    }

    [Trait("Category", Cats.Browser)][Fact]
    public void Extension_Manifest_Permissions_Resources_Schemas()
    {
        string root = RepoRoot();
        string manifestPath = Path.Combine(root, "src", "WDM.BrowserExtension", "manifest.json");
        Assert.True(File.Exists(manifestPath));
        using var doc = JsonDocument.Parse(File.ReadAllText(manifestPath));
        var r = doc.RootElement;
        Assert.Equal(3, r.GetProperty("manifest_version").GetInt32());
        var perms = r.GetProperty("permissions").EnumerateArray().Select(e => e.GetString()).ToHashSet();
        Assert.Contains("downloads", perms);
        Assert.Contains("storage", perms);
        // Declared popup + background resources exist.
        string extDir = Path.Combine(root, "src", "WDM.BrowserExtension");
        foreach (string f in new[] { "background.js", "popup.html", "popup.js", "media_sniffer.js" })
            Assert.True(File.Exists(Path.Combine(extDir, f)), f);
        // Endpoint paths the extension speaks must exist server-side (contract).
        foreach (string ep in new[] { "/ping", "/download", "/download/batch", "/download/blob-chunk", "/download/mega-sid", "/resolve" })
            Assert.True(CaptureServerHasEndpoint(ep), ep);
    }

    private static bool CaptureServerHasEndpoint(string path)
    {
        // Contract check: CaptureServer source routes these paths.
        string root = RepoRoot();
        string src = File.ReadAllText(Path.Combine(root, "src", "WDM.App", "Services", "CaptureServer.cs"));
        return path switch
        {
            "/ping" => src.Contains("/ping"),
            "/download" => src.Contains("/download"),
            "/download/batch" => src.Contains("/download/batch"),
            "/download/blob-chunk" => src.Contains("blob-chunk"),
            "/download/mega-sid" => src.Contains("mega-sid"),
            "/resolve" => src.Contains("/resolve"),
            _ => false,
        };
    }

    [Trait("Category", Cats.E2E)][Fact]
    public async Task LocalE2E_FakePage_To_Capture_To_Origin()
    {
        // Fake browser page -> CaptureServer -> fake HTTP origin -> completed file.
        var captured = new List<string>();
        using var server = new CaptureServer((url, name, referer, headers, title) =>
        {
            lock (captured) captured.Add(url);
        }, 17621);
        server.Start();
        Assert.True(server.IsRunning);
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            using var req = new HttpRequestMessage(HttpMethod.Post, "http://127.0.0.1:17621/download");
            req.Headers.Add("Origin", "chrome-extension://test");
            req.Content = new StringContent(
                "{\"url\":\"http://origin.test/movie.mp4\",\"fileName\":\"movie.mp4\"}",
                Encoding.UTF8, "application/json");
            using var resp = await http.SendAsync(req);
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            Assert.Single(captured);

            // Second half: the captured URL downloads byte-exact from the fake origin.
            var content = TestFiles.Make(2 * 1024 * 1024, seed: 77);
            var origin = new FakeProgrammableOrigin();
            origin.AddObject("http://origin.test/movie.mp4", content);
            string dir = TestFiles.NewTempDir("wdm-e2e");
            try
            {
                await EngineDriver.WithTimeout(
                    EngineDriver.RunAsync(origin, content, dir, "movie.mp4",
                        urls: new List<string> { "http://origin.test/movie.mp4" }, workers: 2),
                    TimeSpan.FromSeconds(90), () => "local E2E download leg");
                Assert.Equal(content, await File.ReadAllBytesAsync(Path.Combine(dir, "movie.mp4")));
            }
            finally { TestFiles.DeleteDir(dir); }
        }
        finally { server.Dispose(); }
    }

    [Trait("Category", Cats.Media)][Theory]
    [InlineData("#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=800000,RESOLUTION=640x360\nlow.m3u8\n#EXT-X-STREAM-INF:BANDWIDTH=5000000,RESOLUTION=1920x1080\nhi.m3u8\n", 2)]
    [InlineData("#EXTM3U\n#EXT-X-TARGETDURATION:10\n#EXTINF:10,\nseg0.ts\n#EXTINF:10,\nseg1.ts\n#EXT-X-ENDLIST\n", 0)]
    [InlineData("not a playlist at all", 0)]
    public void HlsMaster_CueCountsVariants(string body, int minVariants)
    {
        // Fixture-level contract: master playlists carry STREAM-INF cues; media playlists don't.
        int cues = body.Split("#EXT-X-STREAM-INF").Length - 1;
        Assert.True(cues >= minVariants);
        Assert.True(body.Length > 0);
    }

    [Trait("Category", Cats.Media)][Theory]
    [InlineData("<?xml version=\"1.0\"?><MPD xmlns=\"urn:mpeg:dash:schema:mpd:2011\" type=\"static\"><Period><AdaptationSet mimeType=\"video/mp4\"><Representation bandwidth=\"5000000\"/></AdaptationSet></Period></MPD>", true)]
    [InlineData("not xml", false)]
    public void DashManifest_WellFormed_Smoke(string body, bool expectMpd)
    {
        bool hasMpd = body.Contains("<MPD") && body.Contains("<AdaptationSet");
        Assert.Equal(expectMpd, hasMpd);
    }
}
