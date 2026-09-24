using System;
using System.Collections.Generic;
using System.Linq;

namespace WDM.Services.Chunking;

/// <summary>
/// Owns unfinished byte space. Workers lease disjoint intervals; two active leases
/// never own the same uncommitted byte (single-lock invariant). Splits only ever
/// carve the unfinished remainder [committed, end): completed bytes are immutable.
/// </summary>
public sealed class RangeScheduler
{
    private readonly object _lock = new();
    private readonly LinkedList<(long Start, long EndExclusive)> _pending = new();
    private readonly Dictionary<Guid, RangeLease> _active = new();
    private int _desiredWorkers = SchedulerController.InitialWorkers;

    public int DesiredWorkers
    {
        get { lock (_lock) return _desiredWorkers; }
        set { lock (_lock) _desiredWorkers = Math.Clamp(value, SchedulerController.MinWorkers, SchedulerController.MaxWorkers); }
    }

    public int ActiveCount { get { lock (_lock) return _active.Count; } }
    public int PendingCount { get { lock (_lock) return _pending.Count; } }

    public bool HasWork
    {
        get { lock (_lock) return _pending.Count > 0 || _active.Count > 0; }
    }

    public void Initialize(IEnumerable<(long Start, long EndExclusive)> gaps)
    {
        lock (_lock)
        {
            _pending.Clear();
            _active.Clear();
            foreach (var g in gaps.OrderBy(g => g.Start))
            {
                if (g.EndExclusive > g.Start)
                    InsertSorted(g.Start, g.EndExclusive);
            }
        }
    }

    private void InsertSorted(long start, long endExclusive)
    {
        var node = _pending.First;
        while (node is not null && node.Value.Start < start)
            node = node.Next;
        // Merge with overlapping neighbours to keep the list disjoint.
        if (node?.Previous is { } prev && prev.Value.EndExclusive > start)
        {
            start = Math.Min(start, prev.Value.Start);
            endExclusive = Math.Max(endExclusive, prev.Value.EndExclusive);
            _pending.Remove(prev);
        }
        while (node is not null && node.Value.Start < endExclusive)
        {
            endExclusive = Math.Max(endExclusive, node.Value.EndExclusive);
            var drop = node;
            node = node.Next;
            _pending.Remove(drop);
        }
        if (node is null)
            _pending.AddLast((start, endExclusive));
        else
            _pending.AddBefore(node, (start, endExclusive));
    }

    /// <summary>Leases up to desiredSize bytes. Null when the concurrency gate
    /// (active ≥ desired) holds or no pending work remains. Never duplicates.</summary>
    public RangeLease? TryLease(int workerId, long desiredSize)
    {
        lock (_lock)
        {
            if (_active.Count >= _desiredWorkers)
                return null;
            var first = _pending.First;
            if (first is null)
                return null;
            _pending.RemoveFirst();
            var (s, e) = first.Value;
            if (e - s > desiredSize)
            {
                _pending.AddFirst((s + desiredSize, e));
                e = s + desiredSize;
            }
            var lease = new RangeLease(s, e, workerId);
            _active[lease.Id] = lease;
            return lease;
        }
    }

    public bool IsValid(Guid id, int generation)
    {
        lock (_lock)
            return _active.TryGetValue(id, out var l) && l.IsValid && l.Generation == generation;
    }

    /// <summary>Advances the committed offset after successful writes.
    /// Returns false when the lease was invalidated (worker must abort).</summary>
    public bool ReportProgress(Guid id, int generation, long newCommittedOffset,
        long networkBytes, long writtenBytes, double throughputBps)
    {
        lock (_lock)
        {
            if (!_active.TryGetValue(id, out var l) || !l.IsValid || l.Generation != generation)
                return false;
            if (newCommittedOffset > l.CommittedOffset)
            {
                l.CommittedOffset = Math.Min(newCommittedOffset, l.EndExclusive);
                l.LastProgressAt = DateTime.UtcNow;
            }
            l.NetworkBytes = networkBytes;
            l.WrittenBytes = writtenBytes;
            l.ThroughputBps = throughputBps;
            return true;
        }
    }

    /// <summary>Lease fully written: releases ownership. The map already holds the bytes.</summary>
    public void Complete(Guid id, int generation)
    {
        lock (_lock)
        {
            if (_active.TryGetValue(id, out var l) && l.Generation == generation)
                _active.Remove(id);
        }
    }

    /// <summary>Returns the unfinished remainder to pending (retryable failure, cancel).</summary>
    public void Abort(Guid id, int generation)
    {
        lock (_lock)
        {
            if (!_active.TryGetValue(id, out var l) || l.Generation != generation)
                return;
            _active.Remove(id);
            if (l.CommittedOffset < l.EndExclusive)
                InsertSorted(l.CommittedOffset, l.EndExclusive);
        }
    }

    /// <summary>
    /// Atomically splits the unfinished remainder [committed, end) at midpoint.
    /// The old lease is invalidated (generation guard); both halves return to
    /// pending so any worker — including the straggler — picks them up with no
    /// gap and no overlap. Null when the remainder is too small to split.
    /// </summary>
    public (long AStart, long AEnd, long BStart, long BEnd)? SplitForStraggler(Guid id, long minSplitSize)
    {
        lock (_lock)
        {
            if (!_active.TryGetValue(id, out var l) || !l.IsValid)
                return null;
            long s = l.CommittedOffset, e = l.EndExclusive;
            if (e - s < minSplitSize)
                return null;
            l.IsValid = false;
            l.Generation++;
            _active.Remove(id);
            long mid = s + (e - s) / 2;
            InsertSorted(s, mid);
            InsertSorted(mid, e);
            return (s, mid, mid, e);
        }
    }

    public List<RangeLease> ActiveSnapshot()
    {
        lock (_lock) return _active.Values.ToList();
    }

    /// <summary>Straggler candidates: stalled absolutely, or relatively slow with a bad ETA.</summary>
    public List<RangeLease> FindStragglers(double medianBps, double medianEtaSec, DateTime now)
    {
        var result = new List<RangeLease>();
        lock (_lock)
        {
            foreach (var l in _active.Values)
            {
                if (l.Remaining < SchedulerController.MinSplitSize)
                    continue;
                if (now - l.LastProgressAt >= SchedulerController.StallThreshold)
                {
                    result.Add(l);
                    continue;
                }
                bool slow = medianBps > 0 && l.ThroughputBps > 0 &&
                    l.ThroughputBps < medianBps * SchedulerController.StragglerThroughputFactor;
                double eta = l.EstimatedRemainingSeconds;
                bool badEta = !double.IsInfinity(eta) && medianEtaSec > 0 &&
                    eta > medianEtaSec * SchedulerController.StragglerEtaFactor;
                if ((slow && badEta) || (badEta && l.ThroughputBps <= 0 && (now - l.StartedAt).TotalSeconds > 10))
                    result.Add(l);
            }
        }
        return result;
    }
}
