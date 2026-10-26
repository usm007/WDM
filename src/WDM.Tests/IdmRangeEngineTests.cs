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
    [Fact(DisplayName = "IDM-RANGE-007 a mirror with a lying Content-Range is demoted")]
    public async Task WrongContentRange_Mirror_Is_Demoted()
    {
        var origin = new FakeProgrammableOrigin();
        byte[] content = TestFiles.Make(2 * 1024 * 1024, seed: 17);
        origin.AddObject("http://primary/file.bin", content);
        origin.AddObject("http://mirror/file.bin", content);
        origin.FaultPicker = (request, _) =>
            request.RequestUri!.Host.Equals("primary", StringComparison.OrdinalIgnoreCase)
                ? FaultKind.WrongContentRange
                : FaultKind.None;
        string dir = TestFiles.NewTempDir("wdm-idm");
        try
        {
            await EngineDriver.RunAsync(origin, content, dir, "f.bin", workers: 2, maxRetries: 5,
                urls: new List<string> { "http://primary/file.bin", "http://mirror/file.bin" });

            Assert.Equal(content, await File.ReadAllBytesAsync(Path.Combine(dir, "f.bin")));
            Assert.Contains(origin.Requests, r => r.Status == 206);
        }
        finally { TestFiles.DeleteDir(dir); }
    }

    [Trait("Category", Cats.Unit)]
    [Fact(DisplayName = "IDM-RANGE-008 every mirror lying about the range fails loudly")]
    public async Task WrongContentRange_All_Mirrors_Fails_Loudly()
    {
        var origin = new FakeProgrammableOrigin();
        byte[] content = TestFiles.Make(512 * 1024, seed: 18);
        origin.AddObject("http://primary/file.bin", content);
        origin.FaultPicker = (_, _) => FaultKind.WrongContentRange;
        string dir = TestFiles.NewTempDir("wdm-idm");
        try
        {
            Exception ex = await Assert.ThrowsAnyAsync<Exception>(() =>
                EngineDriver.RunAsync(origin, content, dir, "f.bin", workers: 1, maxRetries: 2));

            Assert.Contains("range", ex.Message, StringComparison.OrdinalIgnoreCase);
            Assert.False(File.Exists(Path.Combine(dir, "f.bin")));
        }
        finally { TestFiles.DeleteDir(dir); }
    }

    [Trait("Category", Cats.Unit)]
    [Fact(DisplayName = "IDM-RANGE-009 a mirror that changes ETag is rejected before its bytes are written")]
    public async Task IdentityMismatch_Mirror_Is_Rejected()
    {
        var origin = new FakeProgrammableOrigin();
        byte[] content = TestFiles.Make(2 * 1024 * 1024, seed: 19);
        origin.AddObject("http://primary/file.bin", content, etag: "\"v1\"");
        origin.AddObject("http://mirror/file.bin", content, etag: "\"v2\"");
        string dir = TestFiles.NewTempDir("wdm-idm");
        try
        {
            await EngineDriver.RunAsync(origin, content, dir, "f.bin", workers: 2, maxRetries: 5,
                urls: new List<string> { "http://mirror/file.bin", "http://primary/file.bin" });

            Assert.Equal(content, await File.ReadAllBytesAsync(Path.Combine(dir, "f.bin")));
        }
        finally { TestFiles.DeleteDir(dir); }
    }

    [Trait("Category", Cats.Unit)]
    [Fact(DisplayName = "IDM-RANGE-010 a lying mirror cannot append to an existing part")]
    public async Task LyingMirror_Cannot_Append_To_Part()
    {
        var origin = new FakeProgrammableOrigin();
        byte[] content = TestFiles.Make(2 * 1024 * 1024, seed: 20);
        byte[] wrong = (byte[])content.Clone();
        wrong[0] ^= 0xFF;
        origin.AddObject("http://lying/file.bin", wrong);
        origin.AddObject("http://primary/file.bin", content);
        string dir = TestFiles.NewTempDir("wdm-idm");
        try
        {
            string dest = Path.Combine(dir, "f.bin");
            await File.WriteAllBytesAsync(dest + ".part0", content.AsSpan(0, 64 * 1024).ToArray());

            await EngineDriver.RunAsync(origin, content, dir, "f.bin", workers: 1, maxRetries: 5,
                urls: new List<string> { "http://lying/file.bin", "http://primary/file.bin" });

            Assert.Equal(content, await File.ReadAllBytesAsync(dest));
        }
        finally { TestFiles.DeleteDir(dir); }
    }

    [Trait("Category", Cats.Unit)]
    [Fact(DisplayName = "IDM-RANGE-011 no mirror may append to a part when every mirror lies")]
    public async Task LyingMirrors_Do_Not_Assemble_Corrupt_File()
    {
        var origin = new FakeProgrammableOrigin();
        byte[] content = TestFiles.Make(2 * 1024 * 1024, seed: 21);
        byte[] wrong1 = (byte[])content.Clone();
        byte[] wrong2 = (byte[])content.Clone();
        wrong1[0] ^= 0xFF;
        wrong2[0] ^= 0x0F;
        origin.AddObject("http://lying-a/file.bin", wrong1);
        origin.AddObject("http://lying-b/file.bin", wrong2);
        string dir = TestFiles.NewTempDir("wdm-idm");
        try
        {
            string dest = Path.Combine(dir, "f.bin");
            await File.WriteAllBytesAsync(dest + ".part0", content.AsSpan(0, 64 * 1024).ToArray());

            Exception ex = await Assert.ThrowsAnyAsync<Exception>(() =>
                EngineDriver.RunAsync(origin, content, dir, "f.bin", workers: 1, maxRetries: 5,
                    urls: new List<string> { "http://lying-a/file.bin", "http://lying-b/file.bin" }));

            Assert.Contains("different bytes", ex.Message, StringComparison.OrdinalIgnoreCase);
            Assert.False(File.Exists(dest));
        }
        finally { TestFiles.DeleteDir(dir); }
    }

    [Trait("Category", Cats.Unit)]
    [Fact(DisplayName = "IDM-RANGE-012 range attempts feed the per-origin pressure budget")]
    public async Task RangeAttempts_Update_OriginPressure()
    {
        var origin = new FakeProgrammableOrigin();
        byte[] content = TestFiles.Make(1024 * 1024, seed: 22);
        const string source = "http://pressure-idm.test/file.bin";
        origin.AddObject(source, content);
        int seen = 0;
        origin.StatusOverride = (_, _) =>
            Interlocked.Increment(ref seen) <= 2 ? HttpStatusCode.TooManyRequests : null;
        string dir = TestFiles.NewTempDir("wdm-idm");
        try
        {
            await EngineDriver.RunAsync(origin, content, dir, "f.bin", workers: 2, maxRetries: 6,
                urls: new List<string> { source });

            Assert.Equal(content, await File.ReadAllBytesAsync(Path.Combine(dir, "f.bin")));
            Assert.True(seen > 2, $"expected retries after 429, saw {seen} requests");
            Assert.True(OriginController.GetPressure(OriginController.KeyOf(source)).Budget < 16,
                "a 429 must reduce the learned origin budget");
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
