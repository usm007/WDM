using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using WDM.Services.Chunking;
using WDM.Tests.TestInfrastructure;
using Xunit;

namespace WDM.Tests;

/// <summary>Coverage for the IDM range engine (Redm.Core segmentation plus
/// part-file resume) that replaced the adaptive range scheduler.</summary>
public sealed class IdmRangeEngineTests
{
    [Trait("Category", Cats.Unit)]
    [Fact(DisplayName = "IDM-RANGE-001 multi-part transfer reassembles the source exactly")]
    public async Task MultiPartTransfer_ReassemblesSource()
    {
        var origin = new FakeProgrammableOrigin();
        byte[] content = TestFiles.Make(4 * 1024 * 1024, seed: 11);
        origin.AddObject("http://primary/file.bin", content);
        string dir = TestFiles.NewTempDir("wdm-idm");
        try
        {
            var engine = await EngineDriver.RunAsync(origin, content, dir, "f.bin", workers: 4);

            Assert.Equal(content, await File.ReadAllBytesAsync(Path.Combine(dir, "f.bin")));
            Assert.Equal(4, engine.ProgressFractions().Length);
            Assert.Equal(0, engine.CompletedBytesBaseline);
            Assert.Empty(Directory.GetFiles(dir, "*.part*"));
            Assert.Empty(Directory.GetFiles(dir, "*.wdmstate"));
        }
        finally { TestFiles.DeleteDir(dir); }
    }

    [Trait("Category", Cats.Unit)]
    [Fact(DisplayName = "IDM-RANGE-002 a finished part file counts as resume progress")]
    public async Task FinishedPart_CountsAsProgress()
    {
        var origin = new FakeProgrammableOrigin();
        byte[] content = TestFiles.Make(4 * 1024 * 1024, seed: 12);
        origin.AddObject("http://primary/file.bin", content);
        string dir = TestFiles.NewTempDir("wdm-idm");
        try
        {
            // The IDM plan splits four ways: part 0 owns the first quarter.
            var plan = Redm.Core.Engine.Plan(content.Length, 4);
            long part0 = plan[0].End - plan[0].Start + 1;
            string dest = Path.Combine(dir, "f.bin");
            await File.WriteAllBytesAsync(dest + ".part0", content.AsSpan(0, (int)part0).ToArray());

            var engine = await EngineDriver.RunAsync(origin, content, dir, "f.bin", workers: 4);

            Assert.Equal(content, await File.ReadAllBytesAsync(dest));
            Assert.Equal(part0, engine.CompletedBytesBaseline);
            Assert.Empty(Directory.GetFiles(dir, "*.part*"));
        }
        finally { TestFiles.DeleteDir(dir); }
    }

    [Trait("Category", Cats.Unit)]
    [Fact(DisplayName = "IDM-RANGE-003 transient server failures are retried")]
    public async Task TransientFailures_AreRetried()
    {
        var origin = new FakeProgrammableOrigin();
        byte[] content = TestFiles.Make(1024 * 1024, seed: 13);
        origin.AddObject("http://primary/file.bin", content);
        int seen = 0;
        origin.StatusOverride = (_, _) =>
            Interlocked.Increment(ref seen) <= 3 ? HttpStatusCode.ServiceUnavailable : null;
        string dir = TestFiles.NewTempDir("wdm-idm");
        try
        {
            await EngineDriver.RunAsync(origin, content, dir, "f.bin", workers: 2, maxRetries: 6);

            Assert.Equal(content, await File.ReadAllBytesAsync(Path.Combine(dir, "f.bin")));
            Assert.True(seen > 3, $"expected retries, saw {seen} requests");
        }
        finally { TestFiles.DeleteDir(dir); }
    }

    [Trait("Category", Cats.Unit)]
    [Fact(DisplayName = "IDM-RANGE-004 a connection dropped mid-body resumes the part file")]
    public async Task DroppedConnection_Resumes()
    {
        var origin = new FakeProgrammableOrigin();
        byte[] content = TestFiles.Make(2 * 1024 * 1024, seed: 14);
        origin.AddObject("http://primary/file.bin", content);
        int seen = 0;
        origin.FaultPicker = (_, _) =>
            Interlocked.Increment(ref seen) <= 2 ? FaultKind.ResetAfterBytes : FaultKind.None;
        string dir = TestFiles.NewTempDir("wdm-idm");
        try
        {
            await EngineDriver.RunAsync(origin, content, dir, "f.bin", workers: 2, maxRetries: 5);

            Assert.Equal(content, await File.ReadAllBytesAsync(Path.Combine(dir, "f.bin")));
            Assert.True(origin.RequestCount > 2, $"expected a resumed request, saw {origin.RequestCount}");
            Assert.Empty(Directory.GetFiles(dir, "*.part*"));
        }
        finally { TestFiles.DeleteDir(dir); }
    }

    [Trait("Category", Cats.Unit)]
    [Fact(DisplayName = "IDM-RANGE-005 cancelling stops before assembly and keeps part files")]
    public async Task Cancel_KeepsPartFiles()
    {
        var origin = new FakeProgrammableOrigin();
        byte[] content = TestFiles.Make(32 * 1024 * 1024, seed: 15);
        origin.AddObject("http://primary/file.bin", content);
        string dir = TestFiles.NewTempDir("wdm-idm");
        try
        {
            using var cts = new CancellationTokenSource();
            long total = 0;
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                EngineDriver.RunAsync(origin, content, dir, "f.bin", workers: 4, ct: cts.Token,
                    onBytes: n =>
                    {
                        if (Interlocked.Add(ref total, n) > 2 * 1024 * 1024)
                            cts.Cancel();
                    }));

            Assert.False(File.Exists(Path.Combine(dir, "f.bin")));
            Assert.NotEmpty(Directory.GetFiles(dir, "*.part*"));
        }
        finally { TestFiles.DeleteDir(dir); }
    }

    [Trait("Category", Cats.Unit)]
    [Fact(DisplayName = "IDM-RANGE-006 an unreachable source falls back to its mirror")]
    public async Task UnreachableSource_FallsBackToMirror()
    {
        var origin = new FakeProgrammableOrigin();
        byte[] content = TestFiles.Make(1024 * 1024, seed: 16);
        origin.AddObject("http://mirror/file.bin", content);
        string dir = TestFiles.NewTempDir("wdm-idm");
        try
        {
            string dest = Path.Combine(dir, "f.bin");
            var engine = new IdmRangeEngine();
            using var http = new HttpClient(origin) { Timeout = TimeSpan.FromSeconds(60) };
            await engine.RunAsync(content.Length, dest, dest + ".wdmstate",
                new List<string> { "http://primary/file.bin", "http://mirror/file.bin" }, "\"v1\"", null,
                2, 5, null, http,
                (method, range, url) =>
                {
                    var req = new HttpRequestMessage(method, url);
                    if (range is not null) req.Headers.Range = range;
                    return req;
                },
                (resp, url) => null,
                (bytes, tok) => Task.CompletedTask,
                _ => { }, _ => { }, CancellationToken.None);

            Assert.Equal(content, await File.ReadAllBytesAsync(dest));
        }
        finally { TestFiles.DeleteDir(dir); }
    }

    [Trait("Category", Cats.Unit)]
    [Fact(DisplayName = "IDM-CORE-001 the IDM plan splits into at most eight equal parts")]
    public void IdmPlan_SplitsEvenly()
    {
        var plan = Redm.Core.Engine.Plan(1000, 4);
        Assert.Equal(4, plan.Count);
        Assert.Equal(0, plan[0].Start);
        Assert.Equal(249, plan[0].End);
        Assert.Equal(750, plan[3].Start);
        Assert.Equal(999, plan[3].End);
        Assert.Equal("bytes=100-249", Redm.Core.Engine.RangeHeader(100, 249));
        Assert.Equal("bytes=100-", Redm.Core.Engine.RangeHeader(100, -1));
        Assert.Single(Redm.Core.Engine.Plan(1000, 1));
        Assert.Equal(8, Redm.Core.Engine.Plan(1000, 64).Count);
        Assert.Equal(-1, Redm.Core.Engine.Plan(null, 4)[0].End);
    }
}
