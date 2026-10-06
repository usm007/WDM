using System.Net;
using System.Security.Cryptography;
using System.Text;
using WDM.Services;
using WDM.Tests.TestInfrastructure;

namespace WDM.Tests;

/// <summary>In-process HLS tests over a fake handler (no TCP).</summary>
public sealed class HlsDownloaderTests
{
    private sealed class FakeHlsOrigin : HttpMessageHandler
    {
        public readonly Dictionary<string, Func<HttpRequestMessage, HttpResponseMessage>> Routes = new(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string, int> Hits = new(StringComparer.OrdinalIgnoreCase);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            string url = request.RequestUri!.AbsoluteUri;
            Hits[url] = Hits.TryGetValue(url, out int n) ? n + 1 : 1;
            foreach (var kv in Routes)
                if (url.StartsWith(kv.Key, StringComparison.OrdinalIgnoreCase))
                    return Task.FromResult(kv.Value(request));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound) { RequestMessage = request });
        }
    }

    private static byte[] IvOf(int i)
    {
        var iv = new byte[16];
        iv[15] = (byte)i; iv[14] = (byte)(i >> 8); iv[13] = (byte)(i >> 16); iv[12] = (byte)(i >> 24);
        return iv;
    }

    private static byte[] AesEnc(byte[] plain, byte[] key, int i)
    {
        using var aes = Aes.Create();
        aes.Key = key; aes.IV = IvOf(i); aes.Mode = CipherMode.CBC; aes.Padding = PaddingMode.PKCS7;
        return aes.CreateEncryptor().TransformFinalBlock(plain, 0, plain.Length);
    }

    private static byte[] Payload(int i, int size = 4096)
    {
        var b = new byte[size];
        for (int k = 0; k < size; k++) b[k] = (byte)((i * 31 + k) & 0xFF);
        return b;
    }

    private static string Vod(string baseUrl, int n)
    {
        var sb = new StringBuilder("#EXTM3U\n#EXT-X-VERSION:3\n#EXT-X-TARGETDURATION:6\n");
        for (int i = 0; i < n; i++) sb.Append($"#EXTINF:6.0,\n{baseUrl}/seg{i}.ts\n");
        return sb.Append("#EXT-X-ENDLIST\n").ToString();
    }

    private static HttpResponseMessage Text(string s, string ct = "application/vnd.apple.mpegurl")
    {
        var r = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(s, Encoding.UTF8, ct) };
        r.Content.Headers.ContentLength = Encoding.UTF8.GetByteCount(s);
        return r;
    }

    private static async Task<string> RunDl(FakeHlsOrigin origin, string manifestUrl, string dir, string file = "out.ts", CancellationToken ct = default)
    {
        using var http = new HttpClient(origin) { Timeout = TimeSpan.FromSeconds(60) };
        string dest = Path.Combine(dir, file);
        await HlsDownloader.DownloadAsync(http, manifestUrl, null, dest, ct, _ => { }, _ => { }, (_, _) => Task.CompletedTask);
        return dest;
    }

    private static string TempDir()
    {
        string d = Path.Combine(Path.GetTempPath(), "wdm-hls", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    [Trait("Category", Cats.Unit)][Fact]
    public async Task Playlist_SegmentCount_And_KeyUri()
    {
        string dir = TempDir();
        try
        {
            var o = new FakeHlsOrigin();
            o.Routes["http://h/manifest.m3u8"] = _ => Text(Vod("http://h", 4));
            for (int i = 0; i < 4; i++)
            {
                int k = i;
                o.Routes[$"http://h/seg{k}.ts"] = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Payload(k)) };
            }
            string dest = await RunDl(o, "http://h/manifest.m3u8", dir);
            Assert.Equal(4 * 4096, new FileInfo(dest).Length);
        }
        finally { TestFiles.DeleteDir(dir); }
    }

    [Trait("Category", Cats.Unit)][Fact]
    public async Task Aes128_KnownPlaintext_RoundTrips()
    {
        string dir = TempDir();
        try
        {
            byte[] key = RandomNumberGenerator.GetBytes(16);
            var o = new FakeHlsOrigin();
            var sb = new StringBuilder("#EXTM3U\n#EXT-X-VERSION:3\n#EXT-X-TARGETDURATION:6\n");
            for (int i = 0; i < 3; i++)
            {
                int k = i;
                byte[] enc = AesEnc(Payload(k), key, k);
                o.Routes[$"http://h/seg{k}.ts"] = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(enc) };
                sb.Append($"#EXT-X-KEY:METHOD=AES-128,URI=\"http://h/key.bin\",IV=0x0000000000000000000000000000000{k}\n");
                sb.Append($"#EXTINF:6.0,\nhttp://h/seg{k}.ts\n");
            }
            sb.Append("#EXT-X-ENDLIST\n");
            o.Routes["http://h/manifest.m3u8"] = _ => Text(sb.ToString());
            o.Routes["http://h/key.bin"] = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(key) };
            string dest = await RunDl(o, "http://h/manifest.m3u8", dir);
            using var ms = new MemoryStream();
            for (int i = 0; i < 3; i++) { byte[] p = Payload(i); ms.Write(p, 0, p.Length); }
            Assert.Equal(ms.ToArray(), await File.ReadAllBytesAsync(dest));
        }
        finally { TestFiles.DeleteDir(dir); }
    }

    [Trait("Category", Cats.Unit)][Fact]
    public async Task SampleAes_Throws_PackagedException()
    {
        string dir = TempDir();
        try
        {
            var o = new FakeHlsOrigin();
            o.Routes["http://h/manifest.m3u8"] = _ => Text("#EXTM3U\n#EXT-X-VERSION:3\n#EXT-X-KEY:METHOD=SAMPLE-AES,URI=\"http://h/key.bin\"\n#EXTINF:6.0,\nhttp://h/seg0.ts\n#EXT-X-ENDLIST\n");
            await Assert.ThrowsAsync<HlsDownloader.HlsPackagedStreamException>(() => RunDl(o, "http://h/manifest.m3u8", dir));
        }
        finally { TestFiles.DeleteDir(dir); }
    }

    [Trait("Category", Cats.Unit)][Fact]
    public async Task Cancel_MidSegment_Throws()
    {
        string dir = TempDir();
        try
        {
            var o = new FakeHlsOrigin();
            o.Routes["http://h/manifest.m3u8"] = _ => Text(Vod("http://h", 4));
            for (int i = 0; i < 4; i++)
            {
                int k = i;
                o.Routes[$"http://h/seg{k}.ts"] = req =>
                {
                    // Slow body so cancel lands mid-segment.
                    var content = new SlowContent(Payload(k, 256 * 1024));
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
                };
            }
            using var cts = new CancellationTokenSource(300);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RunDl(o, "http://h/manifest.m3u8", dir, "c.ts", cts.Token));
        }
        finally { TestFiles.DeleteDir(dir); }
    }

    [Trait("Category", Cats.Unit)][Fact]
    public async Task Retry_On_503_SingleSegment()
    {
        string dir = TempDir();
        try
        {
            var o = new FakeHlsOrigin();
            o.Routes["http://h/manifest.m3u8"] = _ => Text(Vod("http://h", 3));
            for (int i = 0; i < 3; i++)
            {
                int k = i;
                o.Routes[$"http://h/seg{k}.ts"] = _ =>
                {
                    int hits = o.Hits[$"http://h/seg{k}.ts"];
                    if (k == 1 && hits == 1)
                        return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Payload(k)) };
                };
            }
            string dest = await RunDl(o, "http://h/manifest.m3u8", dir);
            Assert.Equal(3 * 4096, new FileInfo(dest).Length);
        }
        finally { TestFiles.DeleteDir(dir); }
    }

    [Trait("Category", Cats.Unit)][Fact]
    public async Task DeadSegment_Get404_ThrowsFatal_After_Single_Attempt()
    {
        // Dead token link: probe HEAD succeeds (size known) but segment GET
        // 404s. Must throw HlsFatalHttpException with exactly ONE GET — a 404
        // will never succeed on retry, so retrying burns minutes per segment.
        string dir = TempDir();
        try
        {
            int getHits = 0;
            var o = new FakeHlsOrigin();
            o.Routes["http://h/manifest.m3u8"] = _ => Text(Vod("http://h", 1));
            o.Routes["http://h/seg0.ts"] = req =>
            {
                if (req.Method == HttpMethod.Head)
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Payload(0)) };
                getHits++;
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            };
            using var http = new HttpClient(o) { Timeout = TimeSpan.FromSeconds(60) };
            string dest = Path.Combine(dir, "out.ts");
            var ex = await Assert.ThrowsAsync<HlsDownloader.HlsFatalHttpException>(() =>
                HlsDownloader.DownloadAsync(http, "http://h/manifest.m3u8", null, dest, CancellationToken.None,
                    _ => { }, _ => { }, (_, _) => Task.CompletedTask));
            Assert.Equal(HttpStatusCode.NotFound, ex.StatusCode);
            Assert.Equal(1, getHits);
        }
        finally { TestFiles.DeleteDir(dir); }
    }

    [Trait("Category", Cats.Unit)][Fact]
    public async Task DeadPlaylist_404_FailsFast()
    {
        string dir = TempDir();
        try
        {
            var o = new FakeHlsOrigin();
            o.Routes["http://h/manifest.m3u8"] = _ => new HttpResponseMessage(HttpStatusCode.NotFound);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            await Assert.ThrowsAsync<HlsDownloader.HlsFatalHttpException>(() => RunDl(o, "http://h/manifest.m3u8", dir));
            sw.Stop();
            // No retry delays: failover in well under the old ~5s retry burn.
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10), $"took {sw.Elapsed}");
        }
        finally { TestFiles.DeleteDir(dir); }
    }

    [Trait("Category", Cats.Unit)][Fact]
    public async Task ProbePhase_Reports_Progress()
    {
        string dir = TempDir();
        try
        {
            var o = new FakeHlsOrigin();
            o.Routes["http://h/manifest.m3u8"] = _ => Text(Vod("http://h", 2));
            for (int i = 0; i < 2; i++)
            {
                int k = i;
                o.Routes[$"http://h/seg{k}.ts"] = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Payload(k)) };
            }
            var phases = new List<string>();
            using var http = new HttpClient(o) { Timeout = TimeSpan.FromSeconds(60) };
            string dest = Path.Combine(dir, "out.ts");
            await HlsDownloader.DownloadAsync(http, "http://h/manifest.m3u8", null, dest, CancellationToken.None,
                _ => { }, _ => { }, (_, _) => Task.CompletedTask, null, phases.Add);
            Assert.Contains(phases, p => p.Contains("Probing"));
            Assert.Equal(2 * 4096, new FileInfo(dest).Length);
        }
        finally { TestFiles.DeleteDir(dir); }
    }

    [Trait("Category", Cats.Unit)][Fact]
    public async Task Master_Picks_HighestBandwidth()
    {
        using var http = new HttpClient(new FakeMasterOrigin()) { Timeout = TimeSpan.FromSeconds(30) };
        var variants = await HlsDownloader.ListVariantsAsync(http, "http://h/master.m3u8", null, null, CancellationToken.None);
        Assert.NotEmpty(variants);
        Assert.True(variants[0].Bandwidth >= variants[^1].Bandwidth);
    }

    private sealed class FakeMasterOrigin : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            string m = "#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=800000,RESOLUTION=1280x720\nhttp://h/720p.m3u8\n#EXT-X-STREAM-INF:BANDWIDTH=2500000,RESOLUTION=1920x1080\nhttp://h/1080p.m3u8\n";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(m) });
        }
    }

    private sealed class SlowContent : HttpContent
    {
        private readonly byte[] _data;
        public SlowContent(byte[] data) => _data = data;
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            for (int off = 0; off < _data.Length; off += 4096)
            {
                await Task.Delay(50);
                await stream.WriteAsync(_data, off, Math.Min(4096, _data.Length - off));
            }
        }
        protected override bool TryComputeLength(out long length) { length = _data.Length; return true; }
    }

    [Trait("Category", Cats.Unit)][Fact]
    public void EstimateTotalBytes_MathAndGuards()
    {
        Assert.Equal(60_000_000, HlsDownloader.EstimateTotalBytes(8_000_000, 60));
        Assert.Equal(0, HlsDownloader.EstimateTotalBytes(0, 60));
        Assert.Equal(0, HlsDownloader.EstimateTotalBytes(8_000_000, 0));
        Assert.Equal(0, HlsDownloader.EstimateTotalBytes(-1, 60));
        Assert.Equal(0, HlsDownloader.EstimateTotalBytes(8_000_000, double.NaN));
    }

    [Trait("Category", Cats.Unit)][Fact]
    public async Task TryEstimateSize_Master_UsesBandwidth_NoSegmentProbes()
    {
        var o = new FakeHlsOrigin();
        o.Routes["http://h/master.m3u8"] = _ => Text(
            "#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=8000000,RESOLUTION=1920x1080\nhttp://h/1080p.m3u8\n");
        o.Routes["http://h/1080p.m3u8"] = _ => Text(Vod("http://h", 10));
        using var http = new HttpClient(o) { Timeout = TimeSpan.FromSeconds(30) };
        long est = await HlsDownloader.TryEstimateSizeAsync(http, "http://h/master.m3u8", null, null, CancellationToken.None);
        Assert.Equal(60_000_000, est); // 8 Mbps * 60s / 8
        Assert.DoesNotContain(o.Hits.Keys, u => u.Contains("seg"));
    }

    [Trait("Category", Cats.Unit)][Fact]
    public async Task TryEstimateSize_DirectMediaPlaylist_ExtrapolatesFirstSegments()
    {
        var o = new FakeHlsOrigin();
        o.Routes["http://h/index.m3u8"] = _ => Text(Vod("http://h", 10));
        for (int i = 0; i < 10; i++)
        {
            int k = i;
            // HEAD reports a length; the estimator must stop after a few.
            o.Routes[$"http://h/seg{k}.ts"] = req =>
            {
                if (req.Method != HttpMethod.Head)
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Payload(k, 1000 * (k + 1))) };
                var r = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Array.Empty<byte>()) };
                r.Content.Headers.ContentLength = 1000 * (k + 1);
                return r;
            };
        }
        using var http = new HttpClient(o) { Timeout = TimeSpan.FromSeconds(30) };
        long est = await HlsDownloader.TryEstimateSizeAsync(http, "http://h/index.m3u8", null, null, CancellationToken.None);
        // First 3: 6000 B / 18 s over 60 s total.
        Assert.Equal(20_000, est);
        Assert.True(o.Hits.Keys.Count(u => u.Contains("seg")) <= 3);
    }

    [Trait("Category", Cats.Unit)][Fact]
    public async Task TryEstimateSize_LivePlaylist_ReturnsZero()
    {
        var o = new FakeHlsOrigin();
        // Sliding-window live playlist: no EXT-X-ENDLIST.
        var sb = new StringBuilder("#EXTM3U\n#EXT-X-VERSION:3\n#EXT-X-TARGETDURATION:6\n#EXT-X-MEDIA-SEQUENCE:100\n");
        for (int i = 0; i < 4; i++) sb.Append($"#EXTINF:6.0,\nhttp://h/seg{i}.ts\n");
        o.Routes["http://h/live.m3u8"] = _ => Text(sb.ToString());
        using var http = new HttpClient(o) { Timeout = TimeSpan.FromSeconds(30) };
        Assert.Equal(0, await HlsDownloader.TryEstimateSizeAsync(http, "http://h/live.m3u8", null, null, CancellationToken.None));
    }

    [Trait("Category", Cats.Unit)][Fact]
    public async Task TryEstimateSize_DeadLink_ReturnsZero()
    {
        var o = new FakeHlsOrigin();
        o.Routes["http://h/gone.m3u8"] = _ => new HttpResponseMessage(HttpStatusCode.Forbidden);
        using var http = new HttpClient(o) { Timeout = TimeSpan.FromSeconds(30) };
        Assert.Equal(0, await HlsDownloader.TryEstimateSizeAsync(http, "http://h/gone.m3u8", null, null, CancellationToken.None));
    }

    [Trait("Category", Cats.Unit)][Fact]
    public async Task DownloadAsync_FallsBackToEstimate_WhenSegmentsUnmeasurable()
    {
        string dir = TempDir();
        try
        {
            var o = new FakeHlsOrigin();
            o.Routes["http://h/master.m3u8"] = _ => Text(
                "#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=8000000,RESOLUTION=1920x1080\nhttp://h/1080p.m3u8\n");
            o.Routes["http://h/1080p.m3u8"] = _ => Text(Vod("http://h", 4));
            for (int i = 0; i < 4; i++)
            {
                int k = i;
                o.Routes[$"http://h/seg{k}.ts"] = req =>
                {
                    // CDN rejects HEAD and answers range probes length-less,
                    // but full downloads succeed.
                    if (req.Method == HttpMethod.Head)
                        return new HttpResponseMessage(HttpStatusCode.MethodNotAllowed);
                    var body = new ByteArrayContent(Payload(k));
                    var r = new HttpResponseMessage(HttpStatusCode.OK) { Content = body };
                    r.Content.Headers.ContentLength = null;
                    return r;
                };
            }
            using var http = new HttpClient(o) { Timeout = TimeSpan.FromSeconds(60) };
            string dest = Path.Combine(dir, "out.ts");
            long reportedTotal = -1;
            bool? estimated = null;
            await HlsDownloader.DownloadAsync(http, "http://h/master.m3u8", null, dest, CancellationToken.None,
                _ => { }, t => reportedTotal = t, (_, _) => Task.CompletedTask, null, null, e => estimated = e);
            Assert.Equal(24_000_000, reportedTotal); // 8 Mbps * 24 s / 8
            Assert.True(estimated);
            Assert.Equal(4 * 4096, new FileInfo(dest).Length);
        }
        finally { TestFiles.DeleteDir(dir); }
    }
}
