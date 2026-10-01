// HlsSegmentResumeTests — interrupted HLS downloads continue at the first
// missing segment instead of restarting (IDM keyframe-record equivalent,
// coarse: segments start on keyframes). Deterministic: pre-seeded resume
// dirs, method-aware fake origin, exact GET-hit assertions (HEAD re-probes
// are cheap and always re-run).
using System.Net;
using System.Text;
using WDM.Services;
using WDM.Tests.TestInfrastructure;
using Xunit.Abstractions;

namespace WDM.Tests;

public sealed class HlsSegmentResumeTests
{
    private readonly ITestOutputHelper _out;
    public HlsSegmentResumeTests(ITestOutputHelper o) { _out = o; }

    private sealed class MethodAwareOrigin : HttpMessageHandler
    {
        public readonly Dictionary<string, byte[]> Objects = new(StringComparer.OrdinalIgnoreCase);
        private readonly object _lock = new();
        public readonly Dictionary<string, int> GetHits = new(StringComparer.OrdinalIgnoreCase);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            string url = request.RequestUri!.AbsoluteUri;
            if (!Objects.TryGetValue(url, out var content))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound) { RequestMessage = request });
            if (request.Method == HttpMethod.Head)
            {
                var h = new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request };
                h.Content = new ByteArrayContent(Array.Empty<byte>());
                h.Content.Headers.ContentLength = content.Length;
                return Task.FromResult(h);
            }
            lock (_lock) GetHits[url] = GetHits.TryGetValue(url, out int n) ? n + 1 : 1;
            var r = new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request };
            r.Content = new ByteArrayContent(content);
            r.Content.Headers.ContentLength = content.Length;
            return Task.FromResult(r);
        }
    }

    private static string Vod(string baseUrl, int n)
    {
        var sb = new StringBuilder("#EXTM3U\n#EXT-X-VERSION:3\n#EXT-X-TARGETDURATION:6\n");
        for (int i = 0; i < n; i++) sb.Append($"#EXTINF:6.0,\n{baseUrl}/seg{i}.ts\n");
        return sb.Append("#EXT-X-ENDLIST\n").ToString();
    }

    private static byte[] Payload(int i, int size = 4096)
    {
        var b = new byte[size];
        for (int k = 0; k < size; k++) b[k] = (byte)((i * 31 + k) & 0xFF);
        return b;
    }

    private static (MethodAwareOrigin origin, string manifest, byte[][] segs) Setup(int n = 4)
    {
        var origin = new MethodAwareOrigin();
        const string baseUrl = "http://hls.test/vod";
        string manifest = baseUrl + "/list.m3u8";
        origin.Objects[manifest] = Encoding.UTF8.GetBytes(Vod(baseUrl, n));
        var segs = new byte[n][];
        for (int i = 0; i < n; i++)
        {
            segs[i] = Payload(i);
            origin.Objects[$"{baseUrl}/seg{i}.ts"] = segs[i];
        }
        return (origin, manifest, segs);
    }

    private static void SeedResumeDir(string dest, string manifest, byte[][] segs, int[] complete)
    {
        string dir = HlsDownloader.ResumeDirFor(dest);
        Directory.CreateDirectory(dir);
        var playlist = new HlsDownloader.Playlist();
        const string baseUrl = "http://hls.test/vod";
        for (int i = 0; i < segs.Length; i++)
            playlist.Segments.Add(new HlsDownloader.Segment { Uri = $"{baseUrl}/seg{i}.ts", Length = segs[i].Length });
        HlsDownloader.WriteResumeState(dir, manifest, playlist);
        foreach (int i in complete)
            File.WriteAllBytes(Path.Combine(dir, $"seg_{i:D6}.part"), segs[i]);
    }

    private static int Gets(MethodAwareOrigin origin, string url) =>
        origin.GetHits.TryGetValue(url, out int n) ? n : 0;

    private static async Task RunDl(MethodAwareOrigin origin, string manifest, string dest)
    {
        using var http = new HttpClient(origin) { Timeout = TimeSpan.FromSeconds(60) };
        await HlsDownloader.DownloadAsync(http, manifest, null, dest, CancellationToken.None,
            _ => { }, _ => { }, (_, _) => Task.CompletedTask);
    }

    // Interrupted run left seg0+seg1 complete: only seg2+seg3 are fetched.
    [Trait("Category", Cats.E2E)][Fact]
    public async Task InterruptedRun_Resumes_At_First_Missing_Segment()
    {
        var (origin, manifest, segs) = Setup();
        string dir = TestFiles.NewTempDir("wdm-hls-resume");
        try
        {
            string dest = Path.Combine(dir, "out.ts");
            SeedResumeDir(dest, manifest, segs, new[] { 0, 1 });
            _out.WriteLine("resume dir: " + HlsDownloader.ResumeDirFor(dest));
            _out.WriteLine("pre files: " + string.Join(",", Directory.GetFiles(HlsDownloader.ResumeDirFor(dest)).Select(Path.GetFileName)));
            await RunDl(origin, manifest, dest);
            var expected = segs.SelectMany(s => s).ToArray();
            Assert.Equal(expected, await File.ReadAllBytesAsync(dest));
            const string baseUrl = "http://hls.test/vod";
            _out.WriteLine("GET hits: " + string.Join(";", origin.GetHits.Select(kv => kv.Key + "=" + kv.Value)));
            _out.WriteLine("post files: " + (Directory.Exists(HlsDownloader.ResumeDirFor(dest)) ?
                string.Join(",", Directory.GetFiles(HlsDownloader.ResumeDirFor(dest)).Select(Path.GetFileName)) : "<gone>"));
            Assert.Equal(0, Gets(origin, $"{baseUrl}/seg0.ts"));
            Assert.Equal(0, Gets(origin, $"{baseUrl}/seg1.ts"));
            Assert.Equal(1, Gets(origin, $"{baseUrl}/seg2.ts"));
            Assert.Equal(1, Gets(origin, $"{baseUrl}/seg3.ts"));
            // Success cleans up the resume dir.
            Assert.False(Directory.Exists(HlsDownloader.ResumeDirFor(dest)));
        }
        finally { TestFiles.DeleteDir(dir); }
    }

    // Same folder+name, different stream: identity mismatch forces a full
    // re-download (stale segments can never poison the concat).
    [Trait("Category", Cats.E2E)][Fact]
    public async Task ChangedStream_Restarts_Full_Download()
    {
        var (origin, manifest, segs) = Setup();
        string dir = TestFiles.NewTempDir("wdm-hls-resume");
        try
        {
            string dest = Path.Combine(dir, "out.ts");
            SeedResumeDir(dest, "http://hls.test/other/list.m3u8", segs, new[] { 0, 1, 2, 3 });
            await RunDl(origin, manifest, dest);
            var expected = segs.SelectMany(s => s).ToArray();
            Assert.Equal(expected, await File.ReadAllBytesAsync(dest));
            const string baseUrl = "http://hls.test/vod";
            for (int i = 0; i < 4; i++)
                Assert.Equal(1, Gets(origin, $"{baseUrl}/seg{i}.ts"));
        }
        finally { TestFiles.DeleteDir(dir); }
    }
}
