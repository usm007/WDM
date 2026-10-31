using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Redm.Core;

namespace WDM.Services.Chunking;

/// <summary>A range transfer could not be completed (the source refused the
/// range, or every resume attempt failed). Carries the underlying cause so the
/// UI can produce a readable message.</summary>
internal sealed class RangeFailedException : Exception
{
    public RangeFailedException(string message) : base(message) { }
    public RangeFailedException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>A local write failed (disk full, permissions, device error). This is
/// never treated as server pressure: the origin budget must not be reduced for
/// a local-disk problem.</summary>
internal sealed class DiskWriteException : IOException
{
    public DiskWriteException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>The source stopped honouring byte ranges mid-transfer: a ranged
/// request came back as a plain 200 with the whole entity. The caller falls
/// back to a single-stream transfer instead of failing the whole download
/// (IDM does the same when a server drops range support).</summary>
internal sealed class RangeNotSupportedException : IOException
{
    public RangeNotSupportedException(string url)
        : base($"source {url} stopped honouring byte ranges") { }
}

/// <summary>
/// One HTTP range download executed by the IDM core (Redm.Core): the segment
/// plan comes from <see cref="Redm.Core.Engine.Plan"/>, resume is part-file based
/// and retries follow the IDM transfer rules (<see cref="TransferEngine.MaxAttempts"/>,
/// <see cref="TransferEngine.RetryDelay"/>, <see cref="TransferEngine.IsTransient"/>).
/// The host supplies request construction, Cloudflare classification, speed
/// throttling and progress reporting.
///
/// Reliability contract (restored from the adaptive scheduler):
///  * every attempt takes a slot from <see cref="OriginController"/>;
///  * a 206 must carry the exact requested Content-Range and the same object
///    identity (ETag / Last-Modified / total length) as the primary;
///  * when a part is resumed through a different mirror, the last 64 KiB of
///    durable bytes are byte-compared before that mirror may append;
///  * a mirror that lies is demoted for the rest of the download instead of
///    being tried forever, and a corrupt part is never silently assembled.
/// </summary>
public sealed class IdmRangeEngine
{
    /// <summary>IDM opens at most this many connections per download.</summary>
    public const int MaxConnections = 8;

    private const int BufferSize = 64 * 1024;

    /// <summary>Durable tail re-fetched from a different mirror before resume.</summary>
    private const int OverlapVerifyBytes = 64 * 1024;

    private readonly object _gate = new();
    private readonly List<Segment> _plan = new();
    private readonly List<long> _written = new();

    /// <summary>Bytes already on disk when this run started (resume baseline).</summary>
    public long CompletedBytesBaseline { get; private set; }

    /// <summary>Per-segment completion fraction, for the host's progress map.</summary>
    public double[] ProgressFractions()
    {
        lock (_gate)
        {
            if (_plan.Count == 0) return Array.Empty<double>();
            var fractions = new double[_plan.Count];
            for (int i = 0; i < _plan.Count; i++)
            {
                long need = _plan[i].End - _plan[i].Start + 1;
                fractions[i] = need <= 0 ? 1.0 : Math.Min(1.0, (double)_written[i] / need);
            }
            return fractions;
        }
    }

    /// <summary>Resume records for the host's persisted task snapshot.</summary>
    public List<SegmentRecord>? SnapshotSegments()
    {
        lock (_gate)
        {
            if (_plan.Count == 0) return null;
            var records = new List<SegmentRecord>(_plan.Count);
            for (int i = 0; i < _plan.Count; i++)
            {
                long need = _plan[i].End - _plan[i].Start + 1;
                records.Add(new SegmentRecord
                {
                    Index = _plan[i].Index,
                    Start = _plan[i].Start,
                    End = _plan[i].End,
                    Done = _written[i] >= need,
                });
            }
            return records;
        }
    }

    public Task RunAsync(
        long totalBytes,
        string destPath,
        string statePath,
        IReadOnlyList<string> urls,
        string? etag,
        string? lastModified,
        int initialWorkers,
        int maxRetries,
        List<SegmentRecord>? legacySegments,
        HttpClient http,
        Func<HttpMethod, RangeHeaderValue?, string, HttpRequestMessage> buildRequest,
        Func<HttpResponseMessage, string, Exception?> classifyCloudflare,
        Func<int, CancellationToken, Task> throttleAsync,
        Action<long> addBytes,
        Action<long> setBaseline,
        CancellationToken sessionToken) =>
        RunAsync(totalBytes, destPath, statePath, urls, etag, lastModified, initialWorkers, maxRetries,
            legacySegments, () => http, buildRequest, classifyCloudflare, throttleAsync, addBytes, setBaseline, sessionToken);

    public async Task RunAsync(
        long totalBytes,
        string destPath,
        string statePath,
        IReadOnlyList<string> urls,
        string? etag,
        string? lastModified,
        int initialWorkers,
        int maxRetries,
        List<SegmentRecord>? legacySegments,
        Func<HttpClient> httpProvider,
        Func<HttpMethod, RangeHeaderValue?, string, HttpRequestMessage> buildRequest,
        Func<HttpResponseMessage, string, Exception?> classifyCloudflare,
        Func<int, CancellationToken, Task> throttleAsync,
        Action<long> addBytes,
        Action<long> setBaseline,
        CancellationToken sessionToken)
    {
        if (totalBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(totalBytes));

        // IDM segmentation: equal parts over at most eight connections.
        var plan = Engine.Plan(totalBytes, initialWorkers);
        var parts = new string[plan.Count];
        for (int i = 0; i < plan.Count; i++)
            parts[i] = destPath + ".part" + plan[i].Index;

        long baseline = 0;
        lock (_gate)
        {
            _plan.Clear();
            _plan.AddRange(plan);
            _written.Clear();
            for (int i = 0; i < plan.Count; i++)
            {
                long need = plan[i].End - plan[i].Start + 1;
                long have = PartLength(parts[i]);
                if (have > need)
                {
                    // Stale, over-long part (the source shrank): start it over.
                    TryDelete(parts[i]);
                    have = 0;
                }
                _written.Add(have);
                baseline += have;
            }
        }
        CompletedBytesBaseline = baseline;
        try { setBaseline(baseline); } catch { }

        var mirrors = urls.Where(u => !string.IsNullOrWhiteSpace(u))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (mirrors.Length == 0)
            throw new ArgumentException("at least one source URL is required", nameof(urls));

        int retries = maxRetries > 0 ? maxRetries : TransferEngine.MaxAttempts;
        var health = new MirrorHealth(mirrors);

        // Workers share a linked token so the first hard failure stops the
        // others: leaving them writing part files after the task is reported
        // Failed lets a later resume/delete race with those orphaned writes.
        using var runCts = CancellationTokenSource.CreateLinkedTokenSource(sessionToken);
        var workers = plan
            .Select(segment => RunSegmentAsync(segment, parts, health, totalBytes, etag, lastModified, retries,
                httpProvider, buildRequest, classifyCloudflare, throttleAsync, addBytes, runCts.Token))
            .ToArray();
        try
        {
            await Task.WhenAll(workers).ConfigureAwait(false);
        }
        catch
        {
            runCts.Cancel();
            try { await Task.WhenAll(workers).ConfigureAwait(false); } catch { }
            throw;
        }

        await MergeAsync(destPath, parts, totalBytes, sessionToken).ConfigureAwait(false);
        TryDelete(statePath);
    }

    private async Task RunSegmentAsync(
        Segment segment,
        string[] parts,
        MirrorHealth mirrors,
        long totalBytes,
        string? etag,
        string? lastModified,
        int retries,
        Func<HttpClient> httpProvider,
        Func<HttpMethod, RangeHeaderValue?, string, HttpRequestMessage> buildRequest,
        Func<HttpResponseMessage, string, Exception?> classifyCloudflare,
        Func<int, CancellationToken, Task> throttleAsync,
        Action<long> addBytes,
        CancellationToken ct)
    {
        string part = parts[segment.Index];
        long need = segment.End - segment.Start + 1;
        // IDM's retry rule: a reconnect is only a failure when the transfer is
        // not advancing. `consecutive` counts attempts since the last durable
        // byte; any progress resets it, so a link that keeps dropping after
        // delivering data is resumed for as long as it keeps making headway.
        int consecutive = 0;
        int requests = 0;
        Exception? lastError = null;
        string? lastWriter = null;
        // Highest part length already credited. A reconnect only counts as a
        // failure while the part is not growing.
        long durableBytes = -1;

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            long have = PartLength(part);
            if (have > durableBytes)
            {
                durableBytes = have;
                consecutive = 0;
            }
            if (have >= need)
            {
                MarkWritten(segment.Index, need);
                return;
            }
            if (have > need)
            {
                Discard(segment.Index, part, have, addBytes);
                have = 0;
                durableBytes = 0;
                lastWriter = null;
            }

            requests++;
            if (requests > MaxRequestsPerSegment)
                throw Failure(segment, requests, lastError, "too many reconnects");

            if (!mirrors.TryPick(out int mirrorIndex, out string url))
            {
                throw Failure(segment, requests, lastError, "no usable source remains");
            }

            // A part written by the previous process (or by another mirror) is
            // verified against the next mirror before it may append. The same
            // mirror that wrote the tail is trusted for a plain reconnect.
            if (have > 0 && !string.Equals(lastWriter, url, StringComparison.OrdinalIgnoreCase))
            {
                long? mismatch = await VerifyMirrorContentAsync(part, segment.Start, have, url, etag,
                        httpProvider, buildRequest, ct)
                    .ConfigureAwait(false);
                if (mismatch is long at)
                {
                    string origin = OriginController.KeyOf(url);
                    OriginController.ReportResult(origin, HttpOutcome.ContentMismatch);
                    OriginController.ReportIntegrityFault(origin, "content-mismatch");
                    mirrors.ReportBad(mirrorIndex);
                    lastError = new IOException(
                        $"source {url} served different bytes at offset {at} than the already-downloaded part");
                    if (++consecutive > retries)
                        throw Failure(segment, requests, lastError, lastError.Message);
                    await BackoffAsync(consecutive, ct).ConfigureAwait(false);
                    continue;
                }
            }

            string originKey = OriginController.KeyOf(url);
            bool acquired = false;
            HttpOutcome outcome = HttpOutcome.Success;
            bool reportOutcome = true;
            try
            {
                await OriginController.WaitForSlotAsync(originKey, ct).ConfigureAwait(false);
                acquired = true;

                using var request = buildRequest(HttpMethod.Get,
                    new RangeHeaderValue(segment.Start + have, segment.End), url);
                if (have > 0 && !string.IsNullOrEmpty(etag))
                    request.Headers.TryAddWithoutValidation("If-Range", etag);

                var http = httpProvider();
                using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
                    .ConfigureAwait(false);

                int status = (int)response.StatusCode;

                // Server pressure: keep the source but let the origin controller
                // decide how long to wait before the next attempt.
                if (status == 408 || status == 429 || status >= 500)
                {
                    outcome = status == 429 ? HttpOutcome.TooManyRequests : HttpOutcome.ServerError;
                    mirrors.ReportTransient(mirrorIndex);
                    lastError = new HttpRequestException($"server returned HTTP {status} from {url}");
                    if (++consecutive > TransportBudget(retries))
                        break;
                    continue;
                }

                if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
                {
                    outcome = HttpOutcome.RangeNotSatisfiable;
                    Discard(segment.Index, part, have, addBytes);
                    have = 0;
                    durableBytes = 0;
                    lastWriter = null;
                    lastError = new HttpRequestException($"source {url} rejected the requested range with HTTP 416");
                    if (++consecutive > retries)
                        break;
                    await BackoffAsync(consecutive, ct).ConfigureAwait(false);
                    continue;
                }

                if (response.StatusCode == HttpStatusCode.OK)
                {
                    if (have > 0 || segment.Start > 0)
                    {
                        // A ranged request answered with the whole entity: this
                        // source no longer supports ranges. Do not burn the
                        // retry budget on every segment - tell the host so the
                        // file can continue over one ordinary connection.
                        outcome = HttpOutcome.OkWithoutRange;
                        throw new RangeNotSupportedException(url);
                    }
                    // First byte of the first segment: a plain 200 is a valid
                    // full-body response. Copy only this segment's window.
                }
                else if (response.StatusCode != HttpStatusCode.PartialContent)
                {
                    outcome = HttpOutcome.ClientError;
                    mirrors.ReportTransient(mirrorIndex);
                    lastError = new HttpRequestException($"source {url} returned HTTP {status}");
                    if (++consecutive > retries)
                        break;
                    continue;
                }

                if (classifyCloudflare(response, url) is { } blocked)
                {
                    outcome = HttpOutcome.CloudflareBlocked;
                    mirrors.ReportBad(mirrorIndex);
                    lastError = blocked;
                    if (++consecutive > retries || !mirrors.HasUsable)
                        throw Failure(segment, requests, blocked, "Cloudflare challenge");
                    continue;
                }

                if (response.StatusCode == HttpStatusCode.PartialContent)
                {
                    long wantFrom = segment.Start + have;
                    var range = response.Content.Headers.ContentRange;
                    if (range?.From != wantFrom || range?.To != segment.End)
                    {
                        outcome = HttpOutcome.WrongRange;
                        OriginController.ReportIntegrityFault(originKey, "wrong-range");
                        mirrors.ReportBad(mirrorIndex);
                        lastError = new HttpRequestException(
                            $"source {url} returned range {range?.From}-{range?.To}, expected {wantFrom}-{segment.End}");
                        if (++consecutive > retries)
                            break;
                        await BackoffAsync(consecutive, ct).ConfigureAwait(false);
                        continue;
                    }

                    if (!mirrors.CheckIdentity(mirrorIndex, response, totalBytes, etag, lastModified))
                    {
                        outcome = HttpOutcome.WrongRange;
                        OriginController.ReportIntegrityFault(originKey, "identity-mismatch");
                        mirrors.ReportBad(mirrorIndex);
                        lastError = new HttpRequestException(
                            $"source {url} serves a different object than the primary (ETag/length mismatch)");
                        if (++consecutive > retries)
                            break;
                        await BackoffAsync(consecutive, ct).ConfigureAwait(false);
                        continue;
                    }
                }

                using var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                FileStream dest;
                try
                {
                    dest = new FileStream(part, FileMode.Append, FileAccess.Write,
                        FileShare.None, BufferSize, FileOptions.Asynchronous);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    throw new DiskWriteException(
                        $"could not open the part file for segment {segment.Index}", ex);
                }
                long written = have;
                await using (dest)
                {
                    var buffer = new byte[BufferSize];
                    int read;
                    while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                    {
                        int take = (int)Math.Min(read, need - written);
                        if (take <= 0) break;
                        try
                        {
                            await dest.WriteAsync(buffer.AsMemory(0, take), ct).ConfigureAwait(false);
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                        {
                            throw new DiskWriteException(
                                $"could not write {take} bytes to the part file for segment {segment.Index}", ex);
                        }
                        written += take;
                        MarkWritten(segment.Index, written);
                        addBytes(take);
                        await throttleAsync(take, ct).ConfigureAwait(false);
                    }

                    if (written > have)
                    {
                        lastWriter = url;
                        consecutive = 0; // durable progress: the retry budget restarts
                    }
                }

                if (written >= need)
                {
                    MarkWritten(segment.Index, need);
                    reportOutcome = false;
                    OriginController.ReportResult(originKey, HttpOutcome.Success);
                    return;
                }

                // Short body (the server closed early): keep the grown part and
                // resume on the next attempt. Progress already reset the budget.
                if (written > have)
                {
                    OriginController.ReportResult(originKey, HttpOutcome.Success);
                    consecutive = 0;
                }
                outcome = HttpOutcome.ShortRead;
                lastError = new IOException(
                    $"source {url} ended after {written - have} of {need - have} requested bytes");
                if (++consecutive > TransportBudget(retries))
                    break;
                await BackoffAsync(consecutive, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // Timed out after bytes landed? The host is still serving; only
                // a stall from a standing start is server pressure.
                lastError = new TimeoutException($"source {url} timed out");
                if (GrewSince(part, have))
                {
                    outcome = HttpOutcome.Success;
                    consecutive = 0;
                }
                else
                {
                    outcome = HttpOutcome.Timeout;
                    mirrors.ReportTransient(mirrorIndex);
                    if (++consecutive > TransportBudget(retries))
                        break;
                }
                await BackoffAsync(consecutive, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (DiskWriteException ex)
            {
                outcome = HttpOutcome.DiskFatal;
                lastError = ex;
                throw;
            }
            catch (RangeNotSupportedException)
            {
                throw;
            }
            catch (HttpRequestException ex)
            {
                lastError = ex;
                if (GrewSince(part, have))
                {
                    outcome = HttpOutcome.Success;
                    consecutive = 0;
                }
                else
                {
                    outcome = HttpOutcome.ConnectionReset;
                    mirrors.ReportTransient(mirrorIndex);
                    if (++consecutive > TransportBudget(retries))
                        break;
                }
                await BackoffAsync(consecutive, ct).ConfigureAwait(false);
            }
            catch (IOException ex)
            {
                lastError = ex;
                if (GrewSince(part, have))
                {
                    outcome = HttpOutcome.Success;
                    consecutive = 0;
                }
                else
                {
                    outcome = HttpOutcome.ConnectionReset;
                    mirrors.ReportTransient(mirrorIndex);
                    if (++consecutive > TransportBudget(retries))
                        break;
                }
                await BackoffAsync(consecutive, ct).ConfigureAwait(false);
            }
            finally
            {
                if (acquired)
                    OriginController.Release(originKey);
                if (reportOutcome && acquired)
                    OriginController.ReportResult(originKey, outcome);
            }
        }

        throw Failure(segment, requests, lastError, "retry budget exhausted");
    }

    /// <summary>Consecutive transport failures (no durable progress) a segment
    /// waits through. IDM keeps reconnecting for roughly half a minute to a
    /// minute before it reports a dead link.</summary>
    private const int MinTransportAttempts = 8;

    /// <summary>Hard ceiling on requests per segment, counting the ones that
    /// made progress. Guards against a source that dribbles one byte per
    /// response instead of letting it loop forever.</summary>
    private const int MaxRequestsPerSegment = 1000;

    private static int TransportBudget(int retries) => Math.Max(retries, MinTransportAttempts);

    /// <summary>True when the part grew while the failed attempt was running:
    /// the drop delivered data, so the mirror is serving and the reconnect must
    /// not be charged as server pressure.</summary>
    private static bool GrewSince(string part, long before)
    {
        try { return PartLength(part) > before; }
        catch { return false; }
    }

    /// <summary>Throws away a part that can no longer be used and retracts the
    /// bytes it contributed, so progress always mirrors durable data (a
    /// discarded part used to inflate DownloadedBytes past the file size).</summary>
    private void Discard(int index, string part, long have, Action<long> addBytes)
    {
        TryDelete(part);
        MarkWritten(index, 0);
        if (have <= 0)
            return;
        try { addBytes(-have); } catch { }
    }

    /// <summary>Jittered exponential wait between reconnects (IDM waits between
    /// attempts instead of hammering the source).</summary>
    private static async Task BackoffAsync(int attempt, CancellationToken ct)
    {
        int cap = (int)Math.Min(8000, 400 * Math.Pow(2, Math.Min(attempt, 6)));
        int ms = Random.Shared.Next(cap / 2, cap + 1);
        await Task.Delay(ms, ct).ConfigureAwait(false);
    }

    /// <summary>Re-fetches the durable tail of a part from <paramref name="url"/>
    /// and reports the first differing offset. Network/range problems return
    /// null: they are handled by the normal request path, not treated as a
    /// content lie.</summary>
    private static async Task<long?> VerifyMirrorContentAsync(
        string part,
        long segmentStart,
        long have,
        string url,
        string? etag,
        Func<HttpClient> httpProvider,
        Func<HttpMethod, RangeHeaderValue?, string, HttpRequestMessage> buildRequest,
        CancellationToken ct)
    {
        int length = (int)Math.Min(OverlapVerifyBytes, have);
        if (length <= 0)
            return null;
        long localFrom = have - length;
        long globalFrom = segmentStart + localFrom;
        long globalTo = segmentStart + have - 1;

        byte[] disk = new byte[length];
        try
        {
            await using var file = new FileStream(part, FileMode.Open, FileAccess.Read,
                FileShare.Read, BufferSize, FileOptions.Asynchronous);
            file.Seek(localFrom, SeekOrigin.Begin);
            int offset = 0;
            while (offset < length)
            {
                int read = await file.ReadAsync(disk.AsMemory(offset, length - offset), ct).ConfigureAwait(false);
                if (read <= 0)
                    return null;
                offset += read;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        string origin = OriginController.KeyOf(url);
        await OriginController.WaitForSlotAsync(origin, ct).ConfigureAwait(false);
        HttpOutcome outcome = HttpOutcome.Success;
        try
        {
            using var request = buildRequest(HttpMethod.Get, new RangeHeaderValue(globalFrom, globalTo), url);
            if (!string.IsNullOrEmpty(etag))
                request.Headers.TryAddWithoutValidation("If-Range", etag);
            using var response = await httpProvider().SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.PartialContent)
            {
                outcome = response.StatusCode == HttpStatusCode.OK
                    ? HttpOutcome.OkWithoutRange
                    : HttpOutcome.ClientError;
                return null;
            }

            var range = response.Content.Headers.ContentRange;
            if (range?.From != globalFrom || range?.To != globalTo)
            {
                outcome = HttpOutcome.WrongRange;
                return null;
            }

            byte[] net = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            if (net.Length != length)
            {
                outcome = HttpOutcome.ShortRead;
                return null;
            }

            for (int i = 0; i < length; i++)
            {
                if (net[i] != disk[i])
                {
                    outcome = HttpOutcome.ContentMismatch;
                    return globalFrom + i;
                }
            }
            return null;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            outcome = HttpOutcome.Timeout;
            return null;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            outcome = HttpOutcome.ConnectionReset;
            return null;
        }
        finally
        {
            OriginController.Release(origin);
            OriginController.ReportResult(origin, outcome);
        }
    }

    private void MarkWritten(int index, long value)
    {
        lock (_gate) _written[index] = value;
    }

    private static RangeFailedException Failure(Segment segment, int attempts, Exception? cause, string reason)
    {
        string detail = cause is null || string.Equals(cause.Message, reason, StringComparison.Ordinal)
            ? reason
            : $"{reason}: {cause.Message}";
        return new($"segment {segment.Index} failed after {attempts} attempt(s): {detail}",
            cause ?? new IOException(reason));
    }

    private static async Task MergeAsync(string destPath, string[] parts, long totalBytes, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destPath) ?? ".");
        await using (var dest = new FileStream(destPath, FileMode.Create, FileAccess.Write,
            FileShare.None, BufferSize, FileOptions.Asynchronous))
        {
            foreach (string part in parts)
            {
                await using var source = new FileStream(part, FileMode.Open, FileAccess.Read,
                    FileShare.Read, BufferSize, FileOptions.Asynchronous);
                await source.CopyToAsync(dest, BufferSize, ct).ConfigureAwait(false);
            }
        }

        long length = new FileInfo(destPath).Length;
        if (length != totalBytes)
            throw new IOException($"assembled file is {length} bytes, expected {totalBytes}");

        foreach (string part in parts) TryDelete(part);
    }

    private static long PartLength(string path)
    {
        try { return File.Exists(path) ? new FileInfo(path).Length : 0; }
        catch { return 0; }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    /// <summary>Per-mirror health + identity gate shared by all segment workers.</summary>
    private sealed class MirrorHealth
    {
        private readonly object _lock = new();
        private readonly string[] _urls;
        private readonly bool[] _bad;
        private readonly int[] _errors;
        private int _cursor;

        public MirrorHealth(string[] urls)
        {
            _urls = urls;
            _bad = new bool[urls.Length];
            _errors = new int[urls.Length];
        }

        public bool HasUsable
        {
            get
            {
                lock (_lock)
                    return _urls.Length == 1 || Array.IndexOf(_bad, false) >= 0;
            }
        }

        public bool TryPick(out int index, out string url)
        {
            lock (_lock)
            {
                for (int i = 0; i < _urls.Length; i++)
                {
                    int candidate = (_cursor + i) % _urls.Length;
                    if (_bad[candidate]) continue;
                    _cursor = (candidate + 1) % _urls.Length;
                    index = candidate;
                    url = _urls[candidate];
                    return true;
                }
            }
            index = -1;
            url = "";
            return false;
        }

        public void ReportTransient(int index)
        {
            lock (_lock)
            {
                if (_urls.Length <= 1) return;
                if (++_errors[index] >= 6)
                    _bad[index] = true;
            }
        }

        public void ReportBad(int index)
        {
            lock (_lock)
            {
                if (_urls.Length > 1)
                    _bad[index] = true;
            }
        }

        /// <summary>Rejects a 206 that describes another object. Identity
        /// mismatches are treated like wrong ranges: the response body is never
        /// written.</summary>
        public bool CheckIdentity(int index, HttpResponseMessage response, long totalBytes,
            string? etag, string? lastModified)
        {
            string? gotEtag = response.Headers.ETag?.ToString();
            string? gotModified = response.Content.Headers.LastModified?.ToString("R");
            long? length = response.Content.Headers.ContentRange?.Length;

            bool bad = false;
            if (length.HasValue && length.Value > 0 && length.Value != totalBytes)
                bad = true;
            if (!string.IsNullOrWhiteSpace(etag) && !string.IsNullOrWhiteSpace(gotEtag) &&
                !string.Equals(etag, gotEtag, StringComparison.Ordinal))
                bad = true;
            bool etagAgrees = !string.IsNullOrWhiteSpace(etag) && !string.IsNullOrWhiteSpace(gotEtag) &&
                              string.Equals(etag, gotEtag, StringComparison.Ordinal);
            if (!etagAgrees && !string.IsNullOrWhiteSpace(lastModified) && !string.IsNullOrWhiteSpace(gotModified) &&
                !string.Equals(lastModified, gotModified, StringComparison.OrdinalIgnoreCase))
                bad = true;

            if (!bad) return true;
            ReportBad(index);
            return false;
        }
    }
}
