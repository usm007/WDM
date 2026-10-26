using WDM.Services;
using WDM.Tests.TestInfrastructure;

namespace WDM.Tests;

/// <summary>Updater hardening: the update paths must fail closed. A Velopack
/// install must never be pointed at the per-machine Inno wizard, a truncated
/// installer body must never be launched, an installer that exits immediately
/// must be reported instead of closing WDM, and a future "last checked" stamp
/// (clock skew) must not suppress checks forever.</summary>
public sealed class UpdateHardeningTests : IDisposable
{
    private readonly List<string> _dirs = new();

    private string NewDir()
    {
        string d = Path.Combine(Path.GetTempPath(), "wdm-update-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        _dirs.Add(d);
        return d;
    }

    public void Dispose()
    {
        foreach (var d in _dirs)
            try { Directory.Delete(d, true); } catch { }
    }

    private static ReleaseInfo SampleRelease(string? installerUrl = "https://github.com/usm007/WDM/releases/download/v2.8.12/WDM-Setup-2.8.12.exe") =>
        new("v2.8.12", new Version(2, 8, 12), "WDM 2.8.12", "https://github.com/usm007/WDM/releases/tag/v2.8.12",
            "notes", DateTime.UtcNow, installerUrl, "https://github.com/usm007/WDM/releases/download/v2.8.12/WDM-2.8.12-delta.nupkg", null);

    [Trait("Category", Cats.Unit)][Fact]
    public void AutoCheck_FutureTimestampIsStale_NotSuppressed()
    {
        var now = new DateTime(2026, 10, 26, 12, 0, 0, DateTimeKind.Utc);

        Assert.False(UpdateChecker.ShouldSkipAutoCheck(now.AddHours(1), now));
        Assert.False(UpdateChecker.ShouldSkipAutoCheck(now.AddDays(400), now));
        Assert.False(UpdateChecker.ShouldSkipAutoCheck(now.AddMinutes(-20), now));
        Assert.False(UpdateChecker.ShouldSkipAutoCheck(null, now));
        Assert.True(UpdateChecker.ShouldSkipAutoCheck(now.AddMinutes(-5), now));
    }

    [Trait("Category", Cats.Unit)][Theory]
    [InlineData(-1, "Downloading…")]
    [InlineData(0, "0%")]
    [InlineData(0.423, "42%")]
    [InlineData(1, "100%")]
    public void DownloadProgress_UnknownSizeIsIndeterminate(double progress, string expected)
    {
        Assert.Equal(expected, UpdateChecker.FormatDownloadProgress(progress));
    }

    [Trait("Category", Cats.Unit)][Theory]
    [InlineData("https://github.com/usm007/WDM/releases/download/v1/x.exe", true)]
    [InlineData("https://objects.githubusercontent.com/abc", true)]
    [InlineData("https://release-assets.githubusercontent.com/abc", true)]
    [InlineData("https://api.github.com/repos/x/y", true)]
    [InlineData("http://github.com/usm007/WDM/x.exe", false)]
    [InlineData("https://github.com.evil.test/x.exe", false)]
    [InlineData("https://example.com/x.exe", false)]
    public void TrustedDownloadUrl_OnlyAllowsGithubHostsOverHttps(string url, bool expected)
    {
        Assert.Equal(expected, UpdateChecker.IsTrustedDownloadUrl(url));
    }

    [Trait("Category", Cats.Unit)][Fact]
    public async Task InstallerBody_TruncatedBelowDeclaredLength_ThrowsAndDeletesTarget()
    {
        string dir = NewDir();
        string target = Path.Combine(dir, "WDM-Setup-test.exe");
        using var source = new MemoryStream(new byte[2048]);

        var ex = await Assert.ThrowsAsync<UpdateException>(
            () => UpdateChecker.CopyInstallerBodyAsync(source, target, declaredTotal: 4096, null, CancellationToken.None));

        Assert.Contains("incomplete", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(target));
    }

    [Trait("Category", Cats.Unit)][Fact]
    public async Task InstallerBody_ExactDeclaredLength_WritesEveryByte()
    {
        string dir = NewDir();
        string target = Path.Combine(dir, "WDM-Setup-test.exe");
        byte[] body = TestFiles.Make(4096, seed: 9);
        using var source = new MemoryStream(body);

        await UpdateChecker.CopyInstallerBodyAsync(source, target, declaredTotal: body.Length, null, CancellationToken.None);

        Assert.Equal(body, await File.ReadAllBytesAsync(target));
    }

    [Trait("Category", Cats.Unit)][Fact]
    public async Task InstallerBody_UnknownLength_SucceedsAndReportsIndeterminateProgress()
    {
        string dir = NewDir();
        string target = Path.Combine(dir, "WDM-Setup-test.exe");
        byte[] body = TestFiles.Make(2048, seed: 3);
        using var source = new MemoryStream(body);
        var reported = new List<double>();

        await UpdateChecker.CopyInstallerBodyAsync(source, target, declaredTotal: -1, reported.Add, CancellationToken.None);

        Assert.Equal(body, await File.ReadAllBytesAsync(target));
        Assert.Contains(-1, reported);
    }

    [Trait("Category", Cats.Unit)][Fact]
    public void StaleInstallerCleanup_DeletesOnlyOldWdmSetupFiles()
    {
        string dir = NewDir();
        var now = new DateTime(2026, 10, 26, 12, 0, 0, DateTimeKind.Utc);
        string oldSetup = Path.Combine(dir, "WDM-Setup-2.8.11_old.exe");
        string newSetup = Path.Combine(dir, "WDM-Setup-2.8.12_new.exe");
        string oldOther = Path.Combine(dir, "other.exe");
        foreach (var f in new[] { oldSetup, newSetup, oldOther })
            File.WriteAllBytes(f, new byte[8]);
        File.SetLastWriteTimeUtc(oldSetup, now.AddDays(-3));
        File.SetLastWriteTimeUtc(oldOther, now.AddDays(-3));
        File.SetLastWriteTimeUtc(newSetup, now.AddMinutes(-1));

        int deleted = UpdateChecker.CleanStaleInstallerDownloads(dir, now, TimeSpan.FromDays(1));

        Assert.Equal(1, deleted);
        Assert.False(File.Exists(oldSetup));
        Assert.True(File.Exists(newSetup));
        Assert.True(File.Exists(oldOther));
    }

    [Trait("Category", Cats.Unit)][Theory]
    [InlineData("WDM-Setup-2.8.11.exe", "2.8.11", true)]
    [InlineData("WDM-Setup-2.8.10.exe", "2.8.11", false)]
    [InlineData("WDM_Setup_2.8.5.10.exe", "2.8.5", true)]
    [InlineData("WDM-Setup-2.8.11.0.exe", "2.8.11", true)]
    [InlineData("WDM-win-Setup.exe", "2.8.11", true)]
    [InlineData("WDM-Setup.exe", "2.8.11", true)]
    public void InstallerAssetVersion_MustMatchReleaseTag(string assetName, string tagVersion, bool expected)
    {
        Assert.Equal(expected, UpdateChecker.IsInstallerAssetCompatible(assetName, Version.Parse(tagVersion)));
    }

    [Trait("Category", Cats.Unit)][Fact]
    public void VelopackInstall_NeverOffersGithubInstaller()
    {
        var release = SampleRelease(installerUrl: "https://github.com/usm007/WDM/releases/download/v2.8.12/WDM-Setup-2.8.12.exe");

        var forVelopack = UpdateChecker.ForHostFlavor(velopackInstalled: true, release);
        Assert.Null(forVelopack.InstallerUrl);
        Assert.Equal(release.UpdatePackageUrl, forVelopack.UpdatePackageUrl);
        Assert.Equal(release.Version, forVelopack.Version);

        var forInno = UpdateChecker.ForHostFlavor(velopackInstalled: false, release);
        Assert.Equal(release.InstallerUrl, forInno.InstallerUrl);
    }

    [Trait("Category", Cats.Unit)][Fact]
    public void UpdateFailure_SurfacesActionableMessage_ButNotRawTransportErrors()
    {
        Assert.Equal("Update feed mismatch", UpdateChecker.DescribeUpdateFailure(new UpdateException("Update feed mismatch")));
        Assert.Null(UpdateChecker.DescribeUpdateFailure(new HttpRequestException("connection reset")));
        Assert.Null(UpdateChecker.DescribeUpdateFailure(new TaskCanceledException("timeout")));
    }

    [Trait("Category", Cats.Unit)][Fact]
    public void FeedCheckScratchDirs_AreUniquePerCall()
    {
        string a = VelopackUpdateService.NewIsolatedCheckDir();
        string b = VelopackUpdateService.NewIsolatedCheckDir();

        Assert.NotEqual(a, b);
        Assert.Contains("WDM_Velopack_Check_", Path.GetFileName(a));
        string temp = Path.GetFullPath(Path.GetTempPath());
        Assert.StartsWith(temp, Path.GetFullPath(a), StringComparison.OrdinalIgnoreCase);
    }

    [Trait("Category", Cats.Integration)][Fact]
    public async Task InstallerLaunch_ImmediateNonZeroExit_IsReportedAsFailed()
    {
        string src = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "where.exe");
        Assert.True(File.Exists(src), "where.exe is required for the early-exit process test.");
        string target = Path.Combine(Path.GetTempPath(), $"WDM-User-Setup-{Guid.NewGuid():N}.exe");
        File.Copy(src, target);
        try
        {
            var result = await UpdateChecker.LaunchInstallerAndWaitForStart(target, timeoutMs: 3000);

            Assert.Equal(InstallerLaunchStatus.Failed, result.Status);
            Assert.Equal(1, result.ExitCode);
        }
        finally
        {
            try { File.Delete(target); } catch { }
        }
    }
}
