using System;

namespace WDM.Services.Chunking;

/// <summary>
/// Feedback controller coupling range sizing and concurrency. Both adapt from the
/// same smoothed measurements: EWMA throughput, average range duration, aggregate
/// throughput trend, and error rate. Single-sample decisions are never made.
/// </summary>
public sealed class SchedulerController
{
    public const double TargetRangeDurationSec = 2.0;
    public const long MinRangeSize = 256L * 1024;
    public const long MaxRangeSize = 64L * 1024 * 1024;
    public const long MinSplitSize = 2L * 1024 * 1024;

    public const int MinWorkers = 1;
    public const int MaxWorkers = 16;
    public const int InitialWorkers = 4;

    /// <summary>No-progress time before a lease is a stall candidate.</summary>
    public static readonly TimeSpan StallThreshold = TimeSpan.FromSeconds(5);
    /// <summary>Lease throughput below this fraction of the median is relatively straggling.</summary>
    public const double StragglerThroughputFactor = 0.25;
    /// <summary>Lease ETA above this multiple of the median ETA is straggling.</summary>
    public const double StragglerEtaFactor = 3.0;
    /// <summary>ETA multiple above target before idle workers may speculate.</summary>
    public const double SpeculationEtaFactor = 4.0;

    private readonly object _lock = new();
    private double _ewmaBps;
    private double _avgRangeSeconds = TargetRangeDurationSec;
    private double _lastAggregateBps;
    private double _bestAggregateBps;
    private int _desiredWorkers;

    public SchedulerController(int initialWorkers = InitialWorkers)
    {
        _desiredWorkers = Math.Clamp(initialWorkers, MinWorkers, MaxWorkers);
    }

    public int DesiredWorkers { get { lock (_lock) return _desiredWorkers; } }
    public double EwmaBps { get { lock (_lock) return _ewmaBps; } }
    public double AverageRangeSeconds { get { lock (_lock) return _avgRangeSeconds; } }

    public void AddThroughputSample(long bytes, double seconds)
    {
        if (bytes <= 0 || seconds <= 0)
            return;
        double sample = bytes / seconds;
        lock (_lock)
            _ewmaBps = _ewmaBps <= 0 ? sample : (_ewmaBps * 0.7 + sample * 0.3);
    }

    public void AddRangeDuration(double seconds)
    {
        if (seconds <= 0 || double.IsNaN(seconds) || double.IsInfinity(seconds))
            return;
        lock (_lock)
            _avgRangeSeconds = _avgRangeSeconds <= 0 ? seconds : (_avgRangeSeconds * 0.7 + seconds * 0.3);
    }

    /// <summary>Seed size before any throughput sample exists (old fixed heuristic, seed only).</summary>
    public static long SeedRangeSize(long totalBytes) =>
        Math.Clamp(totalBytes / 32L, MinRangeSize, MaxRangeSize);

    /// <summary>Future range size targeting ~2s transfers at measured throughput.</summary>
    public long DesiredRangeSize()
    {
        lock (_lock)
        {
            if (_ewmaBps <= 1)
                return 4L * 1024 * 1024;
            long target = (long)(_ewmaBps * TargetRangeDurationSec);
            // Duration feedback: too-short ranges waste requests, too-long ranges stall adaptation.
            if (_avgRangeSeconds < 0.5)
                target *= 2;
            else if (_avgRangeSeconds > 8)
                target /= 2;
            return Math.Clamp(target, MinRangeSize, MaxRangeSize);
        }
    }

    /// <summary>
    /// Periodic concurrency review from aggregate throughput trend + error pressure.
    /// Rising → add, flat → hold, falling/errors → reduce.
    /// </summary>
    public int Review(double aggregateBps, double errorRate)
    {
        lock (_lock)
        {
            if (errorRate > 0.25)
            {
                _desiredWorkers = Math.Max(MinWorkers, _desiredWorkers - 2);
            }
            else if (errorRate > 0.1 || (_bestAggregateBps > 0 && aggregateBps < _bestAggregateBps * 0.85))
            {
                _desiredWorkers = Math.Max(MinWorkers, _desiredWorkers - 1);
            }
            else if (_lastAggregateBps > 0 && aggregateBps > _lastAggregateBps * 1.1 && errorRate < 0.05)
            {
                _desiredWorkers = Math.Min(MaxWorkers, _desiredWorkers + 1);
            }
            else if (_lastAggregateBps <= 0 && aggregateBps > 0)
            {
                // First measurement: probe upward once when the pipe looks healthy.
                if (errorRate < 0.05)
                    _desiredWorkers = Math.Min(MaxWorkers, _desiredWorkers + 1);
            }
            _lastAggregateBps = aggregateBps;
            if (aggregateBps > _bestAggregateBps)
                _bestAggregateBps = aggregateBps;
            return _desiredWorkers;
        }
    }

    /// <summary>Server-pressure feedback bypasses the trend logic: cut immediately.</summary>
    public void OnServerPressure()
    {
        lock (_lock)
            _desiredWorkers = Math.Max(MinWorkers, _desiredWorkers - 1);
    }
}
