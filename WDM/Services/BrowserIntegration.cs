using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using Microsoft.Win32;

namespace WDM.Services;

public enum BrowserKind
{
    Chromium,
    Firefox,
}

public sealed class InstalledBrowser
{
    public required string Name { get; init; }
    public required string ExePath { get; init; }
    public required BrowserKind Kind { get; init; }
}

public static class BrowserIntegration
{
    public const string FirefoxExtensionId = "wdm-catcher@wdm.app";

    public static string DeployDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WDM", "BrowserExtension");

    /// <summary>
    /// Copies bundled extension files to a stable per-user location.
    /// </summary>
    public static string DeployExtension()
    {
        string dst = DeployDir;
        string? src = FindSourceDir();

        // If the app is already deployed next to its data dir (e.g. the installed
        // copy under %LOCALAPPDATA%\WDM), the "source" resolves to the destination
        // itself; copying a directory onto itself fails. In that case the extension
        // is already in place, so just ensure the folder exists.
        if (src is not null && Directory.Exists(src)
            && !string.Equals(Path.GetFullPath(src), Path.GetFullPath(dst), StringComparison.OrdinalIgnoreCase))
        {
            CopyDirectory(src, dst);
        }
        else if (!Directory.Exists(dst))
        {
            Directory.CreateDirectory(dst);
        }

        return dst;
    }

    private static string? FindSourceDir()
    {
        string[] candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "BrowserExtension"),
            Path.Combine(AppContext.BaseDirectory, "..", "WDM.BrowserExtension"),
            Path.Combine(Environment.CurrentDirectory, "WDM.BrowserExtension"),
            Path.Combine(Environment.CurrentDirectory, "BrowserExtension"),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "WDM.BrowserExtension")),
        };

        return candidates.FirstOrDefault(d => Directory.Exists(d) && File.Exists(Path.Combine(d, "manifest.json")));
    }

    public static IReadOnlyList<InstalledBrowser> DetectInstalledBrowsers()
    {
        var found = new List<InstalledBrowser>();

        var candidates = new (string Name, BrowserKind Kind, string[] Paths)[]
        {
            ("Google Chrome", BrowserKind.Chromium, new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Google", "Chrome", "Application", "chrome.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Google", "Chrome", "Application", "chrome.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Google", "Chrome", "Application", "chrome.exe"),
            }),
            ("Microsoft Edge", BrowserKind.Chromium, new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft", "Edge", "Application", "msedge.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft", "Edge", "Application", "msedge.exe"),
            }),
            ("Brave", BrowserKind.Chromium, new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "BraveSoftware", "Brave-Browser", "Application", "brave.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "BraveSoftware", "Brave-Browser", "Application", "brave.exe"),
            }),
            ("Opera", BrowserKind.Chromium, new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Opera", "launcher.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Opera", "launcher.exe"),
            }),
            ("Mozilla Firefox", BrowserKind.Firefox, new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Mozilla Firefox", "firefox.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Mozilla Firefox", "firefox.exe"),
            }),
        };

        foreach (var (name, kind, paths) in candidates)
        {
            string? exe = paths.FirstOrDefault(File.Exists);
            if (exe is not null)
                found.Add(new InstalledBrowser { Name = name, ExePath = exe, Kind = kind });
        }

        return found;
    }

    public static string InjectChromium(InstalledBrowser browser)
    {
        string dir = DeployExtension();
        if (!File.Exists(Path.Combine(dir, "manifest.json")))
            throw new IOException($"Extension files missing from {dir}");

        Process.Start(new ProcessStartInfo(browser.ExePath)
        {
            Arguments = $"--load-extension=\"{dir}\"",
            UseShellExecute = true
        });
        return $"{browser.Name} launched with extension loaded.";
    }

    public static string InjectFirefox(InstalledBrowser browser)
    {
        string dir = DeployExtension();
        string firefoxDir = Path.Combine(dir, "firefox");
        if (!File.Exists(Path.Combine(firefoxDir, "manifest.json")))
            throw new IOException($"Firefox extension missing from {firefoxDir}");

        using var key = Registry.CurrentUser.CreateSubKey(@"Software\Mozilla\Firefox\Extensions");
        key?.SetValue(FirefoxExtensionId, firefoxDir, RegistryValueKind.String);

        Process.Start(new ProcessStartInfo(browser.ExePath) { UseShellExecute = true });
        return $"{browser.Name}: Extension registered in Windows registry.";
    }

    public static void OpenExtensionFolder()
    {
        string dir = DeployExtension();
        string manifest = Path.Combine(dir, "manifest.json");
        if (File.Exists(manifest))
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{manifest}\"") { UseShellExecute = true });
        }
        else if (Directory.Exists(dir))
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
        }
    }

    public static void OpenExtensionsPage(InstalledBrowser? browser = null)
    {
        try
        {
            if (browser is not null && File.Exists(browser.ExePath))
            {
                string page = browser.Kind == BrowserKind.Firefox ? "about:debugging#/runtime/this-firefox" : "chrome://extensions";
                Process.Start(new ProcessStartInfo(browser.ExePath)
                {
                    Arguments = page,
                    UseShellExecute = true
                });
                return;
            }

            // Fall back to default browser or Chrome
            var chrome = DetectInstalledBrowsers().FirstOrDefault(b => b.Name.Contains("Chrome"));
            if (chrome is not null)
            {
                Process.Start(new ProcessStartInfo(chrome.ExePath)
                {
                    Arguments = "chrome://extensions",
                    UseShellExecute = true
                });
            }
            else
            {
                Process.Start(new ProcessStartInfo("cmd.exe", "/c start chrome://extensions")
                {
                    CreateNoWindow = true,
                    UseShellExecute = false
                });
            }
        }
        catch
        {
            // Best effort
        }
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);

        foreach (string file in Directory.GetFiles(source))
        {
            string target = Path.Combine(destination, Path.GetFileName(file));
            File.Copy(file, target, overwrite: true);
        }

        foreach (string sub in Directory.GetDirectories(source))
        {
            string target = Path.Combine(destination, Path.GetFileName(sub));
            CopyDirectory(sub, target);
        }
    }
}
