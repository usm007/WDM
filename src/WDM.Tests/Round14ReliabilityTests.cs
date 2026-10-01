using System.Net;
using System.Text;
using WDM.Models;
using WDM.Services;
using WDM.Tests.TestInfrastructure;
using TaskStatus = WDM.Models.TaskStatus;

namespace WDM.Tests;

/// <summary>Round 14 — reliability audit regressions (WDM-001…WDM-003).
/// Each test fails on the pre-fix code and passes after.
/// Collection with Round10: both redirect the static TaskStore.AppDir.</summary>
[Collection("TaskStoreState")]
public sealed class Round14ReliabilityTests : IDisposable
{
    private readonly string _appDir = Path.Combine(Path.GetTempPath(), "wdm_r14app_" + Guid.NewGuid().ToString("N"));
    private readonly string _realAppDir;

    public Round14ReliabilityTests()
    {
        _realAppDir = TaskStore.AppDir;
        Directory.CreateDirectory(_appDir);
        TaskStore.AppDir = _appDir;
    }

    public void Dispose()
    {
        TaskStore.AppDir = _realAppDir;
        try { Directory.Delete(_appDir, recursive: true); } catch { }
    }

    // WDM-001: empty tasks.json for an existing user is corruption, not "zero
    // tasks". Load must flag failure (blocking saves) instead of starting
    // empty with saves enabled (which cemented data loss).
    [Trait("Category", Cats.Unit)]
    [Fact]
    public void EmptyTasksJson_ExistingUser_BlocksSaves()
    {
        File.WriteAllText(Path.Combine(_appDir, "settings.json"), "{}");
        string tasksPath = Path.Combine(_appDir, "tasks.json");
        File.WriteAllText(tasksPath, "");
        var loaded = TaskStore.LoadTasks();
        Assert.Empty(loaded);
        Assert.True(TaskStore.TasksLoadFailed);

        var task = new DownloadTask
        {
            Url = "https://cdn.example.com/new.mp4",
            FileName = "new.mp4",
            SaveFolder = Path.GetTempPath(),
        };
        TaskStore.SaveTasks(new[] { task });
        Assert.Equal("", File.ReadAllText(tasksPath));
    }

    // WDM-002: Content-Length counts bytes; a multibyte UTF-8 JSON body has
    // fewer chars than bytes. The old char-count read loop blocked waiting
    // for chars that never arrive. Must accept quickly.
    [Trait("Category", Cats.Unit)]
    [Fact]
    public async Task CaptureServer_MultibyteBody_Accepted()
    {
        // Dynamic port: Round9Tests owns the fixed :17532 convention and the
        // two classes run in parallel — a fixed port makes one of them fail.
        int port;
        using (var tcp = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0))
        {
            tcp.Start();
            port = ((IPEndPoint)tcp.LocalEndpoint).Port;
        }
        var captured = new List<string>();
        using var server = new CaptureServer((url, name, referer, headers, title) =>
        {
            lock (captured) captured.Add(url + "|" + title);
        }, port);
        server.Start();
        Assert.True(server.IsRunning);

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        using var req = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{port}/download");
        req.Headers.Add("Origin", "chrome-extension://jehagbjolooaohcbmlhegpmjeaakonof");
        string json = "{\"url\":\"https://cdn.example.com/v.mp4\",\"fileName\":\"\u65e5\u672c\u8a9e_\u6f22\u5b57.mp4\",\"pageTitle\":\"\u65e5\u672c\u8a9e\u30bf\u30a4\u30c8\u30eb \U0001F600 \u00e9\u00e8\"}";
        req.Content = new StringContent(json, Encoding.UTF8, "application/json");
        using var resp = await http.SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Single(captured);
    }

    private sealed class FakeHlsOrigin : HttpMessageHandler
    {
        public readonly Dictionary<string, Func<HttpRequestMessage, HttpResponseMessage>> Routes = new(StringComparer.OrdinalIgnoreCase);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            string url = request.RequestUri!.AbsoluteUri;
            foreach (var kv in Routes)
                if (url.StartsWith(kv.Key, StringComparison.OrdinalIgnoreCase))
                    return Task.FromResult(kv.Value(request));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound) { RequestMessage = request });
        }
    }

    private static HttpResponseMessage Bytes(byte[] b, string ct = "video/mp2t")
    {
        var r = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(b) };
        r.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(ct);
        r.Content.Headers.ContentLength = b.Length;
        return r;
    }

    private static HttpResponseMessage Text(string s)
    {
        var r = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(s, Encoding.UTF8, "application/vnd.apple.mpegurl") };
        r.Content.Headers.ContentLength = Encoding.UTF8.GetByteCount(s);
        return r;
    }

    // WDM-003: a failed HLS download must leave NO file at the final path
    // (old code concatenated direct-to-final, leaving truncated files).
    [Trait("Category", Cats.Unit)]
    [Fact]
    public async Task Hls_FailedDownload_LeavesNoFinalFile()
    {
        var origin = new FakeHlsOrigin();
        string baseUrl = "http://hls14.example.com";
        origin.Routes[baseUrl + "/pl.m3u8"] = _ => Text(
            "#EXTM3U\n#EXT-X-VERSION:3\n#EXT-X-TARGETDURATION:6\n" +
            $"#EXTINF:6.0,\n{baseUrl}/seg0.ts\n#EXTINF:6.0,\n{baseUrl}/seg1.ts\n#EXT-X-ENDLIST\n");
        origin.Routes[baseUrl + "/seg0.ts"] = _ => Bytes(new byte[] { 1, 2, 3, 4 });
        origin.Routes[baseUrl + "/seg1.ts"] = req => new HttpResponseMessage(HttpStatusCode.InternalServerError) { RequestMessage = req };

        string dir = TestFiles.NewTempDir("wdm-r14hls");
        try
        {
            using var http = new HttpClient(origin) { Timeout = TimeSpan.FromSeconds(60) };
            string dest = Path.Combine(dir, "out.ts");
            await Assert.ThrowsAnyAsync<Exception>(() =>
                HlsDownloader.DownloadAsync(http, baseUrl + "/pl.m3u8", null, dest,
                    CancellationToken.None, _ => { }, _ => { }, (_, _) => Task.CompletedTask));
            Assert.False(File.Exists(dest));
            Assert.False(File.Exists(dest + ".wdmpart"));
        }
        finally { TestFiles.DeleteDir(dir); }
    }

    // WDM-003 guard: success path renames staging to final, no leftovers.    [Trait("Category", Cats.Unit)]
    [Fact]
    public async Task Hls_Success_NoStagingLeftBehind()
    {
        var origin = new FakeHlsOrigin();
        string baseUrl = "http://hls14ok.example.com";
        origin.Routes[baseUrl + "/pl.m3u8"] = _ => Text(
            "#EXTM3U\n#EXT-X-VERSION:3\n#EXT-X-TARGETDURATION:6\n" +
            $"#EXTINF:6.0,\n{baseUrl}/seg0.ts\n#EXTINF:6.0,\n{baseUrl}/seg1.ts\n#EXT-X-ENDLIST\n");
        origin.Routes[baseUrl + "/seg0.ts"] = _ => Bytes(new byte[] { 1, 2, 3, 4 });
        origin.Routes[baseUrl + "/seg1.ts"] = _ => Bytes(new byte[] { 5, 6, 7, 8 });

        string dir = TestFiles.NewTempDir("wdm-r14hlsok");
        try
        {
            using var http = new HttpClient(origin) { Timeout = TimeSpan.FromSeconds(60) };
            string dest = Path.Combine(dir, "out.ts");
            await HlsDownloader.DownloadAsync(http, baseUrl + "/pl.m3u8", null, dest,
                CancellationToken.None, _ => { }, _ => { }, (_, _) => Task.CompletedTask);
            Assert.True(File.Exists(dest));
            Assert.Equal(8, new FileInfo(dest).Length);
            Assert.False(File.Exists(dest + ".wdmpart"));
        }
        finally { TestFiles.DeleteDir(dir); }
    }

    // Media fetching kill-switch: default on (existing suites/tests unaffected).
    [Trait("Category", Cats.Unit)]
    [Fact]
    public void MediaFetching_DefaultIsOn()
    {
        Assert.True(new AppSettings().EnableMediaFetching);
    }

    // Kill-switch engaged: a probed HLS stream fails fast with an actionable
    // message instead of downloading segments. No segments are fetched.
    [Trait("Category", Cats.Integration)]
    [Fact]
    public async Task Engine_HlsStream_FailsFastWhenMediaFetchingOff()
    {
        var settings = TaskStore.LoadSettings();
        bool wasEnabled = settings.EnableMediaFetching;
        settings.EnableMediaFetching = false;
        TaskStore.SaveSettings(settings);

        using var server = new HlsLoopback();
        var engine = new DownloadEngine();
        string dir = TestFiles.NewTempDir("wdm-r14nomedia");
        try
        {
            var task = new DownloadTask
            {
                Url = server.BaseUrl + "live/stream.m3u8",
                FileName = "download_2026-01-01_000000.bin",
                SaveFolder = dir,
            };
            engine.Start(task);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.Elapsed < TimeSpan.FromSeconds(30))
            {
                if (task.Status is TaskStatus.Completed or TaskStatus.Failed or TaskStatus.Paused)
                    break;
                await Task.Delay(50);
            }
            Assert.Equal(TaskStatus.Failed, task.Status);
            string detail = (task.Error ?? "") + " " + (task.ErrorDetail ?? "");
            Assert.Contains("media fetching", detail, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(0, server.SegmentHits);
        }
        finally
        {
            TestFiles.DeleteDir(dir);
            var restore = TaskStore.LoadSettings();
            restore.EnableMediaFetching = wasEnabled;
            TaskStore.SaveSettings(restore);
        }
    }

    private sealed class HlsLoopback : IDisposable
    {
        private readonly HttpListener _listener = new();
        public string BaseUrl { get; }
        public int SegmentHits;
        private readonly byte[] _playlist = Encoding.UTF8.GetBytes(
            "#EXTM3U\n#EXT-X-VERSION:3\n#EXT-X-TARGETDURATION:6\n#EXTINF:6.0,\nseg0.ts\n#EXT-X-ENDLIST\n");

        public HlsLoopback()
        {
            int port;
            using (var tcp = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0))
            {
                tcp.Start();
                port = ((System.Net.IPEndPoint)tcp.LocalEndpoint).Port;
            }
            BaseUrl = $"http://127.0.0.1:{port}/";
            _listener.Prefixes.Add(BaseUrl);
            _listener.Start();
            _ = Task.Run(AcceptLoop);
        }

        private async Task AcceptLoop()
        {
            while (_listener.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = await _listener.GetContextAsync(); }
                catch { return; }
                _ = Task.Run(() => Serve(ctx));
            }
        }

        private void Serve(HttpListenerContext ctx)
        {
            try
            {
                var req = ctx.Request;
                var resp = ctx.Response;
                if (req.Url!.AbsolutePath.EndsWith(".ts", StringComparison.OrdinalIgnoreCase))
                {
                    Interlocked.Increment(ref SegmentHits);
                    resp.StatusCode = 200;
                    resp.ContentType = "video/mp2t";
                    byte[] seg = new byte[] { 1, 2, 3, 4 };
                    resp.ContentLength64 = seg.Length;
                    resp.OutputStream.Write(seg, 0, seg.Length);
                    resp.OutputStream.Close();
                    return;
                }
                if (req.HttpMethod == "HEAD")
                {
                    resp.StatusCode = 200;
                    resp.ContentType = "application/vnd.apple.mpegurl";
                    resp.AddHeader("Accept-Ranges", "bytes");
                    resp.OutputStream.Close();
                    return;
                }
                resp.StatusCode = 200;
                resp.ContentType = "application/vnd.apple.mpegurl";
                resp.AddHeader("Accept-Ranges", "bytes");
                resp.ContentLength64 = _playlist.Length;
                resp.OutputStream.Write(_playlist, 0, _playlist.Length);
                resp.OutputStream.Close();
            }
            catch
            {
                try { ctx.Response.Abort(); } catch { }
            }
        }

        public void Dispose()
        {
            try { _listener.Stop(); } catch { }
            try { _listener.Close(); } catch { }
        }
    }
}
