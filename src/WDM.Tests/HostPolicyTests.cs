// HostPolicyTests — per-host connection memory: user caps, learned
// cooldowns persisted across restarts, auto-degrade on integrity faults.
// OriginController is process-static: every test resets it and clears the
// persist hook afterwards so nothing leaks between tests.
using WDM.Services.Chunking;
using WDM.Services;
using WDM.Tests.TestInfrastructure;

namespace WDM.Tests;

public sealed class HostPolicyTests : IDisposable
{
    private const string Origin = "https://fragile.test:443";

    public HostPolicyTests() => OriginController.ResetForTests();
    public void Dispose()
    {
        OriginController.ResetForTests();
        OriginController.CooldownPersisted = null;
    }

    [Trait("Category", Cats.Unit)][Fact]
    public void UserCap_Clamps_Budget()
    {
        OriginController.ApplyPolicy(
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { [Origin] = 1 }, null);
        Assert.Equal(1, OriginController.GetPressure(Origin).Budget);
    }

    [Trait("Category", Cats.Unit)][Fact]
    public void UserCap_Clamped_To_Valid_Range()
    {
        for (int i = 0; i < 20; i++)
            OriginController.ReportResult(Origin, HttpOutcome.Success);
        OriginController.ApplyPolicy(
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { [Origin] = 999 }, null);
        Assert.Equal(32, OriginController.GetPressure(Origin).Budget);
    }

    [Trait("Category", Cats.Unit)][Fact]
    public void IntegrityFaults_Degrade_To_Single_And_Persist()
    {
        var persisted = new List<(string origin, long until, string cause)>();
        OriginController.CooldownPersisted += (o, u, c) => persisted.Add((o, u, c));
        OriginController.ReportIntegrityFault(Origin, "content-mismatch");
        OriginController.ReportIntegrityFault(Origin, "content-mismatch");
        Assert.NotEqual(1, OriginController.GetPressure(Origin).Budget);
        OriginController.ReportIntegrityFault(Origin, "content-mismatch");
        Assert.Equal(1, OriginController.GetPressure(Origin).Budget);
        Assert.Contains(persisted, p => p.origin == Origin && p.cause == "content-mismatch" && p.until > 0);
    }

    [Trait("Category", Cats.Unit)][Fact]
    public void Success_Resets_Fault_Streak()
    {
        OriginController.CooldownPersisted += (_, _, _) => { };
        OriginController.ReportIntegrityFault(Origin, "wrong-range");
        OriginController.ReportIntegrityFault(Origin, "wrong-range");
        OriginController.ReportResult(Origin, HttpOutcome.Success);
        OriginController.ReportIntegrityFault(Origin, "wrong-range");
        OriginController.ReportIntegrityFault(Origin, "wrong-range");
        Assert.NotEqual(1, OriginController.GetPressure(Origin).Budget);
    }

    [Trait("Category", Cats.Unit)][Fact]
    public void ExpiredCooldown_Ignored_Future_Rearmed()
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        OriginController.ApplyPolicy(null, new Dictionary<string, AppSettings.HostCooldown>(StringComparer.OrdinalIgnoreCase)
        {
            ["https://old.test:443"] = new AppSettings.HostCooldown { UntilUnix = now - 60, Cause = "429" },
        });
        Assert.Equal(TimeSpan.Zero, OriginController.GetPressure("https://old.test:443").BackoffRemaining);
        OriginController.ApplyPolicy(null, new Dictionary<string, AppSettings.HostCooldown>(StringComparer.OrdinalIgnoreCase)
        {
            [Origin] = new AppSettings.HostCooldown { UntilUnix = now + 3600, Cause = "429" },
        });
        Assert.True(OriginController.GetPressure(Origin).BackoffRemaining > TimeSpan.Zero);
    }

    [Trait("Category", Cats.Unit)][Fact]
    public void PressureOutcome_Persists_Cooldown()
    {
        // Other collections exercise OriginController concurrently: only
        // record verdicts for this test's origin.
        (string o, long u, string c)? seen = null;
        OriginController.CooldownPersisted += (o, u, c) => { if (o == Origin) seen = (o, u, c); };
        long t0 = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        OriginController.ReportResult(Origin, HttpOutcome.TooManyRequests);
        Assert.NotNull(seen);
        Assert.Equal(Origin, seen!.Value.o);
        Assert.Equal("429", seen!.Value.c);
        // Unix-second truncation makes strict > flaky at second boundaries;
        // the backoff math guarantees until >= invocation second, <= +30s.
        Assert.True(seen!.Value.u >= t0);
        Assert.True(seen!.Value.u <= t0 + 30);
    }
}
