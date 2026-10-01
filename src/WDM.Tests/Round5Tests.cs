using System.Net;
using System.Net.Sockets;
using System.Text;
using WDM.Models;
using WDM.Services;
using WDM.ViewModels;
using TaskStatus = WDM.Models.TaskStatus;

namespace WDM.Tests;

/// <summary>Round 5 — 1DM-borrow verification (P1+P2): dup fingerprint (A4),
/// probe triage (B1), pause-all semantics (A3), auto-resume (A2), notifications (C1).
/// Pure-logic xUnit plus engine integration tests against a loopback HttpListener
/// (the same code path a real download takes, end-user point of view).</summary>
public sealed class Round5Tests
{
    // ---------- A4: URL normalization ----------

    [Theory]
    [InlineData("https://cdn.site.com/file.mp4", "https://cdn.site.com/file.mp4")]
    [InlineData("  https://cdn.site.com/file.mp4  ", "https://cdn.site.com/file.mp4")]
    [InlineData("HTTPS://CDN.SITE.COM/file.mp4", "https://cdn.site.com/file.mp4")]
    [InlineData("https://cdn.site.com/file.mp4#frag", "https://cdn.site.com/file.mp4")]
    [InlineData("https://cdn.site.com/file.mp4?tok=abc", "https://cdn.site.com/file.mp4?tok=abc")]
    public void NormalizeUrlKey_CanonicalizesIdentity(string input, string expected)
    {
        Assert.Equal(expected, MainViewModel.NormalizeUrlKey(input));
    }

    [Fact]
    public void NormalizeUrlKey_PreservesCaseSensitivePath()
    {
        // Paths can be case-sensitive: must NOT fold them.
        Assert.NotEqual(
            MainViewModel.NormalizeUrlKey("https://cdn.site.com/File.mp4"),
            MainViewModel.NormalizeUrlKey("https://cdn.site.com/file.mp4"));
    }

    [Fact]
    public void NormalizeUrlKey_HandlesEmptyAndGarbage()
    {
        Assert.Equal("", MainViewModel.NormalizeUrlKey(null));
        Assert.Equal("", MainViewModel.NormalizeUrlKey("   "));
        Assert.Equal("not a url", MainViewModel.NormalizeUrlKey("not a url"));
    }

    // ---------- A4/A2 via ViewModel ----------

    private static MainViewModel NewVmWith(params DownloadTask[] tasks)
    {
        var vm = new MainViewModel();
        // The VM loads the user's real tasks.json: isolate tests from it, and
        // suppress persistence so deferred saves can never touch user data.
        vm.SuppressPersistence();
        vm.Tasks.Clear();
        foreach (var t in tasks)
            vm.Tasks.Add(t);
        return vm;
    }

    private static DownloadTask TaskWith(string url, long totalBytes, TaskStatus status) =>
        new() { Url = url, FileName = "f.bin", SaveFolder = Path.GetTempPath(), TotalBytes = totalBytes, Status = status };

    [Fact]
    public void FindByUrl_MatchesAcrossFragmentAndHostCase()
    {
        using var vm = NewVmWith(TaskWith("https://cdn.site.com/a.mp4", 10, TaskStatus.Paused));
        Assert.NotNull(vm.FindByUrl("https://CDN.site.com/a.mp4#t=5"));
        Assert.Null(vm.FindByUrl("https://cdn.site.com/b.mp4"));
    }

    [Fact]
    public void ExistingUrl_PreservesLegacyBehavior()
    {
        using var vm = NewVmWith(TaskWith("https://cdn.site.com/a.mp4", 10, TaskStatus.Paused));
        Assert.True(vm.ExistingUrl("https://cdn.site.com/a.mp4"));
        Assert.False(vm.ExistingUrl("https://cdn.site.com/other.mp4"));
    }

    [Fact]
    public void FindRefreshedLink_DistinguishesRefreshFromDuplicate()
    {
        using var vm = NewVmWith(TaskWith("https://cdn.site.com/a.mp4", 1000, TaskStatus.Completed));
        // Same size -> true duplicate (null = not a refresh).
        Assert.Null(vm.FindRefreshedLink("https://cdn.site.com/a.mp4", 1000));
        // Different size -> refreshed link, should be allowed.
        Assert.NotNull(vm.FindRefreshedLink("https://cdn.site.com/a.mp4", 2000));
        // Unknown sizes -> fall back to legacy duplicate treatment.
        Assert.Null(vm.FindRefreshedLink("https://cdn.site.com/a.mp4", -1));
        Assert.Null(vm.FindRefreshedLink("https://cdn.site.com/missing.mp4", 2000));
    }

    [Fact]
    public void CollectAutoResumeCandidates_RespectsToggleBudgetAndStatus()
    {
        using var vm = NewVmWith(
            TaskWith("https://s.com/1", 10, TaskStatus.Failed),
            TaskWith("https://s.com/2", 10, TaskStatus.Failed),
            TaskWith("https://s.com/3", 10, TaskStatus.Paused),
            TaskWith("https://s.com/4", 10, TaskStatus.Failed));

        vm.Settings.AutoResumeFailed = false;
        Assert.Empty(vm.CollectAutoResumeCandidates());

        vm.Settings.AutoResumeFailed = true;
        vm.Settings.MaxRetries = 3;
        vm.Tasks[1].AutoResumeAttempts = 3; // budget spent
        var cands = vm.CollectAutoResumeCandidates();
        Assert.Equal(2, cands.Count); // tasks 1 and 4 only

        vm.Settings.MaxRetries = 0; // 0 = no automatic retries at all
        Assert.Empty(vm.CollectAutoResumeCandidates());
    }

    // ---------- C1: notification policy ----------

    [Theory]
    [InlineData(TaskStatus.Queued, TaskStatus.Downloading, false, NotifyKind.None)]
    [InlineData(TaskStatus.Paused, TaskStatus.Downloading, false, NotifyKind.None)]
    [InlineData(TaskStatus.Downloading, TaskStatus.Failed, true, NotifyKind.Failed)]
    [InlineData(TaskStatus.Downloading, TaskStatus.Failed, false, NotifyKind.None)]
    [InlineData(TaskStatus.Downloading, TaskStatus.Paused, true, NotifyKind.None)]
    [InlineData(TaskStatus.Paused, TaskStatus.Paused, true, NotifyKind.None)]
    [InlineData(TaskStatus.Failed, TaskStatus.Downloading, true, NotifyKind.None)]
    public void NotificationCenter_Decide_Matrix(
        TaskStatus prev, TaskStatus cur, bool onError, NotifyKind expected)
    {
        var s = new AppSettings { NotifyOnError = onError };
        Assert.Equal(expected, NotificationCenter.Decide(prev, cur, s));
    }

    [Fact]
    public void PlayNotificationSound_NeverThrowsHeadless()
    {
        MainViewModel.PlayNotificationSound(isError: false);
        MainViewModel.PlayNotificationSound(isError: true);
    }

    // ---------- B1: triage helpers ----------

    [Theory]
    [InlineData("text/html", true)]
    [InlineData("text/html; charset=utf-8", false)] // raw incl. params is not a bare media type
    [InlineData("application/xhtml+xml", true)]
    [InlineData("video/mp4", false)]
    [InlineData("application/octet-stream", false)]
    [InlineData("", false)]
    public void IsHtmlContentType_Classifies(string mediaType, bool expected)
    {
        Assert.Equal(expected, DownloadEngine.IsHtmlContentType(mediaType));
    }

    [Theory]
    [InlineData("application/x-bittorrent", true)]
    [InlineData("application/x-magnet", true)]
    [InlineData("video/mp4", false)]
    [InlineData("", false)]
    public void IsTorrentContentType_Classifies(string mediaType, bool expected)
    {
        Assert.Equal(expected, DownloadEngine.IsTorrentContentType(mediaType));
    }

    [Theory]
    [InlineData("clip", "video/mp4", "clip.mp4")]
    [InlineData("clip", "video/mp4; codecs=\"avc1\"", "clip.mp4")]
    [InlineData("clip.mkv", "video/mp4", "clip.mkv")] // already has extension: untouched
    [InlineData("clip", "text/plain", "clip")] // 1DM rule: plain stays bare
    [InlineData("clip", "application/octet-stream", "clip")] // 1DM rule: octet-stream stays bare
    [InlineData("clip", "application/x-unknown-type", "clip")] // unmapped MIME: untouched
    [InlineData(null, "video/mp4", null)]
    [InlineData("", "video/mp4", "")]
    public void ApplyMimeExtensionFallback_Rule(string? name, string? mime, string? expected)
    {
        Assert.Equal(expected, DownloadEngine.ApplyMimeExtensionFallback(name, mime));
    }

    // ---------- Engine integration over loopback HTTP (end-user POV) ----------

    private sealed class LoopbackServer : IDisposable
    {
        private readonly HttpListener _listener = new();
        public string BaseUrl { get; }
        public Func<HttpListenerRequest, (int Status, string ContentType, string? Disposition, byte[] Body, bool HonorRange)> Handler { get; set; }
            = _ => (200, "application/octet-stream", null, Array.Empty<byte>(), true);

        public LoopbackServer()
        {
            int port;
            using (var tcp = new TcpListener(IPAddress.Loopback, 0))
            {
                tcp.Start();
                port = ((IPEndPoint)tcp.LocalEndpoint).Port;
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
            var req = ctx.Request;
            var resp = ctx.Response;
            try
            {
                var (status, contentType, disposition, body, honorRange) = Handler(req);
                byte[] payload = body;
                if (req.HttpMethod == "HEAD")
                {
                    resp.StatusCode = status;
                    resp.ContentType = contentType;
                    resp.ContentLength64 = body.Length;
                    resp.AddHeader("Accept-Ranges", "bytes");
                    if (disposition is not null)
                        resp.AddHeader("Content-Disposition", disposition);
                    resp.OutputStream.Close();
                    return;
                }
                string? range = req.Headers["Range"];
                if (honorRange && range is not null && range.StartsWith("bytes=", StringComparison.Ordinal))
                {
                    // bytes=N-M
                    string spec = range["bytes=".Length..];
                    string[] parts = spec.Split('-');
                    long start = long.Parse(parts[0]);
                    long end = parts.Length > 1 && parts[1].Length > 0 ? long.Parse(parts[1]) : body.Length - 1;
                    end = Math.Min(end, body.Length - 1);
                    payload = body[(int)start..((int)end + 1)];
                    resp.StatusCode = 206;
                    resp.AddHeader("Content-Range", $"bytes {start}-{end}/{body.Length}");
                }
                else
                {
                    resp.StatusCode = status;
                }
                resp.ContentType = contentType;
                if (disposition is not null)
                    resp.AddHeader("Content-Disposition", disposition);
                resp.AddHeader("Accept-Ranges", "bytes");
                resp.ContentLength64 = payload.Length;
                resp.OutputStream.Write(payload, 0, payload.Length);
                resp.OutputStream.Close();
            }
            catch
            {
                try { resp.Abort(); } catch { }
            }
        }

        public void Dispose()
        {
            try { _listener.Stop(); } catch { }
            try { _listener.Close(); } catch { }
        }
    }

    private static string NewTempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "wdm_r5_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static async Task<TaskStatus> WaitForSettledAsync(DownloadTask task, TimeSpan timeout)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            if (task.Status is TaskStatus.Completed or TaskStatus.Failed or TaskStatus.Paused)
                return task.Status;
            await Task.Delay(50);
        }
        return task.Status;
    }

    [Fact]
    public async Task Engine_HtmlPage_FailsWithActionableMessage()
    {
        using var server = new LoopbackServer();
        byte[] page = Encoding.UTF8.GetBytes("<html><body>watch page</body></html>");
        server.Handler = _ => (200, "text/html; charset=utf-8", null, page, false);

        var engine = new DownloadEngine();
        string dir = NewTempDir();
        try
        {
            var task = new DownloadTask
            {
                Url = server.BaseUrl + "watch?v=123",
                FileName = "download.bin",
                SaveFolder = dir,
            };
            engine.Start(task);
            Assert.Equal(TaskStatus.Failed, await WaitForSettledAsync(task, TimeSpan.FromSeconds(20)));
            Assert.Contains("web page", task.Error ?? "", StringComparison.OrdinalIgnoreCase);
            Assert.False(File.Exists(Path.Combine(dir, "download.bin")));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task Engine_TorrentMime_FailsWithDeferredMessage()
    {
        using var server = new LoopbackServer();
        byte[] torrent = Encoding.UTF8.GetBytes("d8:announce31:http://t.example.com/e");
        server.Handler = _ => (200, "application/x-bittorrent", "attachment; filename=\"ubuntu.torrent\"", torrent, false);

        var engine = new DownloadEngine();
        string dir = NewTempDir();
        try
        {
            var task = new DownloadTask
            {
                Url = server.BaseUrl + "ubuntu.torrent",
                FileName = "ubuntu.torrent",
                SaveFolder = dir,
            };
            engine.Start(task);
            Assert.Equal(TaskStatus.Failed, await WaitForSettledAsync(task, TimeSpan.FromSeconds(20)));
            Assert.Contains("torrent", task.Error ?? "", StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task Engine_DispositionWithoutExtension_GainsMimeExtension()
    {
        using var server = new LoopbackServer();
        byte[] data = Encoding.UTF8.GetBytes("hello-video-bytes-padded-to-size....");
        server.Handler = _ => (200, "video/mp4", "attachment; filename=\"clip\"", data, true);

        var engine = new DownloadEngine();
        string dir = NewTempDir();
        try
        {
            var task = new DownloadTask
            {
                Url = server.BaseUrl + "get?token=abc",
                FileName = "download.bin", // placeholder -> eligible for upgrade
                SaveFolder = dir,
            };
            engine.Start(task);
            Assert.Equal(TaskStatus.Completed, await WaitForSettledAsync(task, TimeSpan.FromSeconds(20)));
            Assert.Equal("clip.mp4", task.FileName);
            Assert.True(File.Exists(Path.Combine(dir, "clip.mp4")));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void Engine_ResumeAll_RespectsMaxConcurrent()
    {
        using var gate = new ManualResetEventSlim(false);
        using var server = new LoopbackServer();
        byte[] data = new byte[64];
        server.Handler = req =>
        {
            if (req.HttpMethod == "HEAD")
                gate.Wait(TimeSpan.FromSeconds(25)); // hold the probe: t1 stays active
            return (200, "application/octet-stream", null, data, true);
        };

        var engine = new DownloadEngine { MaxConcurrent = 1 };
        string dir = NewTempDir();
        try
        {
            var t1 = new DownloadTask { Url = server.BaseUrl + "a", FileName = "a.bin", SaveFolder = dir };
            var t2 = new DownloadTask { Url = server.BaseUrl + "b", FileName = "b.bin", SaveFolder = dir };
            var t3 = new DownloadTask { Url = server.BaseUrl + "c", FileName = "c.bin", SaveFolder = dir };
            engine.Start(t1);
            engine.Start(t2);
            engine.Start(t3);

            // Wait until t1 is active and the others queued.
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while ((engine.ActiveCount != 1 || engine.QueuedCount != 2) && sw.Elapsed < TimeSpan.FromSeconds(15))
                Thread.Sleep(50);
            Assert.Equal(1, engine.ActiveCount);
            Assert.Equal(2, engine.QueuedCount);

            // Old ResumeAll drained the queue with direct starts (ActiveCount -> 3,
            // bypassing the limit). New ResumeAll only pumps: limits hold.
            engine.ResumeAll();
            Thread.Sleep(500);
            Assert.True(engine.ActiveCount <= 1, $"ActiveCount={engine.ActiveCount} exceeds MaxConcurrent=1");
            Assert.Equal(2, engine.QueuedCount);
        }
        finally
        {
            gate.Set();
            try { engine.PauseAll(); } catch { }
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }
}
