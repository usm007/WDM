using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;

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
    public const string FirefoxAddonUrl =
        "https://addons.mozilla.org/en-US/firefox/addon/wdm-download-catcher/";

    public const string WebGuideUrl =
        "https://get-wdm.vercel.app/extension-guide.html";

    // CRX Loader 1-click install (Chromium, no Developer mode). Single source
    // of truth — BrowserExtensionControl and ExtensionGuideWindow
    // all launch through the helpers below.
    public const string CrxLoaderStoreUrl =
        "https://chromewebstore.google.com/detail/crx-loader/chokhpikgifdgmfipekibjgnhhmkddon";

    public const string CrxLoaderInstallUrl =
        "https://crxloader.com/install" +
        "?download_url=https%3A%2F%2Fget-wdm.vercel.app%2Fwdm-extension.zip" +
        "&name=WDM%20Download%20Catcher" +
        "&icon_url=https%3A%2F%2Fget-wdm.vercel.app%2Ficon128.png" +
        "&success_url=https%3A%2F%2Fget-wdm.vercel.app%2F%3Finstalled%3Dtrue" +
        "&auto=1";

    public static string DeployDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WDM", "BrowserExtension");

    /// <summary>Writes the approved full-session-replay host list for the
    /// extension (<c>wdm-session-hosts.json</c> next to the token file).
    /// Called during deploy and whenever approvals change mid-run.</summary>
    public static void DeploySessionHosts()
    {
        try
        {
            string dir = DeployDir;
            Directory.CreateDirectory(dir);
            List<string> hosts;
            try
            {
                hosts = TaskStore.LoadSettings().FullSessionReplayHosts
                    .Where(h => !string.IsNullOrWhiteSpace(h)).Select(h => h.Trim().ToLowerInvariant())
                    .Distinct(StringComparer.OrdinalIgnoreCase).Take(200).ToList();
            }
            catch { hosts = new List<string>(); }
            string json = System.Text.Json.JsonSerializer.Serialize(new { hosts });
            string path = Path.Combine(dir, "wdm-session-hosts.json");
            string existing = "";
            try { if (File.Exists(path)) existing = File.ReadAllText(path); } catch { }
            if (!string.Equals(existing, json, StringComparison.Ordinal))
            {
                string tmp = path + ".wdm-tmp";
                File.WriteAllText(tmp, json);
                File.Move(tmp, path, overwrite: true);
            }
        }
        catch { }
    }

    /// <summary>
    /// Copies bundled extension files to a stable per-user location for
    /// <b>Load unpacked</b> (the only supported Chromium pathway).
    /// Version-aware: when the deployed folder is current the copy is skipped,
    /// so a running browser's unpacked install is never rewritten under it on
    /// every boot. Pack artifacts (crx/xpi/update.xml/pem) and the Firefox-only
    /// subfolder are excluded — they don't belong in an unpacked load.
    /// </summary>
    public static string DeployExtension()
    {
        string dst = DeployDir;
        string? src = FindSourceDir();

        // With a per-machine install the source is {app}\BrowserExtension under
        // %ProgramFiles%\WDM (read-only) and the destination is the per-user
        // DeployDir below; in dev layouts source may equal destination, and
        // copying a directory onto itself fails. In that case the extension
        // is already in place, so just ensure the folder exists.
        bool isCurrent = false;
        if (src is not null && Directory.Exists(src)
            && !string.Equals(Path.GetFullPath(src), Path.GetFullPath(dst), StringComparison.OrdinalIgnoreCase))
        {
            isCurrent = IsDeployedCurrent(src, dst);
            if (!isCurrent)
                CopyUnpackedExtension(src, dst);
        }
        else if (!Directory.Exists(dst))
        {
            Directory.CreateDirectory(dst);
        }
        else
        {
            isCurrent = true;
        }

        // Only modify deployed files if the deployment wasn't current or token is genuinely missing.
        // Never touch files or modify timestamps in an already-current unpacked folder on every boot,
        // as external modifications trigger Chrome's extension tamper/corruption detection.
        if (!isCurrent || !File.Exists(Path.Combine(dst, CaptureAuth.ExtensionTokenFileName)))
        {
            try { StripDeployedUpdateUrl(dst); } catch { }
            WriteExtensionToken(dst);
        }
        // Approved session hosts ride alongside (change-guarded inside, so an
        // already-current folder is never touched on every boot).
        try { DeploySessionHosts(); } catch { }

        return dst;
    }

    /// <summary>True when the live deployed copy matches the bundled source.
    /// False when the source can't be located (dev/unknown layout) or the
    /// copy is stale — the caller should surface the reload notice.</summary>
    public static bool IsDeployedCurrent()
    {
        try
        {
            string? src = FindSourceDir();
            if (src is null || !Directory.Exists(src))
                return false;
            string dst = DeployDir;
            if (!Directory.Exists(dst))
                return false;
            return IsDeployedCurrent(src, dst);
        }
        catch { return false; }
    }

    public sealed record ExtensionHealth(bool DeployedCurrent, bool TokenPresent, int BrowsersFound)
    {
        public bool Healthy => DeployedCurrent && TokenPresent;
    }

    /// <summary>Extension health snapshot: deployed copy fresh, loopback token
    /// present, browsers detected. The one-click repair is the existing setup
    /// flow (redeploy + open extensions page); this tells the UI when to
    /// offer it.</summary>
    public static ExtensionHealth CheckHealth()
    {
        bool current = false;
        bool token = false;
        int browsers = 0;
        try { current = IsDeployedCurrent(); } catch { }
        try { token = File.Exists(Path.Combine(DeployDir, CaptureAuth.ExtensionTokenFileName)); } catch { }
        try { browsers = DetectInstalledBrowsers().Count; } catch { }
        return new ExtensionHealth(current, token, browsers);
    }

    /// <summary>True when the deployed unpacked folder matches the bundled
    /// source (same version + key, no update_url, no missing files).</summary>
    internal static bool IsDeployedCurrent(string src, string dst)
    {
        try
        {
            string srcManifest = Path.Combine(src, "manifest.json");
            string dstManifest = Path.Combine(dst, "manifest.json");
            if (!File.Exists(srcManifest) || !File.Exists(dstManifest))
                return false;
            string? srcVersion = ReadManifestProperty(srcManifest, "version");
            string? dstVersion = ReadManifestProperty(dstManifest, "version");
            if (string.IsNullOrEmpty(srcVersion) ||
                !string.Equals(srcVersion, dstVersion, StringComparison.OrdinalIgnoreCase))
                return false;
            // Same pinned key => same extension ID across reboots.
            string? srcKey = ReadManifestProperty(srcManifest, "key");
            string? dstKey = ReadManifestProperty(dstManifest, "key");
            if (!string.Equals(srcKey ?? "", dstKey ?? "", StringComparison.Ordinal))
                return false;
            if (ReadManifestProperty(dstManifest, "update_url") is not null)
                return false;
            foreach (string file in EnumerateUnpackedFiles(src))
            {
                string rel = Path.GetRelativePath(src, file);
                if (!File.Exists(Path.Combine(dst, rel)))
                    return false;
            }
            return true;
        }
        catch { return false; } // unknown => redeploy
    }

    private static string? ReadManifestProperty(string path, string name)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (doc.RootElement.TryGetProperty(name, out var el) &&
                el.ValueKind == JsonValueKind.String)
                return el.GetString();
        }
        catch { }
        return null;
    }

    private static IEnumerable<string> EnumerateUnpackedFiles(string root)
    {
        foreach (string file in Directory.GetFiles(root))
        {
            if (!IsPackArtifact(file, root))
                yield return file;
        }
        foreach (string sub in Directory.GetDirectories(root))
        {
            if (IsPackArtifact(Path.Combine(sub, "__dir__"), root))
                continue;
            foreach (string file in EnumerateUnpackedFiles(sub))
                yield return file;
        }
    }

    private static bool IsPackArtifact(string fullPath, string root)
    {
        string rel = Path.GetRelativePath(root, fullPath);
        if (rel.Equals("firefox", StringComparison.OrdinalIgnoreCase) ||
            rel.StartsWith("firefox" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            return true;
        string name = Path.GetFileName(fullPath.TrimEnd(Path.DirectorySeparatorChar));
        if (name.Equals("update.xml", StringComparison.OrdinalIgnoreCase))
            return true;
        string ext = Path.GetExtension(name);
        return ext.Equals(".pem", StringComparison.OrdinalIgnoreCase) ||
               ext.Equals(".crx", StringComparison.OrdinalIgnoreCase) ||
               ext.Equals(".xpi", StringComparison.OrdinalIgnoreCase);
    }

    private static void CopyUnpackedExtension(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (string file in EnumerateUnpackedFiles(source))
        {
            string rel = Path.GetRelativePath(source, file);
            string target = Path.Combine(destination, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
        // Remove stale pack artifacts from earlier deploys — they don't belong
        // in a Load unpacked folder.
        foreach (string file in Directory.GetFiles(destination))
        {
            if (IsPackArtifact(file, destination))
            {
                try { File.Delete(file); } catch { }
            }
        }
    }

    /// <summary>Removes update_url from the deployed manifest (rewrites the file
    /// only when the property is present). Unpacked installs must never declare
    /// an update URL — a dead one gets the extension disabled after reboot.</summary>
    private static void StripDeployedUpdateUrl(string dst)
    {
        string manifestPath = Path.Combine(dst, "manifest.json");
        if (!File.Exists(manifestPath))
            return;
        string json = File.ReadAllText(manifestPath);
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("update_url", out _))
            return;
        using var outStream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(outStream, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (prop.NameEquals("update_url"))
                    continue;
                prop.WriteTo(writer);
            }
            writer.WriteEndObject();
        }
        File.WriteAllText(manifestPath,
            System.Text.Encoding.UTF8.GetString(outStream.ToArray()));
    }

    private static void WriteExtensionToken(string dst)
    {
        // Ship the loopback auth token alongside the unpacked extension so the
        // background worker can authenticate capture calls (see CaptureAuth).
        // Deliberately NOT web-accessible: only the extension itself may read it.
        try
        {
            string tokenPath = Path.Combine(dst, CaptureAuth.ExtensionTokenFileName);
            string tokenJson = $"{{\"token\":\"{CaptureAuth.GetOrCreateToken()}\"}}";
            bool stale = true;
            try { stale = !File.Exists(tokenPath) || !File.ReadAllText(tokenPath).Contains(CaptureAuth.GetOrCreateToken()); }
            catch { stale = true; }
            if (stale)
            {
                // Atomic write: Chrome loads the unpacked extension from this
                // folder, so it must never observe a half-written file.
                string tmp = tokenPath + ".wdm-tmp";
                File.WriteAllText(tmp, tokenJson);
                File.Move(tmp, tokenPath, overwrite: true);
            }
        }
        catch { /* token file is best-effort; the server still enforces SSRF/Origin checks */ }
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
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Mozilla Firefox", "firefox.exe"),
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

    public static void OpenCrxLoaderStore()
    {
        Process.Start(new ProcessStartInfo(CrxLoaderStoreUrl) { UseShellExecute = true });
    }

    public static void OpenOneClickInstall()
    {
        Process.Start(new ProcessStartInfo(CrxLoaderInstallUrl) { UseShellExecute = true });
    }

    public static void OpenExtensionsPage(InstalledBrowser? browser = null)
    {
        try
        {
            var chromium = browser ?? DetectInstalledBrowsers().FirstOrDefault(b => b.Kind == BrowserKind.Chromium);
            if (chromium is not null && File.Exists(chromium.ExePath))
            {
                string url = ExtensionsPageFor(chromium);
                try
                {
                    if (System.Windows.Application.Current?.Dispatcher.CheckAccess() == true)
                        System.Windows.Clipboard.SetText(url);
                }
                catch { }

                Process.Start(new ProcessStartInfo(chromium.ExePath)
                {
                    Arguments = url,
                    UseShellExecute = true
                });
                return;
            }

            // No Chromium browser found — fallback
            Process.Start(new ProcessStartInfo("cmd.exe", "/c start chrome://extensions") { UseShellExecute = true });
        }
        catch
        {
            // Best effort
        }
    }

    /// <summary>Returns the extensions page URL for a browser (edge://extensions on
    /// Edge, chrome://extensions on other Chromium browsers, about:debugging on Firefox).</summary>
    private static string ExtensionsPageFor(InstalledBrowser browser)
    {
        if (browser.Kind == BrowserKind.Firefox)
            return "about:debugging#/runtime/this-firefox";
        if (browser.Name.Contains("Edge", StringComparison.OrdinalIgnoreCase))
            return "edge://extensions";
        return "chrome://extensions";
    }

    public static void OpenFirefoxAddonPage()
    {
        var firefox = DetectInstalledBrowsers().FirstOrDefault(b => b.Kind == BrowserKind.Firefox);
        if (firefox is not null && File.Exists(firefox.ExePath))
        {
            Process.Start(new ProcessStartInfo(firefox.ExePath)
            {
                Arguments = $"\"{FirefoxAddonUrl}\"",
                UseShellExecute = true
            });
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo("firefox", $"\"{FirefoxAddonUrl}\"") { UseShellExecute = true });
            return;
        }
        catch
        {
            // Fallback to default browser if Firefox executable cannot be found
            Process.Start(new ProcessStartInfo(FirefoxAddonUrl) { UseShellExecute = true });
        }
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

    /// <summary>
    /// Opens the hosted online browser extension setup guide in the default
    /// browser. Used on first-ever run (no offline fallback — first run
    /// implies a fresh install with network). For general help with an
    /// offline fallback, use <see cref="OpenExtensionGuide"/>.
    /// </summary>
    public static void OpenExtensionGuideOnline()
    {
        Process.Start(new ProcessStartInfo(WebGuideUrl) { UseShellExecute = true });
    }

    /// <summary>
    /// Opens the online browser extension setup guide in the default browser.
    /// </summary>
    public static void OpenExtensionGuide()
    {
        try
        {
            Process.Start(new ProcessStartInfo(WebGuideUrl) { UseShellExecute = true });
            return;
        }
        catch
        {
            // Fall through to offline copies below
        }

        try
        {
            string[] candidates = new[]
            {
                Path.Combine(AppContext.BaseDirectory, "Docs", "extension_guide.html"),
                Path.Combine(Environment.CurrentDirectory, "Docs", "extension_guide.html"),
                Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "Docs", "extension_guide.html"),
                Path.Combine(AppContext.BaseDirectory, "extension_guide.html"),
            };

            string? localHtml = candidates.FirstOrDefault(File.Exists);
            if (localHtml is not null)
            {
                Process.Start(new ProcessStartInfo(localHtml) { UseShellExecute = true });
                return;
            }

            // Fallback: GitHub repository README/documentation
            Process.Start(new ProcessStartInfo("https://github.com/usm007/WDM#browser-extension") { UseShellExecute = true });
        }
        catch
        {
            // Best effort
        }
    }

}