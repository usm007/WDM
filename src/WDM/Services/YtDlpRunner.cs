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
                psi.ArgumentList.Add("--cookies");
                psi.ArgumentList.Add(Path.Combine(TaskStore.AppDir, "youtube_cookies.txt"));
            }
            else
            {
                psi.ArgumentList.Add("--cookies-from-browser");
                psi.ArgumentList.Add(s.YouTubeBrowserCookies);
            }
        }

        return psi;
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
