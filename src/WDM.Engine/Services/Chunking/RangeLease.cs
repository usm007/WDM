using System;

namespace WDM.Services.Chunking;

/// <summary>
/// Runtime-only ownership of an unfinished byte interval [Start, EndExclusive).
/// Boundaries are immutable while active; progress advances <see cref="CommittedOffset"/>
/// only after bytes are durably written. Never persisted — a crash drops leases and
/// the scheduler rebuilds unfinished work from the durable <see cref="CompletionMap"/>.
/// </summary>
public sealed class RangeLease
{
    public Guid Id { get; } = Guid.NewGuid();

    /// <summary>First byte owned (inclusive). Immutable for the lease lifetime.</summary>
    public long Start { get; }

    /// <summary>One past the last byte owned. Immutable for the lease lifetime.</summary>
    public long EndExclusive { get; }

    public int WorkerId { get; set; }
    public DateTime StartedAt { get; } = DateTime.UtcNow;
    public DateTime LastProgressAt { get; set; } = DateTime.UtcNow;

    /// <summary>Authoritative boundary: bytes in [Start, CommittedOffset) are on disk.</summary>
    public long CommittedOffset { get; set; }

    public long NetworkBytes { get; set; }
    public long WrittenBytes { get; set; }

    /// <summary>Rolling bytes/sec observed by the owning worker.</summary>
    public double ThroughputBps { get; set; }

    public int RetryCount { get; set; }

    /// <summary>Bumped by the scheduler when the lease is invalidated (split).
    /// Stale workers holding an old generation must abort, never write.</summary>
    public int Generation { get; set; }

    public bool IsValid { get; set; } = true;

    public RangeLease(long start, long endExclusive, int workerId)
    {
        if (start < 0 || endExclusive <= start)
            throw new ArgumentOutOfRangeException(nameof(start));
        Start = start;
        EndExclusive = endExclusive;
        CommittedOffset = start;
        WorkerId = workerId;
    }

    public long Length => EndExclusive - Start;
    public long Remaining => EndExclusive - CommittedOffset;

    public double EstimatedRemainingSeconds =>
        ThroughputBps > 1 ? Remaining / ThroughputBps : double.PositiveInfinity;
}
