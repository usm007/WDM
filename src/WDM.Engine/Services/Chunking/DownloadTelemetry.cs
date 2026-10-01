using System;
using System.Collections.Generic;
using System.Threading;

namespace WDM.Services.Chunking;

/// <summary>Outcome of one HTTP range request, for scheduling feedback.
/// Server-pressure errors and disk failures are classified separately:
/// disk failures must never reduce origin pressure.</summary>
public enum HttpOutcome
{
    Success,
    RangeNotSatisfiable,
    OkWithoutRange,
    ClientError,
    TooManyRequests,
    ServerError,
    Timeout,
    ConnectionReset,
    TlsFailure,
    ShortRead,
    WrongRange,
    ContentMismatch,
    DiskFatal,
    CloudflareBlocked,
}

/// <summary>UI-independent telemetry for the range subsystem.</summary>
public sealed class DownloadTelemetry
{
    private long _networkBytes;
    private long _writtenBytes;
    private long _committedBytes;
    private long _retryCount;
    private long _requestCount;
    private long _rangeCompletedCount;
    private long _rangeSplitCount;
    private long _speculationCount;
    private long _timeoutCount;
    private long _tooManyRequestsCount;
    private long _serverErrorCount;
    private long _shortReadCount;
    private long _wrongRangeCount;
    private long _contentMismatchCount;
    private int _activeWorkers;
    private long _rangeTicksSum;
    private long _rangeSamples;

    // Throughput EWMA state (guarded by _ewmaLock).
    private readonly object _ewmaLock = new();
    private double _ewmaBps;
    private DateTime _windowStart = DateTime.UtcNow;
    private long _windowBytes;

    public void AddNetworkBytes(long n) => Interlocked.Add(ref _networkBytes, n);
    public void AddWrittenBytes(long n) => Interlocked.Add(ref _writtenBytes, n);
    public void AddCommittedBytes(long n) => Interlocked.Add(ref _committedBytes, n);
    public void AddRetry() => Interlocked.Increment(ref _retryCount);
    public void AddRequest() => Interlocked.Increment(ref _requestCount);
    public void AddRangeCompleted(TimeSpan duration)
    {
        Interlocked.Increment(ref _rangeCompletedCount);
        Interlocked.Add(ref _rangeTicksSum, duration.Ticks);
        Interlocked.Increment(ref _rangeSamples);
    }
    public void AddSplit() => Interlocked.Increment(ref _rangeSplitCount);
    public void AddSpeculation() => Interlocked.Increment(ref _speculationCount);

    public void RecordOutcome(HttpOutcome outcome)
    {
        switch (outcome)
        {
            case HttpOutcome.Timeout: Interlocked.Increment(ref _timeoutCount); break;
            case HttpOutcome.TooManyRequests: Interlocked.Increment(ref _tooManyRequestsCount); break;
            case HttpOutcome.ServerError: Interlocked.Increment(ref _serverErrorCount); break;
            case HttpOutcome.ShortRead: Interlocked.Increment(ref _shortReadCount); break;
            case HttpOutcome.WrongRange: Interlocked.Increment(ref _wrongRangeCount); break;
            case HttpOutcome.ContentMismatch: Interlocked.Increment(ref _contentMismatchCount); break;
        }
    }

    public void SetActiveWorkers(int n) => Volatile.Write(ref _activeWorkers, n);

    /// <summary>Feeds the throughput EWMA; call per completed request or periodically.</summary>
    public void AddThroughputSample(long bytes, TimeSpan elapsed)
    {
        if (bytes <= 0 || elapsed.TotalSeconds <= 0)
            return;
        double sample = bytes / elapsed.TotalSeconds;
        lock (_ewmaLock)
        {
            _ewmaBps = _ewmaBps <= 0 ? sample : (_ewmaBps * 0.7 + sample * 0.3);
            _windowBytes += bytes;
        }
    }

    public double EwmaBps { get { lock (_ewmaLock) return _ewmaBps; } }

    /// <summary>Aggregate bytes/sec over the recent window (for the concurrency controller).</summary>
    public double DrainWindowBps()
    {
        lock (_ewmaLock)
        {
            var now = DateTime.UtcNow;
            double secs = (now - _windowStart).TotalSeconds;
            double bps = secs > 0 ? _windowBytes / secs : 0;
            _windowStart = now;
            _windowBytes = 0;
            return bps;
        }
    }

    public TelemetrySnapshot Snapshot(long totalBytes)
    {
        long committed = Interlocked.Read(ref _committedBytes);
        double bps = EwmaBps;
        double remaining = Math.Max(0, totalBytes - committed);
        return new TelemetrySnapshot
        {
            NetworkBytes = Interlocked.Read(ref _networkBytes),
            WrittenBytes = Interlocked.Read(ref _writtenBytes),
            CommittedBytes = committed,
            RetryCount = Interlocked.Read(ref _retryCount),
            RequestCount = Interlocked.Read(ref _requestCount),
            RangesCompleted = Interlocked.Read(ref _rangeCompletedCount),
            Splits = Interlocked.Read(ref _rangeSplitCount),
            Speculations = Interlocked.Read(ref _speculationCount),
            Timeouts = Interlocked.Read(ref _timeoutCount),
            TooManyRequests = Interlocked.Read(ref _tooManyRequestsCount),
            ServerErrors = Interlocked.Read(ref _serverErrorCount),
            ShortReads = Interlocked.Read(ref _shortReadCount),
            WrongRanges = Interlocked.Read(ref _wrongRangeCount),
            ContentMismatches = Interlocked.Read(ref _contentMismatchCount),
            ActiveWorkers = Volatile.Read(ref _activeWorkers),
            EwmaBps = bps,
            AverageRangeSeconds = Interlocked.Read(ref _rangeSamples) > 0
                ? new TimeSpan(Interlocked.Read(ref _rangeTicksSum) / Interlocked.Read(ref _rangeSamples)).TotalSeconds : 0,
            EstimatedEta = bps > 1 ? TimeSpan.FromSeconds(remaining / bps) : (TimeSpan?)null,
        };
    }
}

public sealed class TelemetrySnapshot
{
    public long NetworkBytes { get; set; }
    public long WrittenBytes { get; set; }
    public long CommittedBytes { get; set; }
    public long RetryCount { get; set; }
    public long RequestCount { get; set; }
    public long RangesCompleted { get; set; }
    public long Splits { get; set; }
    public long Speculations { get; set; }
    public long Timeouts { get; set; }
    public long TooManyRequests { get; set; }
    public long ServerErrors { get; set; }
    public long ShortReads { get; set; }
    public long WrongRanges { get; set; }
    public long ContentMismatches { get; set; }
    public int ActiveWorkers { get; set; }
    public double EwmaBps { get; set; }
    public double AverageRangeSeconds { get; set; }
    public TimeSpan? EstimatedEta { get; set; }

    /// <summary>Bytes pulled from the network beyond bytes committed to disk.</summary>
    public long OverheadBytes => Math.Max(0, NetworkBytes - CommittedBytes);
}
