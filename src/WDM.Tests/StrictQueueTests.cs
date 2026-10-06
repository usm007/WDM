using System.Text.Json;
using WDM.Models;
using WDM.Services;
using WDM.Tests.TestInfrastructure;
using TaskStatus = WDM.Models.TaskStatus;

namespace WDM.Tests;

/// <summary>"Add to Queue" strict-sequential downloads: a WaitForIdle task
/// starts only at total idle, overriding MaxConcurrentDownloads. Local file
/// URLs keep the test offline (RunLocalCopyAsync, no TCP).</summary>
public sealed class StrictQueueTests
{
    private static string TempDir(string tag)
    {
        string d = Path.Combine(Path.GetTempPath(), "wdm-strictq-" + tag, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        Directory.CreateDirectory(Path.Combine(d, "src"));
        return d;
    }

    private static string WriteSource(string dir, string name, int bytes)
    {
        string path = Path.Combine(dir, "src", name);
        var data = new byte[bytes];
        for (int i = 0; i < data.Length; i++) data[i] = (byte)(i & 0xFF);
        File.WriteAllBytes(path, data);
        return path;
    }

    private static DownloadTask FileTask(string srcPath, string dir, string fileName, bool waitForIdle = false)
    {
        return new DownloadTask
        {
            Url = new Uri(srcPath).AbsoluteUri,
            FileName = fileName,
            SaveFolder = dir,
            WaitForIdle = waitForIdle,
        };
    }

    private static async Task WaitForAsync(Func<bool> done, TimeSpan budget)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!done() && sw.Elapsed < budget)
            await Task.Delay(50);
        Assert.True(done(), "timed out waiting for condition");
    }

    [Trait("Category", Cats.Unit)][Fact]
    public async Task StrictTask_WaitsForIdle_DespiteFreeSlots()
    {
        string dir = TempDir("wait");
        try
        {
            string srcA = WriteSource(dir, "a.bin", 4_000_000);
            string srcB = WriteSource(dir, "b.bin", 1_000);
            var engine = new DownloadEngine { MaxConcurrent = 5 };
            var a = FileTask(srcA, dir, "a.bin");
            var b = FileTask(srcB, dir, "b.bin", waitForIdle: true);

            engine.Start(a);
            engine.Start(b);

            // A free slot exists (1 of 5 used) but the strict task must queue.
            Assert.Equal(TaskStatus.Queued, b.Status);
            Assert.Equal(1, engine.QueuedCount);

            // When A finishes, B starts on its own and completes.
            await WaitForAsync(() =>
                a.Status == TaskStatus.Completed && b.Status == TaskStatus.Completed,
                TimeSpan.FromSeconds(30));
            Assert.Equal(new FileInfo(Path.Combine(dir, "a.bin")).Length, 4_000_000);
        }
        finally { TestFiles.DeleteDir(dir); }
    }

    [Trait("Category", Cats.Unit)][Fact]
    public async Task StrictTask_StartsImmediately_WhenIdle()
    {
        string dir = TempDir("idle");
        try
        {
            string src = WriteSource(dir, "s.bin", 1_000);
            var engine = new DownloadEngine { MaxConcurrent = 1 };
            var task = FileTask(src, dir, "s.bin", waitForIdle: true);

            engine.Start(task);

            Assert.Equal(TaskStatus.Downloading, task.Status);
            await WaitForAsync(() => task.Status == TaskStatus.Completed, TimeSpan.FromSeconds(30));
        }
        finally { TestFiles.DeleteDir(dir); }
    }

    [Trait("Category", Cats.Unit)][Fact]
    public void WaitForIdle_SurvivesTaskRecordRoundTrip()
    {
        var options = new JsonSerializerOptions();
        var record = new TaskRecord { FileName = "x.bin", Url = "https://example.com/x", WaitForIdle = true };
        var back = JsonSerializer.Deserialize<TaskRecord>(JsonSerializer.Serialize(record, options), options)!;
        Assert.True(back.WaitForIdle);

        var legacy = JsonSerializer.Deserialize<TaskRecord>("{}", options)!;
        Assert.False(legacy.WaitForIdle);
    }
}
