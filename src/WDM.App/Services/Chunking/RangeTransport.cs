using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Security.Authentication;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace WDM.Services.Chunking;

/// <summary>Thrown when the scheduler invalidated a lease under the worker (split).
/// Committed bytes are already safe; the worker must take a new lease, never abort.</summary>
internal sealed class LeaseInvalidatedException : Exception
{
    public LeaseInvalidatedException() : base("Lease invalidated by scheduler split.") { }
}

/// <summary>Thrown when a lease exhausts retries and mirrors. Fails the session.</summary>
internal sealed class RangeFailedException : Exception
{
    public RangeFailedException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>
/// Per-range mirror health with object-identity gating. Bytes from different URLs
/// are combined only after a 206 proves the same object (ETag/Last-Modified/length).
/// </summary>
public sealed class MirrorSelector
{
    private readonly object _lock = new();
    private readonly List<string> _urls;
    private readonly string? _etag;
    private readonly string? _lastModified;
    private readonly long _totalBytes;
    private readonly int[] _errors;
    private readonly bool[] _bad;
    private readonly bool[] _verified;
    private int _cursor;

    public MirrorSelector(IReadOnlyList<string> urls, string? etag, string? lastModified, long totalBytes)
    {
        _urls = new List<string>(urls);
        _etag = etag;
        _lastModified = lastModified;
        _totalBytes = totalBytes;
        _errors = new int[_urls.Count];
        _bad = new bool[_urls.Count];
        _verified = new bool[_urls.Count];
        if (_urls.Count == 0)
            throw new ArgumentException("At least one URL is required.", nameof(urls));
    }

    public int UrlCount => _urls.Count;

    public bool TryPick(out int index, out string url)
    {
        lock (_lock)
        {
            for (int i = 0; i < _urls.Count; i++)
            {
                int c = (_cursor + i) % _urls.Count;
                if (!_bad[c])
                {
                    _cursor = (c + 1) % _urls.Count;
                    index = c;
                    url = _urls[c];
                    return true;
                }
            }
        }
        index = -1;
        url = "";
        return false;
    }

    /// <summary>Validates a 206's identity headers against the primary object.</summary>
    public bool CheckResponse(int index, HttpResponseMessage response)
    {
        string? etag = response.Headers.ETag?.ToString();
        string? mod = response.Content.Headers.LastModified?.ToString("R");
        long? len = response.Content.Headers.ContentRange?.Length;
        lock (_lock)
        {
            if (len.HasValue && len.Value > 0 && len.Value != _totalBytes)
            {
                _bad[index] = true;
                return false;
            }
            if (!string.IsNullOrWhiteSpace(_etag) && !string.IsNullOrWhiteSpace(etag) &&
                !string.Equals(_etag, etag, StringComparison.Ordinal))
            {
                _bad[index] = true;
                return false;
            }
            bool etagAgrees = !string.IsNullOrWhiteSpace(_etag) && !string.IsNullOrWhiteSpace(etag) &&
                string.Equals(_etag, etag, StringComparison.Ordinal);
            if (!etagAgrees && !string.IsNullOrWhiteSpace(_lastModified) && !string.IsNullOrWhiteSpace(mod) &&
                !string.Equals(_lastModified, mod, StringComparison.OrdinalIgnoreCase))
            {
                _bad[index] = true;
                return false;
            }
            _verified[index] = true;
            _errors[index] = 0;
            return true;
        }
    }

    public void ReportTransient(int index)
    {
        lock (_lock)
        {
            if (_urls.Count > 1 && ++_errors[index] >= 6)
                _bad[index] = true; // persistently failing mirror steps aside
        }
    }

    public void ReportWrongObject(int index)
    {
        lock (_lock)
        {
            if (_urls.Count > 1)
                _bad[index] = true;
        }
    }
}

/// <summary>Dependencies the transport needs from the orchestrator (DownloadEngine).</summary>
public sealed class RangeTransportDeps
{
    private readonly HttpClient? _httpInstance;
    private readonly Func<HttpClient>? _httpProvider;

    public HttpClient Http
    {
        get => _httpProvider is not null ? _httpProvider() : _httpInstance!;
        init => _httpInstance = value;
    }

    public Func<HttpClient>? HttpProvider
    {
        get => _httpProvider;
        init => _httpProvider = value;
    }

    public required Func<HttpMethod, RangeHeaderValue?, string, HttpRequestMessage> BuildRequest { get; init; }
    public required Func<HttpResponseMessage, string, Exception?> ClassifyCloudflare { get; init; }
    public required Func<int, CancellationToken, Task> ThrottleAsync { get; init; }
    public required Action<long> AddBytes { get; init; }
    public required Action OnServerPressure { get; init; }
    public int MaxRetries { get; init; } = 3;
    public required SafeFileHandle FileHandle { get; init; }
    public long TotalBytes { get; init; }
}

/// <summary>
/// Single-range fetch with first-class partial retry: after any failure the next
/// request starts at the committed offset, never at the range start. Ownership and
/// durability flow through <see cref="RangeScheduler"/> + <see cref="CompletionMap"/>.
/// </summary>
public static class RangeTransport
{
    private const int BufferSize = 256 * 1024;

    public static async Task FetchRangeAsync(
        RangeLease lease, int generation,
        RangeScheduler scheduler, CompletionMap map, MirrorSelector mirrors,
        SchedulerController controller, DownloadTelemetry telemetry,
        RangeTransportDeps deps, CancellationToken ct)
    {
        long toInclusive = lease.EndExclusive - 1;
        long committed = lease.CommittedOffset;
        long net = lease.NetworkBytes, written = lease.WrittenBytes;
        double bps = lease.ThroughputBps;
        int urlCount = mirrors.UrlCount;
        int budget = Math.Max(1, urlCount * (deps.MaxRetries + 1));
        int attempts = 0;
        Exception? lastError = null;
        var rangeSw = System.Diagnostics.Stopwatch.StartNew();

        while (committed <= toInclusive)
        {
            ct.ThrowIfCancellationRequested();
            if (!scheduler.IsValid(lease.Id, generation))
                throw new LeaseInvalidatedException();

            if (!mirrors.TryPick(out int urlIndex, out string url))
                throw new RangeFailedException(
                    $"All mirrors failed for range {committed}-{toInclusive}. {lastError?.Message}",
                    lastError);

            // Fast-retry signal: bytes committed during this attempt prove the
            // server is healthy, so a mid-stream drop reconnects quickly while a
            // pre-byte failure still backs off exponentially (server pressure).
            long attemptStart = committed;

            string origin = OriginController.KeyOf(url);
            await OriginController.WaitForSlotAsync(origin, ct);
            var reqSw = System.Diagnostics.Stopwatch.StartNew();
            bool released = false;
            try
            {
                using var request = deps.BuildRequest(HttpMethod.Get, new RangeHeaderValue(committed, toInclusive), url);
                telemetry.AddRequest();
                using var response = await deps.Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
                reqSw.Stop();

                int code = (int)response.StatusCode;
                if (code == 408 || code == 429 || code >= 500)
                {
                    var outcome = code == 429 ? HttpOutcome.TooManyRequests : HttpOutcome.ServerError;
                    telemetry.RecordOutcome(outcome);
                    OriginController.ReportResult(origin, outcome);
                    deps.OnServerPressure();
                    mirrors.ReportTransient(urlIndex);
                    lastError = new HttpRequestException($"Server pressure {(HttpStatusCode)code} for range {committed}-{toInclusive}.");
                    attempts++;
                    telemetry.AddRetry();
                    if (attempts >= budget)
                        break;
                    await ServerBackoffAsync(response, attempts, ct);
                    continue;
                }

                if (response.StatusCode == HttpStatusCode.Forbidden)
                {
                    var cf = deps.ClassifyCloudflare(response, url);
                    if (cf is not null)
                    {
                        telemetry.RecordOutcome(HttpOutcome.CloudflareBlocked);
                        throw cf;
                    }
                }

                if (response.StatusCode == HttpStatusCode.OK)
                {
                    // Mirror ignored Range (or origin truly lacks range support):
                    // demote this mirror and try the next one instead of failing
                    // the whole download. Single-source servers still fail after
                    // the retry budget, via lastError below.
                    telemetry.RecordOutcome(HttpOutcome.OkWithoutRange);
                    mirrors.ReportTransient(urlIndex);
                    lastError = new InvalidOperationException(
                        $"Server ignored Range for {committed}-{toInclusive} (200 OK).");
                    attempts++;
                    telemetry.AddRetry();
                    if (attempts >= budget)
                        break;
                    await BackoffAsync(attempts, ct);
                    continue;
                }

                if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
                {
                    telemetry.RecordOutcome(HttpOutcome.RangeNotSatisfiable);
                    mirrors.ReportTransient(urlIndex);
                    lastError = new HttpRequestException($"Range not satisfiable: {committed}-{toInclusive}.");
                    attempts++;
                    telemetry.AddRetry();
                    if (attempts >= budget)
                        break;
                    await BackoffAsync(attempts, ct);
                    continue;
                }

                if (response.StatusCode != HttpStatusCode.PartialContent)
                {
                    telemetry.RecordOutcome(HttpOutcome.ClientError);
                    mirrors.ReportTransient(urlIndex);
                    lastError = new HttpRequestException($"Unexpected status {(int)response.StatusCode} for range {committed}-{toInclusive}.");
                    attempts++;
                    telemetry.AddRetry();
                    if (attempts >= budget)
                        break;
                    await BackoffAsync(attempts, ct);
                    continue;
                }

                var cr = response.Content.Headers.ContentRange;
                if (cr?.From != committed || cr?.To != toInclusive)
                {
                    telemetry.RecordOutcome(HttpOutcome.WrongRange);
                    mirrors.ReportTransient(urlIndex);
                    lastError = new HttpRequestException(
                        $"Server returned wrong range (asked {committed}-{toInclusive}, got {cr?.From}-{cr?.To}).");
                    attempts++;
                    telemetry.AddRetry();
                    if (attempts >= budget)
                        break;
                    await BackoffAsync(attempts, ct);
                    continue;
                }

                if (!mirrors.CheckResponse(urlIndex, response))
                {
                    telemetry.RecordOutcome(HttpOutcome.WrongRange);
                    lastError = new HttpRequestException($"Mirror serves a different object (identity mismatch): {url}");
                    attempts++;
                    telemetry.AddRetry();
                    if (attempts >= budget)
                        break;
                    await BackoffAsync(attempts, ct);
                    continue;
                }

                // Stream + write-through: every successful write advances the
                // committed offset before the next read, so a failure resumes
                // exactly where durable bytes end.
                await using var input = await response.Content.ReadAsStreamAsync(ct);
                var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
                try
                {
                    long fileOffset = committed;
                    long prevCommitted = committed;
                    int read;
                    while ((read = await input.ReadAsync(buffer.AsMemory(0, BufferSize), ct)) > 0)
                    {
                        await deps.ThrottleAsync(read, ct);
                        try
                        {
                            await RandomAccess.WriteAsync(deps.FileHandle, buffer.AsMemory(0, read), fileOffset, ct);
                        }
                        catch (Exception ex) when (DownloadEngine.IsFatalDiskError(ex))
                        {
                            telemetry.RecordOutcome(HttpOutcome.DiskFatal);
                            throw;
                        }
                        fileOffset += read;
                        committed += read;
                        net += read;
                        written += read;
                        telemetry.AddNetworkBytes(read);
                        telemetry.AddWrittenBytes(read);
                        telemetry.AddCommittedBytes(read);
                        deps.AddBytes(read);
                        map.MarkCommitted(prevCommitted, committed);
                        bps = (committed - lease.Start) / Math.Max(rangeSw.Elapsed.TotalSeconds, 0.001);
                        if (!scheduler.ReportProgress(lease.Id, generation, committed, net, written, bps))
                            throw new LeaseInvalidatedException();
                        ct.ThrowIfCancellationRequested();
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }

                OriginController.ReportResult(origin, HttpOutcome.Success);
                telemetry.AddThroughputSample(net, rangeSw.Elapsed);

                if (committed - 1 < toInclusive)
                {
                    // Short read: not complete, not corrupt — retry the remainder.
                    // Fast retry (no exponential backoff): the server just proved
                    // healthy by serving bytes; the connection merely ended early.
                    telemetry.RecordOutcome(HttpOutcome.ShortRead);
                    lastError = new HttpRequestException(
                        $"Short read: got {committed} of {toInclusive + 1} bytes for range start.");
                    attempts++;
                    telemetry.AddRetry();
                    if (attempts >= budget)
                        break;
                    await Task.Delay(Random.Shared.Next(50, 150), ct);
                    continue;
                }

                telemetry.AddRangeCompleted(rangeSw.Elapsed);
                controller.AddRangeDuration(rangeSw.Elapsed.TotalSeconds);
                controller.AddThroughputSample(written, rangeSw.Elapsed.TotalSeconds);
                return;
            }
            catch (LeaseInvalidatedException) { throw; }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) when (ex is DownloadEngine.CloudflareBlockedException || DownloadEngine.IsFatalDiskError(ex)) { throw; }
            catch (Exception ex) when (ex is InvalidOperationException && ex.Message.Contains("range downloads")) { throw; }
            catch (Exception ex)
            {
                reqSw.Stop();
                var outcome = ClassifyNetworkError(ex);
                telemetry.RecordOutcome(outcome);
                OriginController.ReportResult(origin, outcome);
                if (outcome is HttpOutcome.Timeout or HttpOutcome.ConnectionReset)
                    deps.OnServerPressure();
                mirrors.ReportTransient(urlIndex);
                lastError = ex;
                attempts++;
                telemetry.AddRetry();
                if (attempts >= budget)
                    break;
                if (committed > attemptStart)
                    await Task.Delay(Random.Shared.Next(50, 150), ct);
                else
                    await BackoffAsync(attempts, ct);
            }
            finally
            {
                if (!released)
                {
                    released = true;
                    OriginController.Release(origin);
                }
            }
        }

        throw new RangeFailedException(
            $"Range {lease.Start}-{lease.EndExclusive - 1} failed after {attempts} attempts. {lastError?.Message}",
            lastError);
    }

    internal static HttpOutcome ClassifyNetworkError(Exception ex)
    {
        if (ex is TimeoutException || ex is TaskCanceledException)
            return HttpOutcome.Timeout;
        if (ex is AuthenticationException)
            return HttpOutcome.TlsFailure;
        if (ex is HttpRequestException hre)
        {
            string m = hre.Message;
            if (m.Contains("SSL", StringComparison.OrdinalIgnoreCase) ||
                m.Contains("TLS", StringComparison.OrdinalIgnoreCase) ||
                m.Contains("certificate", StringComparison.OrdinalIgnoreCase))
                return HttpOutcome.TlsFailure;
            return HttpOutcome.ConnectionReset;
        }
        if (ex is IOException)
            return HttpOutcome.ConnectionReset;
        return HttpOutcome.ConnectionReset;
    }

    private static async Task BackoffAsync(int attempt, CancellationToken ct)
    {
        int cap = (int)Math.Min(8000, 500 * Math.Pow(2, attempt));
        int ms = Random.Shared.Next(cap / 2, cap + 1);
        await Task.Delay(ms, ct);
    }

    private static async Task ServerBackoffAsync(HttpResponseMessage response, int attempt, CancellationToken ct)
    {
        TimeSpan? retryAfter = DownloadEngine.GetRetryAfter(response);
        if (retryAfter is not null)
        {
            try { await Task.Delay(retryAfter.Value, ct); } catch (OperationCanceledException) { throw; }
            return;
        }
        await BackoffAsync(attempt, ct);
    }
}
