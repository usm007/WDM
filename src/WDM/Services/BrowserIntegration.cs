using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading;
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

    /// <summary>
    /// Loads the unpacked extension into a running Chrome/Edge (or launches it)
    /// via the DevTools Protocol "Extensions.loadUnpacked" command. This replaced
    /// the removed "--load-extension" flag (Chrome 137+) and works over a plain
    /// remote-debugging WebSocket on modern browsers (Chrome/Edge 149+; older
    /// versions may need --remote-debugging-pipe, which is not supported here).
    /// <para>
    /// Note: CDP-installed extensions are tied to the browser session and may be
    /// uninstalled after a browser restart, so this is a "load now" helper rather
    /// than a permanent install.
    /// </para>
    /// </summary>
    public static string LoadChromiumViaCdp(InstalledBrowser browser)
    {
        string dir = DeployExtension();
        if (!File.Exists(Path.Combine(dir, "manifest.json")))
            throw new IOException($"Extension files missing from {dir}");

        int port = GetFreePort();
        string args = $"--remote-debugging-port={port} --remote-allow-origins=* --enable-unsafe-extension-debugging";
        Process.Start(new ProcessStartInfo(browser.ExePath) { Arguments = args, UseShellExecute = true });

        string? wsUrl = WaitForDebugEndpoint(port, TimeSpan.FromSeconds(12));
        if (wsUrl is null)
        {
            return $"{browser.Name} is already running without remote debugging — the DevTools " +
                   $"port can't open. Close {browser.Name} completely, then click this button again.";
        }

        var result = CallCdpAsync(wsUrl, "Extensions.loadUnpacked",
            new Dictionary<string, object?> { ["path"] = dir, ["enableInIncognito"] = true }).GetAwaiter().GetResult();

        string id = result.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "" : "";
        string note = "Extension loaded for this session. It may be removed when the browser restarts — " +
                      "run “Install Now” again, or use Load unpacked for a permanent (manual) install.";
        return string.IsNullOrEmpty(id)
            ? $"{browser.Name}: extension loaded. {note}"
            : $"{browser.Name}: extension loaded (ID {id}). {note}";
    }

    /// <summary>
    /// Registers the extension with Chrome/Edge via the Windows "External Extension"
    /// registry pre-install mechanism, so the browser shows the one-time
    /// "enable extension" confirmation on next launch. Chrome requires the add-on
    /// to be published to the Chrome Web Store (one-time $5 developer fee); Edge
    /// uses Microsoft Edge Add-ons, which is free to publish.
    /// </summary>
    public const string ChromiumWebStoreId = ""; // 32-char ID, e.g. "aabbccddeeff00112233445566778899", once published (paid)
    public const string EdgeAddOnsId = "";       // 32-char ID once published to Edge Add-ons (free)

    public static string PreinstallChromium(InstalledBrowser browser)
    {
        bool isEdge = browser.Name.Contains("Edge", StringComparison.OrdinalIgnoreCase);
        string id = isEdge ? EdgeAddOnsId : ChromiumWebStoreId;
        string root = isEdge
            ? @"Software\Microsoft\Edge\Extensions"
            : @"Software\Google\Chrome\Extensions";
        string updateUrl = isEdge
            ? "https://edge.microsoft.com/extensionwebstorebase/v1/crx"
            : "https://clients2.google.com/service/update2/crx";

        if (string.IsNullOrWhiteSpace(id))
        {
            string store = isEdge
                ? "Microsoft Edge Add-ons (free to publish)"
                : "the Chrome Web Store (requires the one-time $5 developer registration)";
            return $"{browser.Name}: the add-on isn't published to {store} yet, so a permanent " +
                   "install isn't possible. Publish it, set the store ID in BrowserIntegration.cs, and retry.";
        }

        using var key = Registry.CurrentUser.CreateSubKey(root + "\\" + id);
        key?.SetValue("update_url", updateUrl, RegistryValueKind.String);

        return $"{browser.Name}: registered for automatic install. Next time you open {browser.Name}, " +
               "confirm the “Enable extension” prompt once and it stays installed.";
    }

    /// <summary>True when the store-registry entry for this Chromium browser exists.</summary>
    public static bool IsChromiumStoreRegistered(InstalledBrowser browser)
    {
        bool isEdge = browser.Name.Contains("Edge", StringComparison.OrdinalIgnoreCase);
        string id = isEdge ? EdgeAddOnsId : ChromiumWebStoreId;
        if (string.IsNullOrWhiteSpace(id))
            return false;
        string root = isEdge
            ? @"Software\Microsoft\Edge\Extensions"
            : @"Software\Google\Chrome\Extensions";
        using var key = Registry.CurrentUser.OpenSubKey(root);
        return key?.GetSubKeyNames().Any(n => string.Equals(n, id, StringComparison.OrdinalIgnoreCase)) == true;
    }

    private static int GetFreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static string? WaitForDebugEndpoint(int port, TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
                string json = client.GetStringAsync($"http://127.0.0.1:{port}/json/version").GetAwaiter().GetResult();
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("webSocketDebuggerUrl", out var ws))
                    return ws.GetString();
            }
            catch
            {
                // Browser not ready yet — keep polling.
            }
            Thread.Sleep(400);
        }
        return null;
    }

    private static async Task<JsonElement> CallCdpAsync(string wsUrl, string method, object? parameters)
    {
        using var ws = new ClientWebSocket();
        ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        await ws.ConnectAsync(new Uri(wsUrl), cts.Token);

        int msgId = Environment.TickCount & 0x7FFFFFFF;
        byte[] request = JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object?>
        {
            ["id"] = msgId,
            ["method"] = method,
            ["params"] = parameters,
        });

        await ws.SendAsync(new ArraySegment<byte>(request), WebSocketMessageType.Text, true, cts.Token);

        using var ms = new MemoryStream();
        var buffer = new byte[8192];
        WebSocketReceiveResult recv;
        do
        {
            recv = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), cts.Token);
            ms.Write(buffer, 0, recv.Count);
        }
        while (!recv.EndOfMessage);

        using var doc = JsonDocument.Parse(ms.ToArray());
        JsonElement root = doc.RootElement;

        if (root.TryGetProperty("error", out JsonElement err))
            throw new InvalidOperationException($"CDP error ({method}): {err.GetProperty("message").GetString()}");

        return root.TryGetProperty("result", out JsonElement result) ? result.Clone() : default;
    }

    /// <summary>
    /// Builds a signed-ready XPI from the bundled unpacked Firefox extension and
    /// registers it via the per-user ExtensionSettings enterprise policy, so
    /// Firefox installs it automatically on next launch.
    /// <para>
    /// Firefox release builds require the XPI to be Mozilla-signed (submit once
    /// to addons.mozilla.org as a self-distributed add-on). Until then the policy
    /// is registered but the extension shows as a blocked/unsigned add-on.
    /// </para>
    /// </summary>
    public static string InstallFirefoxViaPolicy(InstalledBrowser browser)
    {
        string dir = DeployExtension();
        string firefoxDir = Path.Combine(dir, "firefox");
        if (!File.Exists(Path.Combine(firefoxDir, "manifest.json")))
            throw new IOException($"Firefox extension missing from {firefoxDir}");

        string xpi = BuildFirefoxXpi(firefoxDir);
        string json = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            [FirefoxExtensionId] = new Dictionary<string, string>
            {
                ["installation_mode"] = "normal_installed",
                ["install_url"] = new Uri(xpi).AbsoluteUri,
            }
        });

        using var key = Registry.CurrentUser.CreateSubKey(@"Software\Policies\Mozilla\Firefox");
        key?.SetValue("ExtensionSettings", json, RegistryValueKind.String);

        Process.Start(new ProcessStartInfo(browser.ExePath) { UseShellExecute = true });
        return $"{browser.Name}: extension registered via enterprise policy — Firefox installs it on next launch.";
    }

    /// <summary>True when the per-user ExtensionSettings policy for WDM is registered.</summary>
    public static bool IsFirefoxPolicyRegistered()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Policies\Mozilla\Firefox");
        return key?.GetValue("ExtensionSettings") is string json && json.Contains(FirefoxExtensionId, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>True when the bundled XPI carries a Mozilla signature (META-INF/).</summary>
    public static bool IsFirefoxXpiSigned()
    {
        try
        {
            string xpi = Path.Combine(DeployDir, "wdm-catcher.xpi");
            if (!File.Exists(xpi))
                return false;
            using var zip = ZipFile.OpenRead(xpi);
            return zip.Entries.Any(e => e.FullName.StartsWith("META-INF/", StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Packs the unpacked Firefox extension folder into a single XPI file.</summary>
    public static string BuildFirefoxXpi(string firefoxDir)
    {
        string xpi = Path.Combine(DeployDir, "wdm-catcher.xpi");
        if (File.Exists(xpi))
            File.Delete(xpi);

        ZipFile.CreateFromDirectory(firefoxDir, xpi, CompressionLevel.Optimal, includeBaseDirectory: false);
        return xpi;
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
                // Last resort: let the OS resolve the URI to whatever handles it.
                Process.Start(new ProcessStartInfo("chrome://extensions") { UseShellExecute = true });
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
