using System.Diagnostics;
using System.IO;
using System.Text;

namespace WDM.Services;

public sealed class YtDlpException : Exception
{
    public YtDlpException(string message) : base(message) { }
}

public static class YtDlpRunner
{
    public static ProcessStartInfo CreateInfo(IEnumerable<string> args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = EngineManager.YtDlpPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        if (File.Exists(EngineManager.QuickJsPath))
        {
            psi.ArgumentList.Add("--js-runtimes");
            psi.ArgumentList.Add($"quickjs:{EngineManager.QuickJsPath}");
        }

        var s = TaskStore.LoadSettings();
        if (!string.IsNullOrWhiteSpace(s.YouTubeBrowserCookies) && s.YouTubeBrowserCookies != "none")
        {
            if (s.YouTubeBrowserCookies == "wdm-native")
            {
                string cookieFile = Path.Combine(TaskStore.AppDir, "youtube_cookies.txt");
                if (File.Exists(cookieFile))
                {
                    psi.ArgumentList.Add("--cookies");
                    psi.ArgumentList.Add(cookieFile);
                }
            }
            else if (IsAllowedBrowserName(s.YouTubeBrowserCookies))
            {
                psi.ArgumentList.Add("--cookies-from-browser");
                psi.ArgumentList.Add(s.YouTubeBrowserCookies.Trim().ToLowerInvariant());
            }
        }

        return psi;
    }

    /// <summary>Allow-list for yt-dlp --cookies-from-browser (settings.json is
    /// user-editable; an arbitrary value would be passed straight to yt-dlp).</summary>
    private static bool IsAllowedBrowserName(string value)
    {
        string v = value.Trim().ToLowerInvariant();
        // Optional ":profile" suffix (e.g. "chrome:Default").
        int colon = v.IndexOf(':');
        string browser = colon >= 0 ? v[..colon] : v;
        string profile = colon >= 0 ? v[(colon + 1)..] : "";
        bool known = browser is "chrome" or "chromium" or "edge" or "brave" or "vivaldi"
            or "opera" or "firefox" or "safari" or "whale" or "arc";
        if (!known)
            return false;
        if (profile.Length > 128 || profile.Any(ch => char.IsWhiteSpace(ch) || ch is '"' or '\'' or ';' or '&' or '|' or '`' or '$'))
            return false;
        return true;
    }

    public static async Task<string> RunJsonAsync(string url, CancellationToken ct)
    {
        var psi = CreateInfo(new[]
        {
            "--dump-single-json",
            "--flat-playlist",
            "--no-warnings",
            "--socket-timeout", "20",
            "--no-color",
            url
        });

        var output = new StringBuilder();
        var error = new StringBuilder();

        using var proc = Process.Start(psi);
        if (proc is null)
            throw new YtDlpException("Failed to start yt-dlp.");

        var outTask = proc.StandardOutput.ReadToEndAsync();
        var errTask = proc.StandardError.ReadToEndAsync();

        try
        {
            await proc.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            KillTree(proc);
            throw;
        }

        output.Append(await outTask);
        error.Append(await errTask);

        if (output.Length > 20 * 1024 * 1024)
            throw new YtDlpException("Metadata response too large — refusing to parse.");

        if (proc.ExitCode != 0)
        {
            var tail = string.Join("\n", error.ToString().Split('\n').Where(l => l.Trim().Length > 0).TakeLast(6));
            throw new YtDlpException(string.IsNullOrWhiteSpace(tail) ? "yt-dlp failed to analyze this link." : tail.Trim());
        }

        var json = output.ToString();
        if (string.IsNullOrWhiteSpace(json))
            throw new YtDlpException("No metadata returned for this link.");

        return json;
    }

    public static void KillTree(Process proc)
    {
        try
        {
            if (!proc.HasExited)
                proc.Kill(entireProcessTree: true);
        }
        catch
        {
            // already gone
        }
    }
}
