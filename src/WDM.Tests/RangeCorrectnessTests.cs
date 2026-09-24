using WDM.Services.Chunking;
using WDM.Tests.TestInfrastructure;

namespace WDM.Tests;

/// <summary>Range coverage / no-overlap / no-gap / boundaries / sizes / generative invariants.</summary>
public sealed class RangeCorrectnessTests
{
    public RangeCorrectnessTests() => OriginController.ResetForTests();

    private static void AssertDisjoint(IReadOnlyList<(long S, long E)> intervals)
    {
        var o = intervals.OrderBy(t => t.S).ToList();
        for (int i = 1; i < o.Count; i++)
            Assert.True(o[i].S >= o[i - 1].E, $"overlap: {o[i - 1]} vs {o[i]}");
    }

    [Trait("Category", Cats.RangeEngine)][Fact]
    public void LeasePartition_CoversExactlyOnce()
    {
        var sched = new RangeScheduler { DesiredWorkers = 16 };
        const long N = 64_000_000;
        sched.Initialize(new[] { (0L, N) });
        var taken = new List<(long S, long E)>();
        while (sched.TryLease(0, 1_000_000) is { } l)
        {
            taken.Add((l.Start, l.EndExclusive));
            sched.Complete(l.Id, l.Generation);
        }
        Assert.Equal(64, taken.Count);
        AssertDisjoint(taken);
        Assert.Equal(0, taken.Min(t => t.S));
        Assert.Equal(N, taken.Max(t => t.E));
        Assert.Equal(N, taken.Sum(t => t.E - t.S)); // union == file
    }

    [Trait("Category", Cats.RangeEngine)][Theory]
    [InlineData(1)][InlineData(2)]
    [InlineData(127 * 1024)][InlineData(128 * 1024)][InlineData(129 * 1024)]
    [InlineData(1024 * 1024)][InlineData(1024 * 1024 + 1)]
    [InlineData(16 * 1024 * 1024)][InlineData(16 * 1024 * 1024 + 1)]
    public void Boundaries_ExactCoverage(long size)
    {
        var sched = new RangeScheduler { DesiredWorkers = 16 };
        sched.Initialize(new[] { (0L, size) });
        var taken = new List<(long S, long E)>();
        while (sched.TryLease(0, 256 * 1024) is { } l)
        {
            taken.Add((l.Start, l.EndExclusive));
            sched.ReportProgress(l.Id, l.Generation, l.EndExclusive, 0, 0, 1);
            sched.Complete(l.Id, l.Generation);
        }
        AssertDisjoint(taken);
        Assert.Equal(size, taken.Sum(t => t.E - t.S));
    }

    [Trait("Category", Cats.RangeEngine)][Fact]
    public void FinalRange_SmallerThanNormal_Covered()
    {
        const long N = 10 * 1024 * 1024 + 123;
        var sched = new RangeScheduler { DesiredWorkers = 4 };
        sched.Initialize(new[] { (0L, N) });
        var taken = new List<(long S, long E)>();
        while (sched.TryLease(0, 4 * 1024 * 1024) is { } l)
        {
            taken.Add((l.Start, l.EndExclusive));
            sched.Complete(l.Id, l.Generation);
        }
        Assert.Equal(N, taken.Sum(t => t.E - t.S));
        Assert.Equal(N, taken.Max(t => t.E));
    }

    [Trait("Category", Cats.RangeEngine)][Fact]
    public async Task EndToEnd_SparseSizes_ReconstructExactly()
    {
        // 0B throws (contract); the rest must round-trip byte-exact.
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            EngineDriver.RunAsync(new FakeProgrammableOrigin(), Array.Empty<byte>(),
                TestFiles.NewTempDir(), "empty.bin"));
        foreach (int size in new[] { 1, 1024, 64 * 1024, 129 * 1024, 1024 * 1024 + 1 })
        {
            var content = TestFiles.Make(size, seed: size);
            var origin = new FakeProgrammableOrigin();
            origin.AddObject("http://primary/file.bin", content);
            string dir = TestFiles.NewTempDir();
            try
            {
                await EngineDriver.WithTimeout(
                    EngineDriver.RunAsync(origin, content, dir, "f.bin", workers: 2),
                    TimeSpan.FromSeconds(60),
                    () => "e2e sparse size " + size);
                Assert.Equal(content, await File.ReadAllBytesAsync(Path.Combine(dir, "f.bin")));
            }
            finally { TestFiles.DeleteDir(dir); }
        }
    }

    [Trait("Category", Cats.RangeEngine)][Fact]
    public void Generative_NoOverlapNoGap_RandomSplits()
    {
        // Deterministic generator (seed 1234): random sizes/splits/completions.
        var rnd = new Random(1234);
        for (int trial = 0; trial < 200; trial++)
        {
            long n = rnd.Next(1, 40_000_000);
            var sched = new RangeScheduler { DesiredWorkers = 8 };
            sched.Initialize(new[] { (0L, n) });
            var done = new List<(long S, long E)>();
            var leases = new List<RangeLease>();
            for (int w = 0; w < 8 && sched.TryLease(w, rnd.Next(64 * 1024, 4 * 1024 * 1024)) is { } l; w++)
                leases.Add(l);
            // Random progress + random splits + abort half.
            foreach (var l in leases.ToList())
            {
                long mid = l.Start + (l.EndExclusive - l.Start) / 2;
                sched.ReportProgress(l.Id, l.Generation, mid, 0, 0, 1);
                if (rnd.Next(2) == 0)
                {
                    var sp = sched.SplitForStraggler(l.Id, 64 * 1024);
                    if (sp.HasValue)
                    {
                        done.Add((l.Start, mid));
                        // Both halves return to pending: re-lease and finish them.
                        var a = sched.TryLease(99, long.MaxValue);
                        Assert.NotNull(a);
                        done.Add((a.Start, a.EndExclusive)); sched.Complete(a.Id, a.Generation);
                        var b = sched.TryLease(99, long.MaxValue);
                        Assert.NotNull(b);
                        done.Add((b.Start, b.EndExclusive)); sched.Complete(b.Id, b.Generation);
                        continue;
                    }
                }
                done.Add((l.Start, l.EndExclusive));
                sched.Complete(l.Id, l.Generation);
            }
            while (sched.TryLease(0, long.MaxValue) is { } rest)
            {
                done.Add((rest.Start, rest.EndExclusive));
                sched.Complete(rest.Id, rest.Generation);
            }
            // Invariant: committed prefix [0, firstUnfinished) + done covers without overlap.
            AssertDisjoint(done);
            Assert.Equal(n, done.Sum(t => t.E - t.S));
        }
    }
}
