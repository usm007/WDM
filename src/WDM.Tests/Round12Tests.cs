using System.Net;
using System.Net.Sockets;
using System.Text;
using WDM.Models;
using WDM.Services;
using TaskStatus = WDM.Models.TaskStatus;

namespace WDM.Tests;

/// <summary>Round 12 — codeload master.zip regression (reported failure: Failed 404).
/// Bare-substring manifest cues (/master, /playlist, /manifest) misclassified real
/// filenames as HLS/DASH; the binary then parsed as playlist segments that 404d.
/// Boundary-guarded cues + mandatory #EXTM3U magic close both holes.</summary>
public sealed class Round12Tests
{
    [Theory]
    // Real files: no cue at all → direct download, not even a sniff.
    // (The reported failure: master.zip carried a /master cue and skipped verification.)
    [InlineData("https://github.com/Hibbiki/chromium-win64/archive/refs/heads/master.zip", false)]
    [InlineData("https://example.com/videos/master.mp4", false)]
    [InlineData("https://example.com/a/playlists", false)]
    [InlineData("https://example.com/a/playlist2", false)]
    [InlineData("https://example.com/a/manifest.json", false)]
    [InlineData("https://example.com/a/manifests/x", false)]
    [InlineData("https://example.com/a/mastering.mp4", false)]
    [InlineData("https://example.com/streaming/live", false)]
    // Cues: trigger the content sniff, which decides by bytes (PK ≠ #EXTM3U).
    // Bare /master still cues (tokenized endpoints exist) but can no longer force HLS.
    [InlineData("https://codeload.github.com/Hibbiki/chromium-win64/zip/refs/heads/master", true)]
    [InlineData("https://example.com/master", true)]
    [InlineData("https://example.com/master/playlist", true)]
    // True manifests, still caught.
    [InlineData("https://example.com/hls/master.m3u8", true)]
    [InlineData("https://example.com/master-720p.m3u8", true)]
    [InlineData("https://example.com/hls/stream", true)]
    [InlineData("https://example.com/playlist", true)]
    [InlineData("https://example.com/playlist/", true)]
    [InlineData("https://example.com/api/manifest/123", true)]
    [InlineData("https://example.com/stream/abc", true)]
    [InlineData("https://example.com/get?format=m3u8&id=1", true)]
    public void LooksLikeHlsUrl_Boundaries(string url, bool expected)
    {
        Assert.Equal(expected, DownloadEngine.LooksLikeHlsUrl(url));
    }

    [Theory]
    [InlineData("https://example.com/dash/stream.mpd", true)]
    [InlineData("https://example.com/api/manifest/7", true)]
    [InlineData("https://example.com/master", true)]
    [InlineData("https://example.com/a/master.zip", false)]
    [InlineData("https://example.com/a/manifest.json", false)]
    [InlineData("https://example.com/a/file.mp4", false)]
    public void LooksLikeDashUrl_Boundaries(string url, bool expected)
    {
        Assert.Equal(expected, DownloadEngine.LooksLikeDashUrl(url));
    }

    [Fact]
    public void ParsePlaylist_RejectsBinaryGarbage()
    {
        // Simulated ZIP bytes with embedded newlines (what Split('\n') sees).
        string garbage = "PK\x03\x04\x14\x00\x06\x00\x08\x00!\x00m\x9d\x04\x91\xcf\x01\x00\x00\x10\x05\x00\x00\x13\x00\x1c\x00\n" +
            "chromium-win64-master/\x01\x02\x3f\x00\n" +
            "random\xff\xfetrailer\n";
        Assert.Null(HlsDownloader.ParsePlaylist(garbage, "https://h.example.com/x"));
        Assert.Null(HlsDownloader.ParsePlaylist("", "https://h.example.com/x"));
        Assert.Null(HlsDownloader.ParsePlaylist("<html><body>nope</body></html>", "https://h.example.com/x"));
    }

    [Fact]
    public void ParsePlaylist_ToleratesBomAndBlankLead()
    {
        string text = "\n  \n\uFEFF#EXTM3U\n#EXTINF:10,\nseg0.ts\n#EXT-X-ENDLIST\n";
        var pl = HlsDownloader.ParsePlaylist(text, "https://h.example.com/a.m3u8");
        Assert.NotNull(pl);
        Assert.Single(pl.Segments);
    }

    [Fact]
    public void ParsePlaylist_RejectsCommentsBeforeMagic()
    {
        // #EXTM3U must be first per spec; anything else first is not a playlist.
        string text = "#EXT-X-VERSION:3\n#EXTM3U\n#EXTINF:10,\nseg0.ts\n";
        Assert.Null(HlsDownloader.ParsePlaylist(text, "https://h.example.com/a.m3u8"));
    }

    // End-user regression: a zip served under a /master.zip path completes as a
    // file (previously detoured into the HLS path and failed 404 on segments).
    [Fact]
    public async Task Engine_MasterZipPath_DownloadsAsFile()
    {
        using var server = new ZipLoopback();
        var engine = new DownloadEngine();
        string dir = Path.Combine(Path.GetTempPath(), "wdm_r12_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var task = new DownloadTask
            {
                Url = server.BaseUrl + "archive/refs/heads/master.zip",
                FileName = "master.zip",
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
            Assert.Equal(TaskStatus.Completed, task.Status);
            Assert.Equal(server.Body, await File.ReadAllBytesAsync(Path.Combine(dir, "master.zip")));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    // Bare cue path (/master, no extension): the content sniff sees PK bytes,
    // rejects HLS, and the body downloads as a file.
    [Fact]
    public async Task Engine_BareMasterPath_DownloadsAsFileAfterSniff()
    {
        using var server = new ZipLoopback();
        var engine = new DownloadEngine();
        string dir = Path.Combine(Path.GetTempPath(), "wdm_r12b_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var task = new DownloadTask
            {
                Url = server.BaseUrl + "zip/refs/heads/master",
                FileName = "master",
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
            Assert.Equal(TaskStatus.Completed, task.Status);
            // Extension-less name is upgraded from the disposition header.
            Assert.Equal(server.Body, await File.ReadAllBytesAsync(task.FullPath));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    private sealed class ZipLoopback : IDisposable
    {
        private readonly HttpListener _listener = new();
        public string BaseUrl { get; }
        public byte[] Body { get; } = Encoding.UTF8.GetBytes(
            "PK\x03\x04fake-zip-payload-for-regression-test\nsecond-line\xff\xfe\n");

        public ZipLoopback()
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
                // Codeload behavior: no Content-Length/Accept-Ranges on HEAD,
                // Range ignored (200 + full body), zip content-type + disposition.
                if (req.HttpMethod == "HEAD")
                {
                    resp.StatusCode = 200;
                    resp.ContentType = "application/zip";
                    resp.OutputStream.Close();
                    return;
                }
                resp.StatusCode = 200;
                resp.ContentType = "application/zip";
                resp.AddHeader("Content-Disposition", "attachment; filename=repro-master.zip");
                resp.AddHeader("Accept-Ranges", "bytes");
                resp.ContentLength64 = Body.Length;
                resp.OutputStream.Write(Body, 0, Body.Length);
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

    // End-user regression (seedr): a signed hand-off link (/download/archive/<hash>)
    // 302s to a final URL whose path carries the real filename, while the response
    // headers carry a bare `Content-Disposition: attachment` (no filename).
    // The probe must name the file from the redirect target, like IDM does —
    // previously it kept the generic download_*.bin fallback.
    [Theory]
    // Pure offline proof on the reported shapes: hash hand-off → unusable,
    // redirect target → real filename (IDM parity).
    [InlineData("https://www.seedr.cc/download/archive/c1c73193235dce6ccc82b3fd7d9f3d56a53956fcdb1469431958baa63727b9ce?token=abc&exp=123", null)]
    [InlineData("https://nw35.seedr.cc/get_zip_ngen_free/29475024/The%20Strain%20S01%20Season%201%20Complete%20HDTV%20480p%20x264%20AAC%20E-Subs%20[GWC].zip?st=abc&e=123", "Strain")]
    public void FileNameFromUrlPath_SeedrShapes(string url, string? mustContain)
    {
        string? name = DownloadEngine.FileNameFromUrlPath(url);
        if (mustContain is null)
            Assert.Null(name);
        else
        {
            Assert.NotNull(name);
            Assert.Contains(mustContain, name, StringComparison.OrdinalIgnoreCase);
            Assert.EndsWith(".zip", name, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task Engine_RedirectTargetFilename_RenamesGenericTask()
    {
        using var server = new RedirectNameLoopback();
        var engine = new DownloadEngine();
        string dir = Path.Combine(Path.GetTempPath(), "wdm_r12c_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var task = new DownloadTask
            {
                Url = server.BaseUrl + "download/archive/c1c73193235dce6ccc82b3fd7d9f3d56a53956fcdb1469431958baa63727b9ce?token=abc&exp=123",
                FileName = "download_2026-09-24_202332.bin",
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
            Assert.Equal(TaskStatus.Completed, task.Status);
            Assert.Contains("Strain", task.FileName, StringComparison.OrdinalIgnoreCase);
            Assert.EndsWith(".zip", task.FileName, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(server.Body, await File.ReadAllBytesAsync(task.FullPath));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    private sealed class RedirectNameLoopback : IDisposable
    {
        private readonly HttpListener _listener = new();
        public string BaseUrl { get; }
        public byte[] Body { get; } = Encoding.UTF8.GetBytes(
            "PK\x03\x04fake-zip-payload-for-redirect-name-test\n");
        private const string FinalPath = "/get_zip_ngen_free/29475082/The%20Strain%20S01%20Complete.zip";

        public RedirectNameLoopback()
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
                // Signed hand-off: redirect to the real file URL (filename in path).
                if (req.Url!.AbsolutePath.StartsWith("/download/archive/", StringComparison.OrdinalIgnoreCase))
                {
                    resp.StatusCode = 302;
                    resp.RedirectLocation = FinalPath;
                    resp.OutputStream.Close();
                    return;
                }
                // Final file: bare `attachment` disposition (no filename), zip type.
                if (req.HttpMethod == "HEAD")
                {
                    resp.StatusCode = 200;
                    resp.ContentType = "application/zip";
                    resp.ContentLength64 = Body.Length;
                    resp.AddHeader("Content-Disposition", "attachment");
                    resp.AddHeader("Accept-Ranges", "bytes");
                    resp.OutputStream.Close();
                    return;
                }
                string? range = req.Headers["Range"];
                if (range is not null && range.StartsWith("bytes=", StringComparison.Ordinal))
                {
                    string spec = range["bytes=".Length..];
                    string[] parts = spec.Split('-');
                    long start = long.Parse(parts[0]);
                    long end = parts.Length > 1 && parts[1].Length > 0 ? long.Parse(parts[1]) : Body.Length - 1;
                    end = Math.Min(end, Body.Length - 1);
                    byte[] payload = Body[(int)start..((int)end + 1)];
                    resp.StatusCode = 206;
                    resp.AddHeader("Content-Range", $"bytes {start}-{end}/{Body.Length}");
                    resp.ContentType = "application/zip";
                    resp.AddHeader("Content-Disposition", "attachment");
                    resp.AddHeader("Accept-Ranges", "bytes");
                    resp.ContentLength64 = payload.Length;
                    resp.OutputStream.Write(payload, 0, payload.Length);
                    resp.OutputStream.Close();
                    return;
                }
                resp.StatusCode = 200;
                resp.ContentType = "application/zip";
                resp.AddHeader("Content-Disposition", "attachment");
                resp.AddHeader("Accept-Ranges", "bytes");
                resp.ContentLength64 = Body.Length;
                resp.OutputStream.Write(Body, 0, Body.Length);
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
