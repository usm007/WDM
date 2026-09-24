using System.Net;
using System.Net.Http.Headers;
using WDM.Services.Chunking;

namespace WDM.Tests.TestInfrastructure;

/// <summary>Category traits: `dotnet test --filter Category=Unit` etc.</summary>
public static class Cats
{
    public const string Unit = "Unit";
    public const string Integration = "Integration";
    public const string RangeEngine = "RangeEngine";
    public const string Security = "Security";
    public const string Stress = "Stress";
    public const string Performance = "Performance";
    public const string Browser = "Browser";
    public const string Media = "Media";
    public const string E2E = "E2E";
    public const string Regression = "Regression";
    public const string Network = "Network";
    public const string YtDlp = "YtDlp";
}

/// <summary>Deterministic byte factory: seed => reproducible content.</summary>
public static class TestFiles
{
    public static byte[] Make(int size, int seed = 42)
    {
        var rnd = new Random(seed);
        var data = new byte[size];
        rnd.NextBytes(data);
        return data;
    }

    public static string NewTempDir(string prefix = "wdm-test")
    {
        string dir = Path.Combine(Path.GetTempPath(), prefix, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static void DeleteDir(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
    }
}

/// <summary>Failure to inject on a programmable origin request.</summary>
public enum FaultKind
{
    None,
    ResetAfterBytes,   // IOException mid-body
    ShortRead,         // clean EOF at half length (headers promise full)
    WrongContentRange, // 206 with lying Content-Range
    WrongLength,       // Content-Length != body length
    ZeroByte,          // 206 with empty body
    Delayed,           // latency before headers
    Status,            // fixed status code instead of 206
}

public sealed class RecordedRequest
{
    public long From;
    public long To;
    public int Status;
    public long BytesSent;
    public DateTimeOffset At = DateTimeOffset.UtcNow;
    public string? RangeHeader;
}

/// <summary>
/// Programmable fake HTTP range origin (HttpMessageHandler — offline, deterministic).
/// Serves 200/206/304/403/404/408/429/500/502/503/504 + body faults, records
/// request count / ranges / headers / timestamps / status / bytes for scheduler assertions.
/// </summary>
public sealed class FakeProgrammableOrigin : HttpMessageHandler
{
    private readonly object _lock = new();
    private readonly Dictionary<string, byte[]> _objects = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _etags = new(StringComparer.OrdinalIgnoreCase);
    public Func<HttpRequestMessage, int, FaultKind>? FaultPicker;
    public Func<HttpRequestMessage, int, HttpStatusCode?>? StatusOverride;
    public TimeSpan ArtificialDelay;
    public readonly List<RecordedRequest> Requests = new();

    public void AddObject(string url, byte[] content, string? etag = "\"v1\"")
    {
        _objects[new Uri(url).AbsoluteUri] = content;
        if (etag is not null) _etags[new Uri(url).AbsoluteUri] = etag;
    }

    public int RequestCount { get { lock (_lock) return Requests.Count; } }
    public long TotalBytesSent { get { lock (_lock) return Requests.Sum(r => r.BytesSent); } }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        int index;
        lock (_lock) index = Requests.Count;
        string key = request.RequestUri!.AbsoluteUri;
        if (!_objects.TryGetValue(key, out var content) && !_objects.TryGetValue(request.RequestUri.Host, out content))
            return new HttpResponseMessage(HttpStatusCode.NotFound) { RequestMessage = request };

        var range = request.Headers.Range?.Ranges.FirstOrDefault();
        long from = range?.From ?? 0;
        long to = range?.To ?? (content.Length - 1);
        // Clamp display values; validation below decides 416 vs serve.
        var rec = new RecordedRequest { From = from, To = to, RangeHeader = request.Headers.Range?.ToString() };

        if (StatusOverride?.Invoke(request, index) is HttpStatusCode forced)
        {
            rec.Status = (int)forced;
            lock (_lock) Requests.Add(rec);
            return new HttpResponseMessage(forced) { RequestMessage = request };
        }

        if (from < 0 || to >= content.Length || from > to)
        {
            rec.Status = 416;
            lock (_lock) Requests.Add(rec);
            return new HttpResponseMessage(HttpStatusCode.RequestedRangeNotSatisfiable) { RequestMessage = request };
        }

        var fault = FaultPicker?.Invoke(request, index) ?? FaultKind.None;
        if (ArtificialDelay > TimeSpan.Zero || fault == FaultKind.Delayed)
            await Task.Delay(ArtificialDelay > TimeSpan.Zero ? ArtificialDelay : TimeSpan.FromMilliseconds(150), ct);

        int len = (int)(to - from + 1);
        var slice = new byte[len];
        Array.Copy(content, from, slice, 0, len);
        HttpResponseMessage resp;
        switch (fault)
        {
            case FaultKind.ResetAfterBytes:
                resp = new HttpResponseMessage(HttpStatusCode.PartialContent)
                {
                    Content = new StreamContent(new FaultStream(slice, len / 2)),
                    RequestMessage = request,
                };
                break;
            case FaultKind.ShortRead:
                var half = new byte[len / 2];
                Array.Copy(slice, half, half.Length);
                resp = new HttpResponseMessage(HttpStatusCode.PartialContent)
                {
                    Content = new ByteArrayContent(half),
                    RequestMessage = request,
                };
                break;
            case FaultKind.WrongContentRange:
                resp = new HttpResponseMessage(HttpStatusCode.PartialContent)
                {
                    Content = new ByteArrayContent(slice),
                    RequestMessage = request,
                };
                resp.Content.Headers.ContentRange = new ContentRangeHeaderValue(from + 1, to + 1, content.Length);
                rec.Status = 206;
                rec.BytesSent = len;
                lock (_lock) Requests.Add(rec);
                return resp;
            case FaultKind.WrongLength:
                resp = new HttpResponseMessage(HttpStatusCode.PartialContent)
                {
                    Content = new ByteArrayContent(slice),
                    RequestMessage = request,
                };
                resp.Content.Headers.ContentLength = len + 100;
                resp.Content.Headers.ContentRange = new ContentRangeHeaderValue(from, to, content.Length);
                rec.Status = 206;
                rec.BytesSent = len;
                lock (_lock) Requests.Add(rec);
                return resp;
            case FaultKind.ZeroByte:
                resp = new HttpResponseMessage(HttpStatusCode.PartialContent)
                {
                    Content = new ByteArrayContent(Array.Empty<byte>()),
                    RequestMessage = request,
                };
                resp.Content.Headers.ContentRange = new ContentRangeHeaderValue(from, to, content.Length);
                rec.Status = 206;
                lock (_lock) Requests.Add(rec);
                return resp;
            case FaultKind.Status:
                resp = new HttpResponseMessage(HttpStatusCode.InternalServerError) { RequestMessage = request };
                rec.Status = 500;
                lock (_lock) Requests.Add(rec);
                return resp;
            default:
                resp = new HttpResponseMessage(range is null ? HttpStatusCode.OK : HttpStatusCode.PartialContent)
                {
                    Content = new ByteArrayContent(slice),
                    RequestMessage = request,
                };
                break;
        }
        resp.Content.Headers.ContentLength = resp.Content is ByteArrayContent ? ((ByteArrayContent)resp.Content).Headers.ContentLength : len;
        if (resp.StatusCode == HttpStatusCode.PartialContent && resp.Content.Headers.ContentRange is null)
            resp.Content.Headers.ContentRange = new ContentRangeHeaderValue(from, to, content.Length);
        if (_etags.TryGetValue(key, out var etag))
            resp.Headers.ETag = new EntityTagHeaderValue(etag);
        rec.Status = (int)resp.StatusCode;
        rec.BytesSent = fault is FaultKind.ShortRead ? len / 2 : len;
        lock (_lock) Requests.Add(rec);
        await Task.Yield();
        return resp;
    }

    private sealed class FaultStream : Stream
    {
        private readonly byte[] _data;
        private readonly long _failAfter;
        private long _pos;
        public FaultStream(byte[] data, long failAfter) { _data = data; _failAfter = failAfter; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _data.Length;
        public override long Position { get => _pos; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_pos >= _failAfter) throw new IOException("Simulated connection reset.");
            int n = (int)Math.Min(count, Math.Min(_data.Length - _pos, _failAfter - _pos));
            if (n <= 0) return 0;
            Array.Copy(_data, _pos, buffer, offset, n);
            _pos += n;
            return n;
        }
        public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException();
        public override void SetLength(long v) => throw new NotSupportedException();
        public override void Write(byte[] b, int o, int c) => throw new NotSupportedException();
    }
}

/// <summary>One-line diagnostics dump for range-engine failures (no binary data).</summary>
public static class TestDiagnostics
{
    public static string Dump(RangeScheduler sched, CompletionMap? map, FakeProgrammableOrigin? origin, long totalBytes)
    {
        var active = sched.ActiveSnapshot();
        return $"total={totalBytes} completed={map?.CompletedBytes} blocks={map?.CompletedBlockCount}/{map?.BlockCount} " +
               $"active={active.Count} pending={sched.PendingCount} " +
               $"leases=[{string.Join(",", active.Select(l => $"{l.Start}-{l.EndExclusive}@{l.CommittedOffset}g{l.Generation}"))}] " +
               $"requests={origin?.RequestCount} bytesSent={origin?.TotalBytesSent}";
    }
}

/// <summary>Shared engine driver: real AdaptiveRangeEngine over a fake origin.</summary>
public static class EngineDriver
{
    public static async Task<AdaptiveRangeEngine> RunAsync(
        FakeProgrammableOrigin origin, byte[] content, string dir, string name,
        List<string>? urls = null, string? etag = "\"v1\"", int workers = 4,
        int maxRetries = 3, CancellationToken ct = default, Action<long>? onBytes = null)
    {
        OriginController.ResetForTests();
        var engine = new AdaptiveRangeEngine();
        using var http = new HttpClient(origin) { Timeout = TimeSpan.FromSeconds(60) };
        string dest = Path.Combine(dir, name);
        urls ??= new List<string> { "http://primary/file.bin" };
        await engine.RunAsync(content.Length, dest, dest + ".wdmstate", urls, etag, null,
            workers, maxRetries, null, http,
            (method, range, url) =>
            {
                var req = new HttpRequestMessage(method, url);
                if (range is not null) req.Headers.Range = range;
                return req;
            },
            (resp, url) => null,
            (bytes, tok) => Task.CompletedTask,
            bytes => onBytes?.Invoke(bytes),
            baseline => { }, ct);
        return engine;
    }

    /// <summary>Runs with a hard timeout; dumps scheduler state instead of hanging.</summary>
    public static async Task<T> WithTimeout<T>(Task<T> task, TimeSpan timeout, Func<string> dump)
    {
        if (await Task.WhenAny(task, Task.Delay(timeout)) == task)
            return await task;
        throw new TimeoutException("Test timed out. State: " + dump());
    }
}
