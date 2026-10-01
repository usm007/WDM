// ContentMismatchTests — a well-framed lie (right headers, wrong bytes) must
// never assemble silently: the disagreeing mirror is demoted, the region is
// re-queued from a survivor, and single-source lies fail loudly with the
// offending range attributed. Deterministic: hand-built leases against a
// two-faced fake origin (no scheduler timing involved).
using System.Net;
using System.Net.Http.Headers;
using Microsoft.Win32.SafeHandles;
using WDM.Services.Chunking;
using WDM.Tests.TestInfrastructure;
using Xunit.Abstractions;

namespace WDM.Tests;

public sealed class ContentMismatchTests
{
    private readonly ITestOutputHelper _out;
    public ContentMismatchTests(ITestOutputHelper o) { _out = o; }
    private const int Block = 1024 * 1024;

    private sealed class TwoFacedOrigin : HttpMessageHandler
    {
        public readonly byte[] Good;
        public readonly byte[] Evil;
        private readonly object _lock = new();
        public readonly List<string> Log = new();
        public TwoFacedOrigin(byte[] good, byte[] evil) { Good = good; Evil = evil; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            string url = request.RequestUri!.AbsoluteUri;
            lock (_lock) Log.Add(request.Method.Method + " " + url + " range=" + (request.Headers.Range?.ToString() ?? "-"));
            bool evil = request.RequestUri.Host.StartsWith("evil", StringComparison.OrdinalIgnoreCase);
            byte[] content = evil ? Evil : Good;
            var range = request.Headers.Range?.Ranges.FirstOrDefault();
            long from = range?.From ?? 0;
            long to = range?.To ?? (content.Length - 1);
            HttpResponseMessage resp;
            if (request.Method == HttpMethod.Head)
            {
                resp = new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request };
                resp.Content = new ByteArrayContent(Array.Empty<byte>());
                resp.Content.Headers.ContentLength = content.Length;
                return Task.FromResult(resp);
            }
            resp = new HttpResponseMessage(HttpStatusCode.PartialContent) { RequestMessage = request };
            int len = (int)(to - from + 1);
            var body = new byte[len];
            Array.Copy(content, from, body, 0, len);
            resp.Content = new ByteArrayContent(body);
            resp.Content.Headers.ContentLength = len;
            resp.Content.Headers.ContentRange = new ContentRangeHeaderValue(from, to, content.Length);
            resp.Headers.ETag = new EntityTagHeaderValue("\"v1\"");
            return Task.FromResult(resp);
        }
    }

    private static (string file, CompletionMap map, SafeFileHandle handle) SeedPrefix(byte[] goodPrefix)
    {
        string dir = TestFiles.NewTempDir("wdm-mismatch");
        string file = Path.Combine(dir, "f.bin");
        using (var fs = new FileStream(file, FileMode.Create, FileAccess.Write, FileShare.ReadWrite))
            fs.Write(goodPrefix, 0, goodPrefix.Length);
        var map = new CompletionMap(goodPrefix.Length + Block, CompletionMap.DefaultBlockSize);
        map.MarkCommitted(0, goodPrefix.Length);
        var handle = File.OpenHandle(file, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
        return (file, map, handle);
    }

    private static RangeTransportDeps Deps(TwoFacedOrigin origin, SafeFileHandle handle, long total) =>
        new()
        {
            Http = new HttpClient(origin),
            BuildRequest = (method, range, url) =>
            {
                var req = new HttpRequestMessage(method, url);
                if (range is not null) req.Headers.Range = range;
                return req;
            },
            ClassifyCloudflare = (resp, url) => null,
            ThrottleAsync = (bytes, tok) => Task.CompletedTask,
            AddBytes = _ => { },
            OnServerPressure = () => { },
            MaxRetries = 3,
            FileHandle = handle,
            TotalBytes = total,
        };

    private static (RangeScheduler scheduler, RangeLease lease) Lease(long start, long end)
    {
        var scheduler = new RangeScheduler();
        scheduler.Initialize(new[] { (start, end) });
        var lease = scheduler.TryLease(0, end - start);
        Assert.NotNull(lease);
        return (scheduler, lease!);
    }

    // Liar speaks first: its bytes lose to durable truth, region repaired
    // from the good mirror, output byte-exact, liar demoted.
    [Trait("Category", Cats.E2E)][Fact]
    public async Task LyingMirror_Demoted_RegionRepairedCorrect()
    {
        byte[] good = TestFiles.Make(2 * Block, seed: 11);
        byte[] evil = TestFiles.Make(2 * Block, seed: 99);
        var origin = new TwoFacedOrigin(good, evil);
        string goodUrl = "http://good.test/file.bin";
        string evilUrl = "http://evil.test/file.bin";
        // Durable prefix [0,1MiB) in good bytes — the anchor the liar disputes.
        byte[] prefix = new byte[Block];
        Array.Copy(good, 0, prefix, 0, Block);
        var (file, map, handle) = SeedPrefix(prefix);
        _out.WriteLine("durable: " + map.IsRangeDurable(Block - 65536, Block));
        using (handle)
        {
            var mirrors = new MirrorSelector(new[] { evilUrl, goodUrl }, "\"v1\"", null, 2L * Block);
            var (scheduler, lease) = Lease(Block, 2L * Block);
            var controller = new SchedulerController();
            var telemetry = new DownloadTelemetry();
            await RangeTransport.FetchRangeAsync(lease, 0, scheduler, map, mirrors,
                controller, telemetry, Deps(origin, handle, 2L * Block), CancellationToken.None);
            Assert.True(telemetry.Snapshot(2L * Block).ContentMismatches >= 1);
            // The overlap window was re-fetched (range starting below 1MiB).
            Assert.Contains(origin.Log, e => e.Contains("bytes=983040-1048575"));
            // Liar demoted: only the good mirror is pickable now.
            Assert.True(mirrors.TryPick(out _, out string pick));
            Assert.Equal(goodUrl, pick);
            Assert.True(map.IsComplete);
        }
        byte[] onDisk = await File.ReadAllBytesAsync(file);
        byte[] want = new byte[2 * Block];
        Array.Copy(good, 0, want, 0, 2 * Block);
        Assert.Equal(want, onDisk);
        TestFiles.DeleteDir(Path.GetDirectoryName(file)!);
    }

    [Trait("Category", Cats.Unit)][Fact]
    public void InvalidateRange_ClearsOnlyIntersectingBlocks()
    {
        var map = new CompletionMap(4L * Block, CompletionMap.DefaultBlockSize);
        map.MarkCommitted(0, 4L * Block);
        Assert.True(map.IsComplete);
        map.InvalidateRange(2L * Block - 65536, 2L * Block);
        Assert.True(map.IsBlockComplete(0));
        Assert.False(map.IsBlockComplete(1));
        Assert.True(map.IsBlockComplete(2));
        Assert.True(map.IsBlockComplete(3));
        Assert.False(map.IsComplete);
    }

    // Lone liar: no good source exists — fail loudly with attribution, never
    // present the lie as a complete download.
    [Trait("Category", Cats.E2E)][Fact]
    public async Task LoneLiar_FailsLoudly_NeverSilent()
    {
        byte[] good = TestFiles.Make(2 * Block, seed: 11);
        byte[] evil = TestFiles.Make(2 * Block, seed: 99);
        var origin = new TwoFacedOrigin(good, evil);
        string evilUrl = "http://evil.test/file.bin";
        byte[] prefix = new byte[Block];
        Array.Copy(good, 0, prefix, 0, Block);
        var (file, map, handle) = SeedPrefix(prefix);
        _out.WriteLine("durable: " + map.IsRangeDurable(Block - 65536, Block));
        _out.WriteLine("block0: " + map.IsBlockComplete(0) + " blocks: " + map.BlockCount);
        using (handle)
        {
            var mirrors = new MirrorSelector(new[] { evilUrl }, "\"v1\"", null, 2L * Block);
            var (scheduler, lease) = Lease(Block, 2L * Block);
            var controller = new SchedulerController();
            var telemetry = new DownloadTelemetry();
            Exception? caught = null;
            try
            {
                await RangeTransport.FetchRangeAsync(lease, 0, scheduler, map, mirrors,
                    controller, telemetry, Deps(origin, handle, 2L * Block), CancellationToken.None);
            }
            catch (Exception ex) { caught = ex; }
            _out.WriteLine("requests: " + string.Join(" | ", origin.Log));
            Assert.NotNull(caught);
            Assert.Contains("content mismatch", caught.Message, StringComparison.OrdinalIgnoreCase);
            Assert.False(map.IsComplete);
        }
        TestFiles.DeleteDir(Path.GetDirectoryName(file)!);
    }
}
