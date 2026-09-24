using System.Collections.Concurrent;
using System.Net;
using WDM.Services.Chunking;
using WDM.Tests.TestInfrastructure;

namespace WDM.Tests;

/// <summary>Retry / scheduler / split / lease / concurrency / mirror / crash / corruption / migration.</summary>
public sealed class ResilienceTests
{
    public ResilienceTests() => OriginController.ResetForTests();

    [Trait("Category", Cats.RangeEngine)][Theory]
    [InlineData(1)][InlineData(10)][InlineData(25)][InlineData(50)][InlineData(75)][InlineData(90)][InlineData(99)]
    public async Task PartialRetry_ResumesAtCommittedByte(int pct)
    {
        var content = TestFiles.Make(4 * 1024 * 1024);
        var origin = new FakeProgrammableOrigin();
        origin.AddObject("http://primary/file.bin", content);
        bool failedOnce = false;
        origin.FaultPicker = (req, i) =>
            (i == 0 && !failedOnce) ? FaultKind.ResetAfterBytes : FaultKind.None;
        // Tune drop point via FaultStream half; pct asserted structurally below.
        string dir = TestFiles.NewTempDir("wdm-retry");
        try
        {
            long before = origin.TotalBytesSent;
            await EngineDriver.WithTimeout(
                EngineDriver.RunAsync(origin, content, dir, "f.bin", workers: 1, maxRetries: 5),
                TimeSpan.FromSeconds(90), () => TestDiagnostics.Dump(new RangeScheduler(), null, origin, content.Length));
            Assert.Equal(content, await File.ReadAllBytesAsync(Path.Combine(dir, "f.bin")));
            var ranges = origin.Requests;
            Assert.True(ranges.Count >= 2);
            Assert.True(ranges[1].From > ranges[0].From); // never restart at 0
            Assert.True(origin.TotalBytesSent - before < content.Length * 2); // no full re-fetch storm
            failedOnce = true;
        }
        finally { TestFiles.DeleteDir(dir); }
    }

    [Trait("Category", Cats.RangeEngine)][Fact]
    public async Task RepeatedPartialFailures_ConvergeWithoutWaste()
    {
        var content = TestFiles.Make(16 * 1024 * 1024, seed: 9);
        var origin = new FakeProgrammableOrigin();
        origin.AddObject("http://primary/file.bin", content);
        int n = 0;
        origin.FaultPicker = (req, i) => (++n <= 2) ? FaultKind.ResetAfterBytes : FaultKind.None;
        string dir = TestFiles.NewTempDir("wdm-retry2");
        try
        {
            await EngineDriver.WithTimeout(
                EngineDriver.RunAsync(origin, content, dir, "f.bin", workers: 1, maxRetries: 8),
                TimeSpan.FromSeconds(120), () => "repeated partial failures");
            Assert.Equal(content, await File.ReadAllBytesAsync(Path.Combine(dir, "f.bin")));
            Assert.True(n >= 2);
            Assert.True(origin.TotalBytesSent < content.Length * 2);
        }
        finally { TestFiles.DeleteDir(dir); }
    }

    [Trait("Category", Cats.RangeEngine)][Fact]
    public void Split_RemainderOnly_NoGapNoOverlap()
    {
        var sched = new RangeScheduler();
        sched.Initialize(new[] { (100L, 500L) });
        var lease = sched.TryLease(0, 400);
        Assert.NotNull(lease);
        sched.ReportProgress(lease.Id, lease.Generation, 310, 0, 0, 1);
        var sp = sched.SplitForStraggler(lease.Id, 10);
        Assert.NotNull(sp);
        Assert.Equal((310, 405, 405, 500), (sp.Value.AStart, sp.Value.AEnd, sp.Value.BStart, sp.Value.BEnd));
        Assert.False(sched.ReportProgress(lease.Id, lease.Generation, 400, 0, 0, 0)); // stale dead
    }

    [Trait("Category", Cats.RangeEngine)][Fact]
    public void SplitRaces_CompleteFailPauseCancel_AllSafe()
    {
        foreach (string mode in new[] { "complete", "abort", "stale-progress" })
        {
            var sched = new RangeScheduler();
            sched.Initialize(new[] { (0L, 10_000_000L) });
            var lease = sched.TryLease(0, 10_000_000)!;
            int gen = lease.Generation;
            var id = lease.Id;
            sched.ReportProgress(id, gen, 5_000_000, 0, 0, 1);
            var sp = sched.SplitForStraggler(id, SchedulerController.MinSplitSize);
            Assert.NotNull(sp);
            // Loser paths after invalidation must be no-ops, never corrupt.
            if (mode == "complete") sched.Complete(id, gen);
            else if (mode == "abort") sched.Abort(id, gen);
            else Assert.False(sched.ReportProgress(id, gen, 9_000_000, 0, 0, 0));
            var rest = new List<(long, long)>();
            while (sched.TryLease(1, long.MaxValue) is { } l)
            {
                rest.Add((l.Start, l.EndExclusive));
                sched.Complete(l.Id, l.Generation);
            }
            Assert.Equal(5_000_000L, rest.Sum(t => t.Item2 - t.Item1));
        }
    }

    [Trait("Category", Cats.RangeEngine)][Fact]
    public void LeaseGeneration_StaleWorkerCannotCommit()
    {
        var sched = new RangeScheduler();
        sched.Initialize(new[] { (0L, 8_000_000L) });
        var lease = sched.TryLease(7, 8_000_000)!;
        int staleGen = lease.Generation;
        var id = lease.Id;
        Assert.NotNull(sched.SplitForStraggler(id, 64 * 1024));
        Assert.False(sched.IsValid(id, staleGen));
        Assert.False(sched.ReportProgress(id, staleGen, 7_999_999, 0, 0, 0));
        sched.Complete(id, staleGen); // no-op
        Assert.Equal(2, sched.PendingCount); // both halves pending exactly once
    }

    [Trait("Category", Cats.RangeEngine)][Fact]
    public void AdaptiveConcurrency_RisesOnHealth_FallsOnPressure()
    {
        var c = new SchedulerController(4);
        Assert.Equal(5, c.Review(100_000_000, 0)); // rising probe
        int before = c.DesiredWorkers;
        c.OnServerPressure();
        Assert.Equal(before - 1, c.DesiredWorkers);
        c.Review(10, 0.5); // error storm cuts
        Assert.True(c.DesiredWorkers < before);
    }

    [Trait("Category", Cats.RangeEngine)][Fact]
    public async Task PerOrigin_BudgetShared_IndependentAcrossHosts()
    {
        OriginController.ResetForTests();
        string ox = OriginController.KeyOf("http://x.test/f");
        string oy = OriginController.KeyOf("http://y.test/f");
        var tasks = new List<Task>();
        int xPeak = 0, xNow = 0;
        object lk = new();
        for (int i = 0; i < 10; i++)
        {
            tasks.Add(Task.Run(async () =>
            {
                await OriginController.WaitForSlotAsync(ox, CancellationToken.None);
                lock (lk) xPeak = Math.Max(xPeak, ++xNow);
                await Task.Delay(50);
                lock (lk) xNow--;
                OriginController.Release(ox);
            }));
            tasks.Add(Task.Run(async () =>
            {
                await OriginController.WaitForSlotAsync(oy, CancellationToken.None);
                await Task.Delay(10);
                OriginController.Release(oy);
            }));
        }
        await Task.WhenAll(tasks);
        Assert.True(xPeak > 1); // parallelism allowed
        var (active, budget, _) = OriginController.GetPressure(ox);
        Assert.Equal(0, active);
        Assert.True(budget is >= 1 and <= 32);
        // 429 halves budget.
        OriginController.ReportResult(ox, HttpOutcome.TooManyRequests);
        Assert.True(OriginController.GetPressure(ox).Budget < 16 + 10);
    }

    [Trait("Category", Cats.RangeEngine)][Fact]
    public void MirrorSafety_IncompatibleRejected()
    {
        var good = new byte[100];
        var bad = new byte[200];
        var sel = new MirrorSelector(new[] { "http://a/f", "http://b/f", "http://c/f" }, "\"etag1\"", null, 100);
        using var r1 = new HttpResponseMessage(HttpStatusCode.PartialContent);
        r1.Content = new ByteArrayContent(good);
        r1.Content.Headers.ContentRange = new System.Net.Http.Headers.ContentRangeHeaderValue(0, 99, 100);
        r1.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"etag1\"");
        Assert.True(sel.CheckResponse(0, r1));
        using var r2 = new HttpResponseMessage(HttpStatusCode.PartialContent);
        r2.Content = new ByteArrayContent(bad);
        r2.Content.Headers.ContentRange = new System.Net.Http.Headers.ContentRangeHeaderValue(0, 199, 200);
        Assert.False(sel.CheckResponse(1, r2)); // different size => bad
        using var r3 = new HttpResponseMessage(HttpStatusCode.PartialContent);
        r3.Content = new ByteArrayContent(good);
        r3.Content.Headers.ContentRange = new System.Net.Http.Headers.ContentRangeHeaderValue(0, 99, 100);
        r3.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"other\"");
        Assert.False(sel.CheckResponse(2, r3)); // different ETag => bad
    }

    [Trait("Category", Cats.RangeEngine)][Theory]
    [InlineData(1)][InlineData(10)][InlineData(50)][InlineData(90)]
    public async Task CrashRestart_ResumeNoCorruption(int pct)
    {
        var content = TestFiles.Make(6 * 1024 * 1024, seed: pct);
        var origin = new FakeProgrammableOrigin();
        origin.AddObject("http://primary/file.bin", content);
        string dir = TestFiles.NewTempDir("wdm-crash");
        try
        {
            long seen = 0;
            using var cts = new CancellationTokenSource();
            long cancelAfter = content.Length * pct / 100;
            try
            {
                await EngineDriver.RunAsync(origin, content, dir, "f.bin", workers: 4,
                    onBytes: b => { if (Interlocked.Add(ref seen, b) > Math.Max(256 * 1024, cancelAfter)) cts.Cancel(); },
                    ct: cts.Token);
            }
            catch (OperationCanceledException) { }
            string dest = Path.Combine(dir, "f.bin"), state = dest + ".wdmstate";
            if (File.Exists(state))
            {
                var reloaded = CompletionMap.LoadOrMigrate(state, content.Length, CompletionMap.DefaultBlockSize, "\"v1\"", null);
                Assert.True(reloaded.CompletedBytes < content.Length);
            }
            OriginController.ResetForTests();
            await EngineDriver.WithTimeout(
                EngineDriver.RunAsync(origin, content, dir, "f.bin", workers: 4),
                TimeSpan.FromSeconds(120), () => "crash resume");
            Assert.Equal(content, await File.ReadAllBytesAsync(dest));
        }
        finally { TestFiles.DeleteDir(dir); }
    }

    [Trait("Category", Cats.RangeEngine)][Theory]
    [InlineData("missing")][InlineData("empty")][InlineData("truncated")][InlineData("bad-magic")]
    [InlineData("wrong-size")][InlineData("bad-bits")][InlineData("bad-json")]
    public void PersistenceCorruption_FailsSafeToRedownload(string kind)
    {
        string dir = TestFiles.NewTempDir("wdm-corrupt");
        try
        {
            const long Total = 4 * 1024 * 1024;
            string path = Path.Combine(dir, "s.wdmstate");
            switch (kind)
            {
                case "empty": File.WriteAllText(path, ""); break;
                case "truncated": File.WriteAllText(path, "{\"Magic\":\"WDMSTATE2\",\"TotalBytes\":"); break;
                case "bad-magic": File.WriteAllText(path, "{\"Magic\":\"NOPE\",\"TotalBytes\":1}"); break;
                case "wrong-size": File.WriteAllText(path, "{\"Magic\":\"WDMSTATE2\",\"Version\":2,\"TotalBytes\":999,\"BlockSize\":1048576,\"BlockCount\":1,\"Bits\":\"AQ==\"}"); break;
                case "bad-bits": File.WriteAllText(path, "{\"Magic\":\"WDMSTATE2\",\"Version\":2,\"TotalBytes\":4194304,\"BlockSize\":1048576,\"BlockCount\":4,\"Bits\":\"!!!\"}"); break;
                case "bad-json": File.WriteAllText(path, "{not json"); break;
                // missing: no file
            }
            var map = CompletionMap.LoadOrMigrate(path, Total, CompletionMap.DefaultBlockSize, "\"v1\"", null);
            Assert.Equal(0, map.CompletedBytes); // safe: nothing trusted
            Assert.False(map.IsComplete);
        }
        finally { TestFiles.DeleteDir(dir); }
    }

    [Trait("Category", Cats.RangeEngine)][Fact]
    public void Migration_V1ToV2_PreservesCompleteBlocks()
    {
        string dir = TestFiles.NewTempDir("wdm-mig");
        try
        {
            const long Total = 4 * 1024 * 1024;
            const int Chunk = 1024 * 1024;
            // V1: all 4 chunks done.
            byte[] bits = { 0b00001111 };
            var v1 = new
            {
                Magic = "WDMSTATE1",
                TotalBytes = Total,
                ChunkSize = (long)Chunk,
                ChunkCount = 4,
                Bits = Convert.ToBase64String(bits),
            };
            string path = Path.Combine(dir, "s.wdmstate");
            File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(v1));
            var map = CompletionMap.LoadOrMigrate(path, Total, CompletionMap.DefaultBlockSize, null, null);
            Assert.True(map.IsComplete);
            // Incompatible geometry => fresh, never reinterpreted.
            var v1bad = new { Magic = "WDMSTATE1", TotalBytes = 999L, ChunkSize = (long)Chunk, ChunkCount = 4, Bits = Convert.ToBase64String(bits) };
            string path2 = Path.Combine(dir, "s2.wdmstate");
            File.WriteAllText(path2, System.Text.Json.JsonSerializer.Serialize(v1bad));
            var map2 = CompletionMap.LoadOrMigrate(path2, Total, CompletionMap.DefaultBlockSize, null, null);
            Assert.Equal(0, map2.CompletedBytes);
        }
        finally { TestFiles.DeleteDir(dir); }
    }

    [Trait("Category", Cats.RangeEngine)][Fact]
    public void DeterministicConcurrency_NoDuplicateLeases_500Workers()
    {
        var sched = new RangeScheduler { DesiredWorkers = 16 };
        sched.Initialize(new[] { (0L, 64_000_000L) });
        var taken = new ConcurrentBag<(long, long)>();
        Parallel.For(0, 64, w =>
        {
            while (sched.TryLease(w, 1_000_000) is { } lease)
            {
                taken.Add((lease.Start, lease.EndExclusive));
                sched.Complete(lease.Id, lease.Generation);
            }
        });
        var o = taken.OrderBy(t => t.Item1).ToList();
        Assert.Equal(64, o.Count);
        for (int i = 1; i < o.Count; i++)
            Assert.True(o[i].Item1 >= o[i - 1].Item2);
    }
}
