using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace WDM.Services.Chunking;

/// <summary>
/// Orchestrates one HTTP range download: probe/classify stays in
/// <see cref="DownloadEngine"/>, all range scheduling lives here.
/// </summary>
public sealed class AdaptiveRangeEngine
{
    public CompletionMap Map { get; private set; } = null!;
    public DownloadTelemetry Telemetry { get; } = new();
    public SchedulerController Controller { get; private set; } = null!;
    public RangeScheduler Scheduler { get; } = new();
    public MirrorSelector Mirrors { get; private set; } = null!;

    public long CompletedBytesBaseline => Map?.CompletedBytes ?? 0;

    public List<SegmentRecord>? SnapshotSegments() => Map?.SnapshotSegments();

    public double[] ProgressFractions() => Map?.ProgressFractions() ?? Array.Empty<double>();

    public TelemetrySnapshot Snapshot(long totalBytes) => Telemetry.Snapshot(totalBytes);

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
        Func<HttpMethod, System.Net.Http.Headers.RangeHeaderValue?, string, HttpRequestMessage> buildRequest,
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
        Func<HttpMethod, System.Net.Http.Headers.RangeHeaderValue?, string, HttpRequestMessage> buildRequest,
        Func<HttpResponseMessage, string, Exception?> classifyCloudflare,
        Func<int, CancellationToken, Task> throttleAsync,
        Action<long> addBytes,
        Action<long> setBaseline,
        CancellationToken sessionToken)
    {
        if (totalBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(totalBytes));

        Map = CompletionMap.LoadOrMigrate(statePath, totalBytes, CompletionMap.DefaultBlockSize,
            etag, lastModified, legacySegments);
        Map.SetIdentity(etag, lastModified);
        Mirrors = new MirrorSelector(urls, etag, lastModified, totalBytes);
        Controller = new SchedulerController(initialWorkers);
        Scheduler.DesiredWorkers = Controller.DesiredWorkers;

        // Preallocated destination (preserved strategy) + resume-corruption guard:
        // completed bytes can never exceed bytes actually on disk.
        await using (var prealloc = new FileStream(destPath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.ReadWrite))
        {
            long onDisk = prealloc.Length;
            if (onDisk < totalBytes && Map.CompletedBytes > onDisk)
            {
                Map = new CompletionMap(totalBytes, CompletionMap.DefaultBlockSize);
                Map.SetIdentity(etag, lastModified);
                Map.Save(statePath);
            }
            if (prealloc.Length != totalBytes)
                prealloc.SetLength(totalBytes);
        }

        Scheduler.Initialize(Map.GetPendingRanges());
        try { setBaseline(Map.CompletedBytes); } catch { }
        if (!Scheduler.HasWork)
        {
            CompletionMap.Delete(statePath);
            return;
        }

        using var ctx = new WorkerRunContext(sessionToken);
        await using var file = new FileStream(destPath, FileMode.Open, FileAccess.Write,
            FileShare.ReadWrite, 1 << 20, FileOptions.Asynchronous | FileOptions.RandomAccess);

        var deps = new RangeTransportDeps
        {
            HttpProvider = httpProvider,
            BuildRequest = buildRequest,
            ClassifyCloudflare = classifyCloudflare,
            ThrottleAsync = throttleAsync,
            AddBytes = addBytes,
            OnServerPressure = Controller.OnServerPressure,
            MaxRetries = maxRetries,
            FileHandle = file.SafeFileHandle!,
            TotalBytes = totalBytes,
        };

        var workers = new List<Task>();
        for (int w = 0; w < SchedulerController.MaxWorkers; w++)
        {
            int id = w;
            workers.Add(RangeWorkerPool.RunWorkerAsync(id, Scheduler, Map, Mirrors,
                Controller, Telemetry, deps, ctx));
        }
        var control = ControlLoopAsync(statePath, totalBytes, ctx);

        try
        {
            await Task.WhenAll(workers.Concat(new[] { control }));
        }
        finally
        {
            try { Map.Save(statePath); } catch { }
        }

        // Surface worker failure (unless the session itself was cancelled).
        if (ctx.Failure is Exception failure && !sessionToken.IsCancellationRequested)
            throw failure;
        sessionToken.ThrowIfCancellationRequested();

        if (!Map.IsComplete)
            throw new InvalidOperationException("Download did not complete all ranges.");

        CompletionMap.Delete(statePath);
    }

    private async Task ControlLoopAsync(string statePath, long totalBytes, WorkerRunContext ctx)
    {
        var token = ctx.Token;
        long lastRequests = 0, lastErrors = 0;
        try
        {
            while (!token.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), token);

                // Adaptive concurrency from aggregate trend + error rate.
                double aggBps = Telemetry.DrainWindowBps();
                var snap = Telemetry.Snapshot(totalBytes);
                long errors = snap.Timeouts + snap.TooManyRequests + snap.ServerErrors + snap.ShortReads + snap.WrongRanges;
                long reqs = Math.Max(1, snap.RequestCount - lastRequests);
                double errRate = Math.Clamp((double)(errors - lastErrors) / reqs, 0, 1);
                lastRequests = snap.RequestCount;
                lastErrors = errors;
                Scheduler.DesiredWorkers = Controller.Review(aggBps, errRate);

                // Straggler split + tail speculation.
                var active = Scheduler.ActiveSnapshot();
                if (active.Count > 0)
                {
                    var ordered = active
                        .Where(l => l.ThroughputBps > 0)
                        .Select(l => l.ThroughputBps).OrderBy(x => x).ToList();
                    double medianBps = ordered.Count > 0 ? ordered[ordered.Count / 2] : 0;
                    var etas = active
                        .Select(l => l.EstimatedRemainingSeconds)
                        .Where(e => !double.IsInfinity(e)).OrderBy(x => x).ToList();
                    double medianEta = etas.Count > 0 ? etas[etas.Count / 2] : 0;

                    var now = DateTime.UtcNow;
                    foreach (var s in Scheduler.FindStragglers(medianBps, medianEta, now))
                    {
                        if (Scheduler.SplitForStraggler(s.Id, SchedulerController.MinSplitSize) is not null)
                            Telemetry.AddSplit();
                    }

                    // Speculation: pending drained, one slow tail lease, idle capacity.
                    if (Scheduler.PendingCount == 0 && active.Count == 1 &&
                        Scheduler.DesiredWorkers > 1)
                    {
                        var tail = active[0];
                        double target = SchedulerController.TargetRangeDurationSec;
                        if (tail.Remaining > SchedulerController.MinSplitSize &&
                            tail.EstimatedRemainingSeconds > target * SchedulerController.SpeculationEtaFactor)
                        {
                            if (Scheduler.SplitForStraggler(tail.Id, SchedulerController.MinSplitSize) is not null)
                                Telemetry.AddSpeculation();
                        }
                    }
                }

                try { Map.SaveIfDirty(statePath); } catch { }

                if (!Scheduler.HasWork)
                    return;
            }
        }
        catch (OperationCanceledException) { /* teardown */ }
    }
}
