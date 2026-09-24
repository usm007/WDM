using System.Diagnostics;
using WDM.Services;
using WDM.Tests.TestInfrastructure;

namespace WDM.Tests;

public sealed class YtDlpRunnerTests : IDisposable
{
    public void Dispose() => YtDlpRunner.RunnerOverride = null;

    [Trait("Category", Cats.Unit)][Fact]
    public void CreateInfo_Has_NoPlaylist_And_Template_Args()
    {
        var psi = YtDlpRunner.CreateInfo(new[] { "--dump-single-json", "http://x" });
        Assert.False(psi.UseShellExecute);
        Assert.True(psi.RedirectStandardOutput);
        Assert.Contains("--dump-single-json", psi.ArgumentList);
    }

    [Trait("Category", Cats.Unit)][Fact]
    public async Task RunJsonAsync_Override_Returns_Stdout()
    {
        YtDlpRunner.RunnerOverride = _ => Task.FromResult(new ProcessRunResult(0, """{"id":"abc"}""", ""));
        string json = await YtDlpRunner.RunJsonAsync("http://x/v", CancellationToken.None);
        Assert.Contains("abc", json);
    }

    [Trait("Category", Cats.Unit)][Fact]
    public async Task RunJsonAsync_NonZero_Throws_With_Stderr()
    {
        YtDlpRunner.RunnerOverride = _ => Task.FromResult(new ProcessRunResult(1, "", "ERROR: Video unavailable\nsecond line"));
        var ex = await Assert.ThrowsAsync<YtDlpException>(() => YtDlpRunner.RunJsonAsync("http://x/v", CancellationToken.None));
        Assert.Contains("Video unavailable", ex.Message);
    }

    [Trait("Category", Cats.Unit)][Fact]
    public async Task RunJsonAsync_EmptyStdout_Throws()
    {
        YtDlpRunner.RunnerOverride = _ => Task.FromResult(new ProcessRunResult(0, "   ", ""));
        await Assert.ThrowsAsync<YtDlpException>(() => YtDlpRunner.RunJsonAsync("http://x/v", CancellationToken.None));
    }

    [Trait("Category", Cats.Unit)][Fact]
    public async Task RunJsonAsync_Sees_psi_Args()
    {
        ProcessStartInfo? seen = null;
        YtDlpRunner.RunnerOverride = psi => { seen = psi; return Task.FromResult(new ProcessRunResult(0, "{}", "")); };
        await YtDlpRunner.RunJsonAsync("http://x/v", CancellationToken.None);
        Assert.NotNull(seen);
        Assert.Contains("--dump-single-json", seen!.ArgumentList);
    }
}
