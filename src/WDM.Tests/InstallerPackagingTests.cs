using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using WDM.Tests.TestInfrastructure;

namespace WDM.Tests;

/// <summary>Installer-script contract for automatic updates. The in-app updater
/// starts the Inno setup with /SILENT; an entry in [Run] that carries
/// "skipifsilent" is never executed then, so an automatic update closed WDM and
/// never reopened it. These are cheap file assertions that catch a regression
/// before a release is built.</summary>
public sealed class InstallerPackagingTests
{
    private static string InstallerScriptPath()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "src", "WDM.Setup", "installer.iss")))
            dir = Directory.GetParent(dir)?.FullName;
        Assert.NotNull(dir);
        return Path.Combine(dir!, "src", "WDM.Setup", "installer.iss");
    }

    /// <summary>Lines of one [Section], comments and blanks removed.</summary>
    private static List<string> Section(string name)
    {
        var lines = new List<string>();
        bool inSection = false;
        foreach (string raw in File.ReadAllLines(InstallerScriptPath()))
        {
            string line = raw.Trim();
            if (line.StartsWith("[", StringComparison.Ordinal))
            {
                inSection = line.Equals("[" + name + "]", StringComparison.OrdinalIgnoreCase);
                continue;
            }
            if (!inSection || line.Length == 0 || line.StartsWith(";", StringComparison.Ordinal))
                continue;
            lines.Add(line);
        }
        Assert.NotEmpty(lines);
        return lines;
    }

    [Trait("Category", Cats.Unit)][Fact]
    public void SilentUpdate_RelaunchesTheApp()
    {
        string launch = Assert.Single(Section("Run")
            .Where(l => l.Contains("{#MyAppExeName}", StringComparison.OrdinalIgnoreCase)
                        && l.Contains("postinstall", StringComparison.OrdinalIgnoreCase)));

        // skipifsilent suppresses the entry for /SILENT installs, which is
        // exactly how the updater runs setup. That made "update and restart"
        // leave WDM closed.
        Assert.DoesNotContain("skipifsilent", launch, StringComparison.OrdinalIgnoreCase);
    }

    [Trait("Category", Cats.Unit)][Fact]
    public void RelaunchAfterElevatedInstall_KeepsUserToken()
    {
        string launch = Assert.Single(Section("Run")
            .Where(l => l.Contains("{#MyAppExeName}", StringComparison.OrdinalIgnoreCase)
                        && l.Contains("postinstall", StringComparison.OrdinalIgnoreCase)));

        // Setup is per-machine and elevates via UAC. Without runasoriginaluser
        // the relaunched WDM inherits the elevated token, so it would run as
        // administrator and write user data into the admin context.
        Assert.Contains("runasoriginaluser", launch, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Auto-start must have exactly one owner: the app's per-user
    /// switch. A machine-wide HKLM Run value written by setup cannot be
    /// turned off from the app, so autostart started WDM twice at logon and
    /// the switch appeared not to work.</summary>
    [Trait("Category", Cats.Unit)][Fact]
    public void Autostart_IsOwnedByTheAppOnly()
    {
        List<string> registry = Section("Registry");

        Assert.DoesNotContain(registry, l =>
            l.Contains("CurrentVersion\\Run", StringComparison.OrdinalIgnoreCase) &&
            l.Contains("ValueData:", StringComparison.OrdinalIgnoreCase));

        Assert.DoesNotContain(Section("Tasks"), l =>
            l.Contains("startwithwindows", StringComparison.OrdinalIgnoreCase));

        // Upgrades must remove the value earlier versions wrote.
        Assert.Contains(registry, l =>
            l.Contains("Root: HKLM;", StringComparison.OrdinalIgnoreCase) &&
            l.Contains("CurrentVersion\\Run", StringComparison.OrdinalIgnoreCase) &&
            l.Contains("deletevalue", StringComparison.OrdinalIgnoreCase));
    }
}
