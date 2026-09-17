using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using WDM.Services;
using WDM.Services.Embed;
using TaskStatus = WDM.Models.TaskStatus;

namespace WDM.Tests;

/// <summary>Round 7 — P4: frame-tree grabber decisions (B4), HLS key-hint
/// selection + SAMPLE-AES detection/fallback (B6b/B6c). Pure-logic xUnit plus
/// engine integration over loopback HTTP (real AES-128 segment decrypt via the
/// forwarded key hint; SAMPLE-AES packaged failure path). A relative garbage URI
/// still resolves (ResolveUrl never throws); a dead primary is covered at
/// fetch time by the hint retry (see AES integration test below).</summary>
public sealed class Round7Tests
{
    // ---------- B4: frame candidate selection ----------

    [Fact]
    public void SelectFrameCandidates_PlayersFirstDedupedCappedSkipsSelf()
    {
        var frames = new List<string>
        {
            "https://h.example.com/frame/1",
            "https://h.example.com/e/abc",
            "https://h.example.com/frame/1", // dup
            "https://h.example.com/embed/xyz",
            "https://h.example.com/frame/2",
            "https://h.example.com/frame/3",
            "https://h.example.com/frame/4",
            "https://h.example.com/frame/5",
            "https://h.example.com/frame/6", // over cap
        };
        var got = EmbedResolver.SelectFrameCandidates(frames, "https://h.example.com/watch/1");
        Assert.Equal(5, got.Count);
        // Player frames first, in encounter order; then generic frames.
        Assert.Equal("https://h.example.com/e/abc", got[0]);
        Assert.Equal("https://h.example.com/embed/xyz", got[1]);
        Assert.Equal("https://h.example.com/frame/1", got[2]);
        Assert.DoesNotContain("https://h.example.com/frame/6", got);
    }

    [Fact]
    public void SelectFrameCandidates_SkipsSelfLinks()
    {
        var got = EmbedResolver.SelectFrameCandidates(
            new List<string> { "https://h.example.com/e/abc", "https://h.example.com/e/abc/" },
            "https://h.example.com/e/abc");
        Assert.Empty(got);
    }

    [Fact]
    public void LooksLikePlayerPage_Heuristics()
    {
        Assert.False(EmbedResolver.LooksLikePlayerPage("", "https://h.example.com/e/abc"));
        Assert.False(EmbedResolver.LooksLikePlayerPage("<html><body>hello</body></html>", "https://h.example.com/watch/1"));
        // Player path alone qualifies.
        Assert.True(EmbedResolver.LooksLikePlayerPage("<html></html>", "https://h.example.com/embed/xyz"));
        // Bare media URL in HTML qualifies.
        Assert.True(EmbedResolver.LooksLikePlayerPage(
            "<html><video><source src=\"https://cdn.example.com/v.mp4\"></video></html>",
            "https://h.example.com/frame/9"));
    }

    // ---------- B6b/B6c: playlist parsing + key selection ----------

    private const string AesPlaylist = "#EXTM3U\n#EXT-X-VERSION:3\n#EXT-X-TARGETDURATION:10\n" +
        "#EXT-X-KEY:METHOD=AES-128,URI=\"/key.bin\"\n#EXTINF:10,\nseg0.ts\n#EXT-X-ENDLIST\n";

    private const string SampleAesPlaylist = "#EXTM3U\n#EXT-X-VERSION:5\n#EXT-X-TARGETDURATION:10\n" +
        "#EXT-X-KEY:METHOD=SAMPLE-AES,URI=\"skd://key\",KEYFORMAT=\"com.apple.streamingkeydelivery\"\n" +
        "#EXTINF:10,\nseg0.ts\n#EXT-X-ENDLIST\n";

    [Fact]
    public void ParsePlaylist_Aes128_KeysSegmentsNoFlag()
    {
        var pl = HlsDownloader.ParsePlaylist(AesPlaylist, "https://h.example.com/hls/index.m3u8");
        Assert.NotNull(pl);
        Assert.False(pl.HasUnsupportedEncryption);
        Assert.Single(pl.Segments);
        Assert.Equal("/key.bin", pl.Segments[0].KeyUri);
        Assert.NotNull(pl.Segments[0].Iv);
    }

    [Fact]
    public void ParsePlaylist_SampleAes_FlagsAndLeavesClear()
    {
        var pl = HlsDownloader.ParsePlaylist(SampleAesPlaylist, "https://h.example.com/hls/index.m3u8");
        Assert.NotNull(pl);
        Assert.True(pl.HasUnsupportedEncryption);
        Assert.Equal("SAMPLE-AES", pl.UnsupportedMethod);
        Assert.Single(pl.Segments);
        Assert.Null(pl.Segments[0].KeyUri); // not CBC-decryptable: must not attempt
    }

    [Fact]
    public void ParsePlaylist_NoneMethod_NoFlag()
    {
        var pl = HlsDownloader.ParsePlaylist("#EXTM3U\n#EXT-X-KEY:METHOD=NONE\n#EXTINF:10,\nseg0.ts\n", "https://h.example.com/a.m3u8");
        Assert.NotNull(pl);
        Assert.False(pl.HasUnsupportedEncryption);
    }

    [Theory]
    [InlineData("/key.bin", "https://h.example.com/keys/k", "https://h.example.com/m.m3u8", "https://h.example.com/key.bin")]
    [InlineData("https://k.example.com/k", null, "https://h.example.com/m.m3u8", "https://k.example.com/k")]
    [InlineData(null, "https://k.example.com/k", "https://h.example.com/m.m3u8", "https://k.example.com/k")]
    [InlineData("::garbage::", "https://k.example.com/k", "https://h.example.com/m.m3u8", "https://h.example.com/::garbage::")]
    [InlineData(null, null, "https://h.example.com/m.m3u8", null)]
    [InlineData(null, "ftp://k.example.com/k", "https://h.example.com/m.m3u8", null)]
    public void SelectKeyUrl_PrefersPlaylistFallsBackToHint(string? playlistUri, string? hint, string manifest, string? expected)
    {
        Assert.Equal(expected, HlsDownloader.SelectKeyUrl(playlistUri, hint, manifest));
    }

    [Fact]
    public void HlsPackagedStreamException_CarriesMethod()
    {
        var ex = new HlsDownloader.HlsPackagedStreamException("SAMPLE-AES");
        Assert.Equal("SAMPLE-AES", ex.Method);
        Assert.Contains("SAMPLE-AES", ex.Message);
    }

    // ---------- Engine integration over loopback HTTP ----------

    private sealed class LoopbackServer : IDisposable
    {
        private readonly HttpListener _listener = new();
        public string BaseUrl { get; }
        public Func<HttpListenerRequest, (int Status, string ContentType, byte[] Body)> Handler { get; set; }
            = _ => (200, "application/octet-stream", Array.Empty<byte>());

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
                var (status, contentType, body) = Handler(req);
                string? range = req.Headers["Range"];
                byte[] payload = body;
                if (req.HttpMethod != "HEAD" && range is not null && range.StartsWith("bytes=", StringComparison.Ordinal))
                {
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
                resp.AddHeader("Accept-Ranges", "bytes");
                resp.ContentLength64 = payload.Length;
                if (req.HttpMethod != "HEAD")
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
        string dir = Path.Combine(Path.GetTempPath(), "wdm_r7_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static async Task<TaskStatus> WaitForSettledAsync(WDM.Models.DownloadTask task, TimeSpan timeout)
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
    public async Task Engine_Aes128KeyHintFallback_DecryptsSegment()
    {
        // Real AES-128-CBC ciphertext (zero key/IV) so the decrypt path is genuine.
        byte[] key = new byte[16];
        byte[] plain = Encoding.ASCII.GetBytes("HelloWorld123456");
        byte[] cipher;
        using (var aes = Aes.Create())
        {
            aes.KeySize = 128; aes.BlockSize = 128; aes.Mode = CipherMode.CBC; aes.Padding = PaddingMode.PKCS7;
            aes.Key = key; aes.IV = new byte[16];
            using var enc = aes.CreateEncryptor();
            cipher = enc.TransformFinalBlock(plain, 0, plain.Length);
        }

        using var server = new LoopbackServer();
        string playlist = "#EXTM3U\n#EXT-X-VERSION:3\n#EXT-X-TARGETDURATION:10\n" +
            "#EXT-X-KEY:METHOD=AES-128,URI=\"/dead-key.bin\"\n#EXTINF:10,\nseg0.ts\n#EXT-X-ENDLIST\n";
        server.Handler = req => req.Url!.AbsolutePath switch
        {
            "/stream.m3u8" => (200, "application/x-mpegurl", Encoding.UTF8.GetBytes(playlist)),
            "/dead-key.bin" => (404, "text/plain", Encoding.UTF8.GetBytes("rotated")),
            "/real-key.bin" => (200, "application/octet-stream", key),
            "/seg0.ts" => (200, "video/mp2t", cipher),
            _ => (404, "text/plain", Array.Empty<byte>()),
        };

        var engine = new DownloadEngine();
        string dir = NewTempDir();
        try
        {
            var task = new WDM.Models.DownloadTask
            {
                Url = server.BaseUrl + "stream.m3u8",
                FileName = "hint.ts",
                SaveFolder = dir,
            };
            // Extension-forwarded key observation (B6b): playlist key is dead (404),
            // the hint carries the working URL. Same scoped credentials verify it.
            task.Headers["X-WDM-KeyUrl"] = server.BaseUrl + "real-key.bin";
            engine.Start(task);
            Assert.Equal(TaskStatus.Completed, await WaitForSettledAsync(task, TimeSpan.FromSeconds(30)));
            string outFile = Path.Combine(dir, "hint.ts");
            Assert.True(File.Exists(outFile));
            Assert.Equal(plain, await File.ReadAllBytesAsync(outFile));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task Engine_SampleAes_FailsActionableWithoutFfmpeg()
    {
        if (File.Exists(EngineManager.FfmpegPath))
            return; // ffmpeg present: the fallback runs instead (manual E2E covers it).
        using var server = new LoopbackServer();
        byte[] body = Encoding.UTF8.GetBytes(SampleAesPlaylist);
        server.Handler = _ => (200, "application/x-mpegurl", body);

        var engine = new DownloadEngine();
        string dir = NewTempDir();
        try
        {
            var task = new WDM.Models.DownloadTask
            {
                Url = server.BaseUrl + "s.m3u8",
                FileName = "s.ts",
                SaveFolder = dir,
            };
            engine.Start(task);
            Assert.Equal(TaskStatus.Failed, await WaitForSettledAsync(task, TimeSpan.FromSeconds(30)));
            Assert.Contains("SAMPLE-AES", task.Error ?? "", StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }
}
