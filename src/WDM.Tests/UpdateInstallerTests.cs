using System;
using System.IO;
using WDM.Services;
using WDM.Tests.TestInfrastructure;

namespace WDM.Tests;

/// <summary>Update installer routing: Velopack one-click takes clap-style
/// --silent with no elevation; the all-users Inno wizard takes /SILENT and
/// must elevate. A wrong combination silently fails the update.</summary>
public sealed class UpdateInstallerTests
{
    [Trait("Category", Cats.Unit)][Theory]
    [InlineData("WDM-User-Setup-2.8.10.exe", true)]
    [InlineData("WDM-Full-Setup-2.8.5.exe", true)]
    [InlineData("WDM-Velopack-Setup-2.8.10.exe", true)]
    [InlineData("WDM-win-Setup.exe", true)]
    [InlineData("wdm-user-setup-2.8.10.exe", true)]
    [InlineData("WDM-Setup-2.8.10.exe", false)]
    [InlineData("WDM_Setup_2.8.5.10.exe", false)]
    public void IsVelopackSetup_ClassifiesByName(string fileName, bool expected)
    {
        Assert.Equal(expected, UpdateChecker.IsVelopackSetup(@"C:\Temp\" + fileName));
    }

    [Trait("Category", Cats.Unit)][Theory]
    [InlineData("WDM-User-Setup-2.8.10.exe", true, "--silent")]
    [InlineData("WDM-Full-Setup-2.8.5.exe", true, "--silent")]
    [InlineData("WDM-Setup-2.8.10.exe", true, "/SILENT /NORESTART")]
    [InlineData("WDM_Setup_2.8.5.10.exe", true, "/SILENT /NORESTART")]
    [InlineData("WDM-Setup-2.8.10.exe", false, "")]
    [InlineData("WDM-User-Setup-2.8.10.exe", false, "")]
    public void InstallerArgs_MatchFamily(string fileName, bool silent, string expected)
    {
        Assert.Equal(expected, UpdateChecker.InstallerArgs(@"C:\Temp\" + fileName, silent));
    }

    /// <summary>The installer must own its elevation. Pre-elevating it with
    /// the runas verb hides the signed-in user's original token, so Inno's
    /// runasoriginaluser relaunch restores WDM with administrator rights.</summary>
    [Trait("Category", Cats.Unit)][Theory]
    [InlineData("WDM-Setup-3.0.2.exe", true, "/SILENT /NORESTART")]
    [InlineData("WDM_Setup_3.0.2.0.exe", true, "/SILENT /NORESTART")]
    [InlineData("WDM-Setup-3.0.2.exe", false, "")]
    [InlineData("WDM-User-Setup-3.0.2.exe", true, "--silent")]
    public void InstallerStartInfo_LetsSetupRequestItsOwnElevation(string fileName, bool silent, string expectedArgs)
    {
        string dir = Path.Combine(Path.GetTempPath(), "wdm-startinfo-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, fileName);
        File.WriteAllBytes(path, Array.Empty<byte>());
        try
        {
            System.Diagnostics.ProcessStartInfo psi = UpdateChecker.BuildInstallerStartInfo(path, silent);
            Assert.True(psi.UseShellExecute);
            Assert.Equal("", psi.Verb);
            Assert.Equal(expectedArgs, psi.Arguments);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
