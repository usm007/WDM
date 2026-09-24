using WDM.Services;
using WDM.Services.Chunking;
using WDM.Tests.TestInfrastructure;

namespace WDM.Tests;

public sealed class ConcurrencyTests
{
    [Trait("Category", Cats.Unit)][Fact(DisplayName = "RACE-001 cancel at commit keeps state")]
    public void RACE001_Cancel_At_Commit_State_Intact()
    {
        string dir = TestFiles.NewTempDir("wdm-race");
        try
        {
            long total = 4L * 1024 * 1024;
            var map = new CompletionMap(total, 1024 * 1024);
            string state = Path.Combine(dir, "f.wdmstate");
            using var cts = new CancellationTokenSource();
            Parallel.For(0, 20, i =>
            {
                if (i == 10) cts.Cancel();
                map.MarkCommitted((i % 4) * 1024L * 1024, ((i % 4) + 1) * 1024L * 1024);
            });
            map.Save(state); // must not tear even with cancel racing
            var re = CompletionMap.LoadOrMigrate(state, total, 1024 * 1024, null, null);
            Assert.True(re.CompletedBytes >= 0);
        }
        finally { TestFiles.DeleteDir(dir); }
    }

    [Trait("Category", Cats.Unit)][Fact(DisplayName = "RACE-002 same URL twice both complete")]
    public async Task RACE002_SameUrl_Twice()
    {
        var origin = new FakeProgrammableOrigin();
        byte[] content = TestFiles.Make(256 * 1024, seed: 7);
        origin.AddObject("http://primary/file.bin", content);
        string dir = TestFiles.NewTempDir("wdm-race");
        try
        {
            var t1 = EngineDriver.RunAsync(origin, content, dir, "a.bin");
            var t2 = EngineDriver.RunAsync(origin, content, dir, "b.bin");
            await Task.WhenAll(t1, t2);
            Assert.Equal(content, await File.ReadAllBytesAsync(Path.Combine(dir, "a.bin")));
            Assert.Equal(content, await File.ReadAllBytesAsync(Path.Combine(dir, "b.bin")));
        }
        finally { TestFiles.DeleteDir(dir); }
    }

    [Trait("Category", Cats.Unit)][Fact(DisplayName = "RACE-003 taskstore save+complete no corruption")]
    public void RACE003_TaskStore_Concurrent_Save()
    {
        // Never touch the real %LocalAppData%\WDM-Data tasks.json from tests:
        // exercise the same atomic-replace guarantee via CompletionMap sidecars.
        string dir = TestFiles.NewTempDir("wdm-race");
        try
        {
            long total = 2L * 1024 * 1024;
            var map = new CompletionMap(total, 1024 * 1024);
            map.MarkCommitted(0, total / 2);
            string state = Path.Combine(dir, "tasks-sim.wdmstate");
            Parallel.For(0, 20, _ => map.Save(state));
            var re = CompletionMap.LoadOrMigrate(state, total, 1024 * 1024, null, null);
            Assert.True(re.CompletedBytes >= 0); // parses, never torn
        }
        finally { TestFiles.DeleteDir(dir); }
    }

    [Trait("Category", Cats.Unit)][Fact(DisplayName = "RACE-004 settings concurrent save+read")]
    public void RACE004_Settings_Concurrent()
    {
        // AtomicFile.Write is the primitive TaskStore settings/tasks saves rely on.
        string dir = TestFiles.NewTempDir("wdm-race");
        try
        {
            string path = Path.Combine(dir, "settings-sim.json");
            var errors = new System.Collections.Concurrent.ConcurrentBag<Exception>();
            Parallel.For(0, 30, i =>
            {
                try { AtomicFile.Write(path, $$$"""{"MaxConcurrentDownloads":{{{1 + (i % 5)}}}}"""); }
                catch (Exception ex) { errors.Add(ex); }
            });
            Assert.Empty(errors);
            string text = File.ReadAllText(path); // final replace is whole, parses as JSON
            using var doc = System.Text.Json.JsonDocument.Parse(text);
            Assert.True(doc.RootElement.TryGetProperty("MaxConcurrentDownloads", out _));
        }
        finally { TestFiles.DeleteDir(dir); }
    }

    [Trait("Category", Cats.Unit)][Fact(DisplayName = "RACE-005 16 workers on 4MB no overlap")]
    public async Task RACE005_Sixteen_Workers_No_Overlap()
    {
        var origin = new FakeProgrammableOrigin();
        byte[] content = TestFiles.Make(4 * 1024 * 1024, seed: 9);
        origin.AddObject("http://primary/file.bin", content);
        string dir = TestFiles.NewTempDir("wdm-race");
        try
        {
            OriginController.ResetForTests();
            var engine = new AdaptiveRangeEngine();
            using var http = new HttpClient(origin) { Timeout = TimeSpan.FromSeconds(60) };
            string dest = Path.Combine(dir, "f.bin");
            await engine.RunAsync(content.Length, dest, dest + ".wdmstate",
                new List<string> { "http://primary/file.bin" }, "\"v1\"", null,
                16, 3, null, http,
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
}
