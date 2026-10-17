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

/// <summary>
/// One HTTP range download executed by the IDM core (Redm.Core): the segment
/// plan comes from <see cref="Redm.Core.Engine.Plan"/>, resume is part-file based
/// and retries follow the IDM transfer rules (<see cref="TransferEngine.MaxAttempts"/>,
/// <see cref="TransferEngine.RetryDelay"/>, <see cref="TransferEngine.IsTransient"/>).
/// The host supplies request construction, Cloudflare classification, speed
/// throttling and progress reporting.
/// </summary>
public sealed class IdmRangeEngine
{
    /// <summary>IDM opens at most this many connections per download.</summary>
    public const int MaxConnections = 8;

    private const int BufferSize = 64 * 1024;

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

        var workers = plan
            .Select((segment, index) => RunSegmentAsync(segment, index, parts, mirrors, etag, retries,
                httpProvider, buildRequest, classifyCloudflare, throttleAsync, addBytes, sessionToken))
            .ToArray();
        await Task.WhenAll(workers).ConfigureAwait(false);

        await MergeAsync(destPath, parts, totalBytes, sessionToken).ConfigureAwait(false);
        TryDelete(statePath);
    }

    private async Task RunSegmentAsync(
        Segment segment,
        int index,
        string[] parts,
        string[] mirrors,
        string? etag,
        int retries,
        Func<HttpClient> httpProvider,
        Func<HttpMethod, RangeHeaderValue?, string, HttpRequestMessage> buildRequest,
        Func<HttpResponseMessage, string, Exception?> classifyCloudflare,
        Func<int, CancellationToken, Task> throttleAsync,
        Action<long> addBytes,
        CancellationToken ct)
    {
        string part = parts[index];
        long need = segment.End - segment.Start + 1;
        int attempt = 0;

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            long have = PartLength(part);
            if (have >= need)
            {
                MarkWritten(index, need);
                return;
            }
            if (have > need)
            {
                TryDelete(part);
                have = 0;
                MarkWritten(index, 0);
            }

            attempt++;
            string url = mirrors[(index + attempt - 1) % mirrors.Length];
            try
            {
                await using var dest = new FileStream(part, FileMode.Append, FileAccess.Write,
                    FileShare.None, BufferSize, FileOptions.Asynchronous);
                using var request = buildRequest(HttpMethod.Get,
                    new RangeHeaderValue(segment.Start + have, segment.End), url);
                if (have > 0 && !string.IsNullOrEmpty(etag))
                    request.Headers.TryAddWithoutValidation("If-Range", etag);

                var http = httpProvider();
                using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
                    .ConfigureAwait(false);

                // Validator changed or the server ignored our range: the part is
                // stale, restart it clean (mirrors the IDM transfer rules).
                if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable ||
                    (have > 0 && response.StatusCode == HttpStatusCode.OK))
                {
                    TryDelete(part);
                    MarkWritten(index, 0);
                    if (attempt > retries) throw RangeFailure(segment, url, response.StatusCode);
                    continue;
                }

                if (response.StatusCode == HttpStatusCode.OK && index > 0 && have == 0)
                {
                    // Ranged request answered with the whole body for a later
                    // segment: the source does not honour ranges.
                    throw RangeFailure(segment, url, response.StatusCode);
                }

                if (classifyCloudflare(response, url) is { } blocked)
                    throw blocked;

                if (TransferEngine.IsTransient(response.StatusCode) && attempt <= retries)
                {
                    await Task.Delay(TransferEngine.RetryDelay, ct).ConfigureAwait(false);
                    continue;
                }

                response.EnsureSuccessStatusCode();

                using var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                var buffer = new byte[BufferSize];
                long written = have;
                int read;
                while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    int take = (int)Math.Min(read, need - written);
                    if (take <= 0) break;
                    await dest.WriteAsync(buffer.AsMemory(0, take), ct).ConfigureAwait(false);
                    written += take;
                    MarkWritten(index, written);
                    addBytes(take);
                    await throttleAsync(take, ct).ConfigureAwait(false);
                }

                if (written >= need)
                {
                    MarkWritten(index, need);
                    return;
                }

                // Short body (the server closed early): keep the grown part and
                // resume on the next attempt.
                if (attempt > retries) throw RangeFailure(segment, url, response.StatusCode);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) when ((ex is HttpRequestException or IOException) && attempt <= retries)
            {
                await Task.Delay(TransferEngine.RetryDelay, ct).ConfigureAwait(false);
            }
        }
    }

    private void MarkWritten(int index, long value)
    {
        lock (_gate) _written[index] = value;
    }

    private static Exception RangeFailure(Segment segment, string url, HttpStatusCode status) =>
        new RangeFailedException($"segment {segment.Index} could not be resumed from {url} (HTTP {(int)status})");

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
}
