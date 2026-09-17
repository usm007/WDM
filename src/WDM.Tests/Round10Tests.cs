using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using WDM.Models;
using WDM.Services;
using TaskStatus = WDM.Models.TaskStatus;

namespace WDM.Tests;

/// <summary>Round 10 — P7: stable Id + segment snapshot persistence (A6),
/// sidecar-loss resume from snapshot, HLS merge diagnostics (C4).
/// TaskStore.AppDir is redirected to temp dirs (restored afterwards).</summary>
public sealed class Round10Tests : IDisposable
{
    private readonly string _appDir = Path.Combine(Path.GetTempPath(), "wdm_r10app_" + Guid.NewGuid().ToString("N"));
    private readonly string _realAppDir;

    public Round10Tests()
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

    // ---------- A6: Id + snapshot round-trip ----------

    [Fact]
    public void SaveLoad_PreservesIdAndSegments()
    {
        var id = Guid.NewGuid();
        var task = new DownloadTask
        {
            Url = "https://cdn.example.com/f.mp4",
            FileName = "f.mp4",
            SaveFolder = Path.GetTempPath(),
            TotalBytes = 1000,
            Status = TaskStatus.Paused,
        };
        task.Id = id;
        var snap = new List<SegmentRecord>
        {
            new() { Index = 0, Start = 0, End = 499, Done = true },
            new() { Index = 1, Start = 500, End = 999, Done = false },
        };
        TaskStore.SaveTasks(new[] { task }, _ => snap);

        var loaded = TaskStore.LoadTasks();
        var rec = Assert.Single(loaded);
        Assert.Equal(id, rec.Id);
        Assert.NotNull(rec.Segments);
        Assert.Equal(2, rec.Segments.Count);
        Assert.True(rec.Segments[0].Done);
        Assert.False(rec.Segments[1].Done);
    }

    [Fact]
    public void LoadToleratesLegacyFileWithoutIdOrSegments()
    {
        string tasksPath = Path.Combine(_appDir, "tasks.json");
        File.WriteAllText(tasksPath, "[{\"Url\":\"https://cdn.example.com/old.mp4\",\"FileName\":\"old.mp4\",\"SaveFolder\":\"C:\\\\Temp\",\"Status\":2}]");
        var loaded = TaskStore.LoadTasks();
        var rec = Assert.Single(loaded);
        Assert.Equal(Guid.Empty, rec.Id);
        Assert.Null(rec.Segments);
        Assert.Equal("https://cdn.example.com/old.mp4", rec.Url);
    }

    [Fact]
    public void SnapshotSegments_NullWithoutLiveSession()
    {
        var engine = new DownloadEngine();
        var task = new DownloadTask { Url = "https://s.example.com/x", FileName = "x.bin", SaveFolder = Path.GetTempPath() };
        Assert.Null(engine.SnapshotSegments(task));
    }

    // ---------- A6: sidecar-loss resume (end-user POV) ----------

    private sealed class ThrottledServer : IDisposable
    {
        private readonly HttpListener _listener = new();
        public string BaseUrl { get; }
        public long BytesServed;
        private readonly byte[] _body;

        public ThrottledServer(int sizeBytes)
        {
            _body = new byte[sizeBytes];
            new Random(42).NextBytes(_body);
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

        public byte[] Body => _body;

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
                string? range = req.Headers["Range"];
                long start = 0, end = _body.Length - 1;
                if (range is not null && range.StartsWith("bytes=", StringComparison.Ordinal))
                {
                    string[] parts = range["bytes=".Length..].Split('-');
                    start = long.Parse(parts[0]);
                    if (parts.Length > 1 && parts[1].Length > 0)
                        end = Math.Min(long.Parse(parts[1]), _body.Length - 1);
                    resp.StatusCode = 206;
                    resp.AddHeader("Content-Range", $"bytes {start}-{end}/{_body.Length}");
                }
                else
                {
                    resp.StatusCode = 200;
                }
                resp.ContentType = "application/octet-stream";
                resp.AddHeader("Accept-Ranges", "bytes");
                // Slow drip so the test can pause mid-download.
                const int slice = 64 * 1024;
                long total = end - start + 1;
                resp.ContentLength64 = req.HttpMethod == "HEAD" ? _body.Length : total;
                if (req.HttpMethod != "HEAD")
                {
                    for (long off = start; off <= end; off += slice)
                    {
                        int n = (int)Math.Min(slice, end - off + 1);
                        resp.OutputStream.Write(_body, (int)off, n);
                        resp.OutputStream.Flush();
                        Thread.Sleep(15);
                    }
                }
                resp.OutputStream.Close();
                Interlocked.Add(ref BytesServed, req.HttpMethod == "HEAD" ? 0 : total);
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

    private static async Task<TaskStatus> WaitForAsync(DownloadTask task, Func<bool> done, TimeSpan timeout)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            if (done())
                return task.Status;
            await Task.Delay(50);
        }
        return task.Status;
    }

    [Fact]
    public async Task Resume_AfterSidecarLoss_UsesSnapshotInsteadOfRefetch()
    {
        using var server = new ThrottledServer(4 * 1024 * 1024);
        var engine = new DownloadEngine();
        string dir = Path.Combine(Path.GetTempPath(), "wdm_r10_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var task = new DownloadTask { Url = server.BaseUrl + "big.bin", FileName = "big.bin", SaveFolder = dir };
            engine.Start(task);
            // Pause only after whole chunks completed (BytesDownloaded counts
            // partial reads; a snapshot with zero Done chunks would legitimately
            // refetch everything and flake the assertion below).
            var readySw = System.Diagnostics.Stopwatch.StartNew();
            List<SegmentRecord>? live = null;
            while (readySw.Elapsed < TimeSpan.FromSeconds(30))
            {
                live = engine.SnapshotSegments(task);
                if (live is not null && live.Count(r => r.Done) >= 2 &&
                    task.DownloadedBytes < server.Body.Length - 1000L)
                    break;
                live = null;
                await Task.Delay(100);
            }
            Assert.NotNull(live);
            // Snapshot while the session is live (like SaveTasks does on its tick:
            // paused tasks have no session, so the mirror is always taken live).
            var snap = engine.SnapshotSegments(task);
            Assert.NotNull(snap);
            Assert.Contains(snap, r => r.Done);
            Assert.Contains(snap, r => !r.Done);
            engine.Pause(task);
            await WaitForAsync(task, () => task.Status == TaskStatus.Paused, TimeSpan.FromSeconds(15));
            Assert.Equal(TaskStatus.Paused, task.Status);
            task.SegmentSnapshot = snap;

            // Simulate sidecar loss (cleaner tools, manual delete, crash).
            string sidecar = task.FullPath + ".wdmstate";
            Assert.True(File.Exists(sidecar));
            File.Delete(sidecar);

            long servedBefore = Interlocked.Read(ref server.BytesServed);
            engine.Start(task);
            Assert.Equal(TaskStatus.Completed, await WaitForAsync(task, () => task.Status == TaskStatus.Completed, TimeSpan.FromSeconds(60)));
            long servedAfter = Interlocked.Read(ref server.BytesServed) - servedBefore;

            // Snapshot resume: only the missing tail is re-fetched, not the whole file.
            Assert.True(servedAfter < server.Body.Length,
                $"served {servedAfter} of {server.Body.Length} after resume — snapshot was ignored");
            Assert.Equal(server.Body, await File.ReadAllBytesAsync(Path.Combine(dir, "big.bin")));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    // ---------- C4: merge diagnostics ----------

    [Fact]
    public async Task HlsDownload_RecordsMergeInfo()
    {
        using var server = new HlsLoopback();
        var engine = new DownloadEngine();
        string dir = Path.Combine(Path.GetTempPath(), "wdm_r10h_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var task = new DownloadTask { Url = server.BaseUrl + "v.m3u8", FileName = "v.ts", SaveFolder = dir };
            engine.Start(task);
            Assert.Equal(TaskStatus.Completed, await WaitForAsync(task, () => task.Status == TaskStatus.Completed, TimeSpan.FromSeconds(30)));
            Assert.NotNull(HlsDownloader.LastMerge);
            Assert.Equal(2, HlsDownloader.LastMerge.Segments);
            Assert.True(HlsDownloader.LastMerge.Bytes > 0);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    private sealed class HlsLoopback : IDisposable
    {
        private readonly HttpListener _listener = new();
        public string BaseUrl { get; }
        private readonly byte[] _seg = Encoding.UTF8.GetBytes(new string('s', 188 * 10));

        public HlsLoopback()
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
            try
            {
                var req = ctx.Request;
                var resp = ctx.Response;
                byte[] body = req.Url!.AbsolutePath.EndsWith(".m3u8")
                    ? Encoding.UTF8.GetBytes("#EXTM3U\n#EXT-X-VERSION:3\n#EXT-X-TARGETDURATION:10\n#EXTINF:10,\nseg0.ts\n#EXTINF:10,\nseg1.ts\n#EXT-X-ENDLIST\n")
                    : _seg;
                string ct = req.Url.AbsolutePath.EndsWith(".m3u8") ? "application/x-mpegurl" : "video/mp2t";
                string? range = req.Headers["Range"];
                if (req.HttpMethod != "HEAD" && range is not null && range.StartsWith("bytes=", StringComparison.Ordinal))
                {
                    string[] parts = range["bytes=".Length..].Split('-');
                    long start = long.Parse(parts[0]);
                    long end = parts.Length > 1 && parts[1].Length > 0 ? Math.Min(long.Parse(parts[1]), body.Length - 1) : body.Length - 1;
                    body = body[(int)start..((int)end + 1)];
                    resp.StatusCode = 206;
                    resp.AddHeader("Content-Range", $"bytes {start}-{end}/{_seg.Length}");
                }
                else
                {
                    resp.StatusCode = 200;
                }
                resp.ContentType = ct;
                resp.AddHeader("Accept-Ranges", "bytes");
                resp.ContentLength64 = body.Length;
                if (req.HttpMethod != "HEAD")
                    resp.OutputStream.Write(body, 0, body.Length);
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
