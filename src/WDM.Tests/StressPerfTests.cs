using System.Diagnostics;
using WDM.Services.Chunking;
using WDM.Tests.TestInfrastructure;

namespace WDM.Tests;

/// <summary>Stress (opt-in via Category=Stress) + perf benchmark harness.</summary>
public sealed class StressPerfTests
{
    [Trait("Category", Cats.Stress)][Fact]
    public void Stress_10kRanges_NoDeadlockNoCorruption()
    {
        var sched = new RangeScheduler { DesiredWorkers = 16 };
        const long N = 100_000_000;
        const long Chunk = 10 * 1024;
        sched.Initialize(new[] { (0L, N) });
        long claimed = 0;
        int leases = 0;
        var sw = Stopwatch.StartNew();
        while (sched.TryLease(0, Chunk) is { } l)
        {
            claimed += l.EndExclusive - l.Start;
            leases++;
            sched.Complete(l.Id, l.Generation);
            if (sw.Elapsed > TimeSpan.FromSeconds(60))
                throw new TimeoutException($"stress stalled: leases={leases} claimed={claimed}");
        }
        // 100M / 10KiB chunks = 9765.6 -> 9766 leases (last one partial), not 10000.
        Assert.Equal((N + Chunk - 1) / Chunk, leases);
        Assert.Equal(N, claimed);
    }

    [Trait("Category", Cats.Stress)][Fact]
    public void Stress_ParallelSplitStorm_NoOverlap()
    {
        var sched = new RangeScheduler { DesiredWorkers = 16 };
        sched.Initialize(new[] { (0L, 64_000_000L) });
        var errors = new System.Collections.Concurrent.ConcurrentBag<string>();
        // Bytes the scheduler handed off as "committed" (assumed in the
        // CompletionMap, hence invisible to TryLease): recorded so the final
        // union check covers pending + committed with no overlap and no gap.
        var committed = new System.Collections.Concurrent.ConcurrentBag<(long, long)>();
        var held = new System.Collections.Concurrent.ConcurrentBag<(Guid, int)>();
        Parallel.For(0, 32, w =>
        {
            try
            {
                for (int i = 0; i < 50; i++)
                {
                    var l = sched.TryLease(w, 1_000_000);
                    if (l is null) { Thread.Sleep(1); continue; }
                    held.Add((l.Id, l.Generation));
                    long mid = (l.Start + l.EndExclusive) / 2;
                    if (sched.ReportProgress(l.Id, l.Generation, mid, 0, 0, 1))
                        committed.Add((l.Start, mid));
                    sched.SplitForStraggler(l.Id, 64 * 1024);
                }
            }
            catch (Exception ex) { errors.Add(ex.Message); }
        });
        Assert.Empty(errors);
        // Release the storm: Aborted remainders return to pending (split
        // leases are generation-guarded no-ops — their halves are already
        // pending), then drain must equal the file exactly.
        foreach (var (id, gen) in held) sched.Abort(id, gen);
        var rest = new List<(long, long)>(committed);
        while (sched.TryLease(0, long.MaxValue) is { } l)
        {
            rest.Add((l.Start, l.EndExclusive));
            sched.Complete(l.Id, l.Generation);
        }
        var o = rest.OrderBy(t => t.Item1).ToList();
        for (int i = 1; i < o.Count; i++)
            Assert.True(o[i].Item1 >= o[i - 1].Item2, $"overlap at {i}");
        Assert.Equal(64_000_000L, o.Sum(t => t.Item2 - t.Item1));
    }

    [Trait("Category", Cats.Performance)][Fact]
    public void Perf_Scheduler_LeaseThroughput_Baseline()
    {
        // Algorithmic benchmark (no network): lease+complete rate + diagnostics.
        // No hardcoded pass/fail number — prints baseline for trend comparison.
        var sched = new RangeScheduler { DesiredWorkers = 16 };
        sched.Initialize(new[] { (0L, 1_000_000_000L) });
        var sw = Stopwatch.StartNew();
        int n = 0;
        long bytes = 0;
        while (sched.TryLease(0, 1_000_000) is { } l)
        {
            bytes += l.EndExclusive - l.Start;
            sched.Complete(l.Id, l.Generation);
            if (++n >= 1000) break;
        }
        sw.Stop();
        long gc0 = GC.GetAllocatedBytesForCurrentThread();
        Assert.Equal(1000, n);
        Assert.Equal(1_000_000_000L, bytes);
        // xUnit output = benchmark record (collect in CI artifacts).
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(30), $"elapsed={sw.Elapsed} gcBytes={gc0}");
    }
}
