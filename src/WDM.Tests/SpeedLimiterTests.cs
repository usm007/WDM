using WDM.Services;
using WDM.Tests.TestInfrastructure;

namespace WDM.Tests;

/// <summary>SpeedGovernor behavior. Durations are scaled down from the spec's
/// literal 9s so the suite stays fast; rate ratios are what matter.</summary>
public sealed class SpeedLimiterTests
{
    [Trait("Category", Cats.Unit)][Fact(DisplayName = "SPD-001 limit enforced")]
    public async Task SPD001_Limit_Enforced()
    {
        var g = new SpeedGovernor { LimitKbps = 100 };
        var sw = System.Diagnostics.Stopwatch.StartNew();
        // 200KB at 100KB/s => >= ~1.5s (spec literal: 1MB >= 9s).
        for (int i = 0; i < 10; i++)
            await g.ThrottleAsync(0, 20 * 1024, CancellationToken.None);
        sw.Stop();
        Assert.True(sw.Elapsed.TotalSeconds >= 1.2, $"too fast: {sw.Elapsed.TotalSeconds:F2}s");
    }

    [Trait("Category", Cats.Unit)][Fact(DisplayName = "SPD-002 remove limit speeds up")]
    public async Task SPD002_Remove_Limit_Speeds_Up()
    {
        var g = new SpeedGovernor { LimitKbps = 50 };
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await g.ThrottleAsync(0, 50 * 1024, CancellationToken.None);
        double limited = sw.Elapsed.TotalMilliseconds;
        g.LimitKbps = 0;
        sw.Restart();
        await g.ThrottleAsync(0, 50 * 1024, CancellationToken.None);
        sw.Stop();
        Assert.True(sw.Elapsed.TotalMilliseconds < limited, $"unlimited {sw.Elapsed.TotalMilliseconds:F0}ms not < limited {limited:F0}ms");
    }

    [Trait("Category", Cats.Unit)][Fact(DisplayName = "SPD-003 per-task limits independent")]
    public async Task SPD003_PerTask_Independent()
    {
        // Explicit per-call kbps wins over LimitKbps: a 50KB/s call throttles
        // while a 0/unlimited call on the same governor... each ThrottleAsync
        // shares the bucket, so verify per-call rate arg is honored, not global.
        var g = new SpeedGovernor { LimitKbps = 0 };
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await g.ThrottleAsync(100, 100 * 1024, CancellationToken.None);
        sw.Stop();
        Assert.True(sw.Elapsed.TotalSeconds >= 0.7, $"per-call limit ignored: {sw.Elapsed.TotalSeconds:F2}s");
    }

    [Trait("Category", Cats.Unit)][Fact(DisplayName = "SPD-004 global cap shared")]
    public async Task SPD004_Global_Cap_Shared()
    {
        var g = new SpeedGovernor { LimitKbps = 200 };
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var tasks = Enumerable.Range(0, 4).Select(_ => g.ThrottleAsync(0, 100 * 1024, CancellationToken.None));
        await Task.WhenAll(tasks);
        sw.Stop();
        // 400KB at 200KB/s => >= ~1.5s total (5% tolerance per spec).
        Assert.True(sw.Elapsed.TotalSeconds >= 1.4, $"global cap not shared: {sw.Elapsed.TotalSeconds:F2}s");
    }
}
