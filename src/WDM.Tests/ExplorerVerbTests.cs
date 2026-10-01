// ExplorerVerbTests — right-click "Download with WDM": CLI intake turns an
// existing file into a file:// URL, and the engine imports it as a managed
// local copy (byte-exact, resumable via append).
using WDM;
using WDM.Models;
using WDM.Services;
using WDM.Tests.TestInfrastructure;
using TaskStatus = WDM.Models.TaskStatus;

namespace WDM.Tests;

public sealed class ExplorerVerbTests : IDisposable
{
    private readonly DownloadEngine _engine = new();
    private readonly List<string> _dirs = new();

    public void Dispose()
    {
        try { _engine.PauseAll(); } catch { }
        foreach (var d in _dirs)
            try { Directory.Delete(d, true); } catch { }
    }

    private string NewDir(string prefix)
    {
        string d = Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        _dirs.Add(d);
        return d;
    }

    private static async Task WaitForIdle(DownloadTask task, int timeoutMs = 30000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (task.Status is TaskStatus.Completed or TaskStatus.Failed or TaskStatus.Paused)
                return;
            await Task.Delay(50);
        }
    }

    [Trait("Category", Cats.Unit)][Fact]
    public void FirstDownloadLink_Resolves_File_And_Http()
    {
        string dir = NewDir("wdm-verb-");
        string f = Path.Combine(dir, "doc.pdf");
        File.WriteAllBytes(f, new byte[] { 1, 2, 3 });
        string? link = App.FirstDownloadLink("\"" + f + "\"");
        Assert.NotNull(link);
        Assert.StartsWith("file:///", link);
        Assert.Equal("https://a.example.com/x", App.FirstDownloadLink("https://a.example.com/x"));
        Assert.Null(App.FirstDownloadLink(Path.Combine(dir, "missing.bin")));
        Assert.False(App.ArgsContainUrl(new[] { "/minimized" }));
        Assert.True(App.ArgsContainUrl(new[] { f }));
    }

    [Trait("Category", Cats.E2E)][Fact]
    public async Task LocalCopy_ByteExact_With_Progress_Size()
    {
        string srcDir = NewDir("wdm-verbsrc-");
        string dstDir = NewDir("wdm-verbdst-");
        byte[] content = TestFiles.Make(512 * 1024, seed: 5);
        string src = Path.Combine(srcDir, "source.dat");
        await File.WriteAllBytesAsync(src, content);
        var task = new DownloadTask
        {
            Url = new Uri(src).AbsoluteUri,
            FileName = "copy.dat",
            SaveFolder = dstDir,
        };
        _engine.Start(task);
        await WaitForIdle(task);
        Assert.Equal(TaskStatus.Completed, task.Status);
        Assert.Equal(content, await File.ReadAllBytesAsync(Path.Combine(dstDir, "copy.dat")));
        Assert.Equal(content.Length, task.TotalBytes);
    }

    [Trait("Category", Cats.E2E)][Fact]
    public async Task LocalCopy_Resumes_Append()
    {
        string srcDir = NewDir("wdm-verbsrc-");
        string dstDir = NewDir("wdm-verbdst-");
        byte[] content = TestFiles.Make(256 * 1024, seed: 6);
        string src = Path.Combine(srcDir, "source.dat");
        await File.WriteAllBytesAsync(src, content);
        // Simulate an interrupted copy: first half already on disk.
        byte[] half = new byte[content.Length / 2];
        Array.Copy(content, half, half.Length);
        await File.WriteAllBytesAsync(Path.Combine(dstDir, "copy.dat"), half);
        var task = new DownloadTask
        {
            Url = new Uri(src).AbsoluteUri,
            FileName = "copy.dat",
            SaveFolder = dstDir,
        };
        _engine.Start(task);
        await WaitForIdle(task);
        Assert.Equal(TaskStatus.Completed, task.Status);
        Assert.Equal(content, await File.ReadAllBytesAsync(Path.Combine(dstDir, "copy.dat")));
    }

    [Trait("Category", Cats.E2E)][Fact]
    public async Task LocalCopy_Missing_Source_Fails_Cleanly()
    {
        string dstDir = NewDir("wdm-verbdst-");
        var task = new DownloadTask
        {
            Url = new Uri(Path.Combine(dstDir, "nope.dat")).AbsoluteUri,
            FileName = "nope.dat",
            SaveFolder = dstDir,
        };
        _engine.Start(task);
        await WaitForIdle(task);
        Assert.Equal(TaskStatus.Failed, task.Status);
    }
}
