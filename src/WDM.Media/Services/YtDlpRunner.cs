using System.Diagnostics;
using System.IO;
using System.Text;
using WDM.Media;

namespace WDM.Services;

public sealed class YtDlpException : Exception
{
    public YtDlpException(string message) : base(message) { }
}

public sealed record ProcessRunResult(int ExitCode, string Stdout, string Stderr);

public static class YtDlpRunner
{
    /// <summary>Test hook: when non-null, invoked instead of Process.Start.
    /// Static to match WDM's static service pattern. Always reset to null after use.</summary>
    public static Func<ProcessStartInfo, Task<ProcessRunResult>>? RunnerOverride { get; set; }

    public static ProcessStartInfo CreateInfo(IEnumerable<string> args) => CreateInfo(args, out _);

    /// <summary>Builds the yt-dlp spawn info. When the DPAPI-protected cookie
    /// store exists, a short-lived plaintext copy is materialized for this
    /// spawn; invoke <paramref name="cookieCleanup"/> after the child exits
    /// (both real and <see cref="RunnerOverride"/> paths) to delete it.</summary>
    public static ProcessStartInfo CreateInfo(IEnumerable<string> args, out Action? cookieCleanup)
    {
        cookieCleanup = null;
        var psi = new ProcessStartInfo
        {
            FileName = MediaEnvironment.YtDlpPath(),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        if (File.Exists(MediaEnvironment.QuickJsPath()))
        {
            psi.ArgumentList.Add("--js-runtimes");
            psi.ArgumentList.Add($"quickjs:{MediaEnvironment.QuickJsPath()}");
        }

        var s = MediaSettings.Current;
        string? proxyUrl = ProxyHelper.ProxyUrlFor(s);
        if (!string.IsNullOrWhiteSpace(proxyUrl))
        {
            psi.ArgumentList.Add("--proxy");
            psi.ArgumentList.Add(proxyUrl);
        }
        if (!string.IsNullOrWhiteSpace(s.YouTubeBrowserCookies) && s.YouTubeBrowserCookies != "none")
        {
            if (s.YouTubeBrowserCookies == "wdm-native")
            {
                string? materialized = MaterializeCookieFile(out cookieCleanup);
                if (materialized is not null)
                {
                    psi.ArgumentList.Add("--cookies");
                    psi.ArgumentList.Add(materialized);
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

    /// <summary>Resolves the plaintext cookie file for one yt-dlp spawn.
    /// DPAPI blobs are decrypted into a temp copy (deleted via the returned
    /// cleanup); legacy plaintext stores are used as-is (next sign-in
    /// re-exports them protected). Returns null when there is nothing usable.
    /// </summary>
    internal static string? MaterializeCookieFile(out Action? cookieCleanup)
    {
        cookieCleanup = null;
        try
        {
            string cookieFile = Path.Combine(AppPaths.DataDir, "youtube_cookies.txt");
            if (!File.Exists(cookieFile))
                return null;
            string stored;
            try
            {
                var info = new FileInfo(cookieFile);
                if (info.Length <= 0 || info.Length > 2 * 1024 * 1024)
                    return null;
                stored = File.ReadAllText(cookieFile);
            }
            catch { return null; }
            string? plain = DataProtector.TryUnprotectFromBase64(stored);
            if (plain is null)
            {
                // Legacy plaintext store (or foreign blob): pass through only
                // if it parses as a Netscape cookie file.
                try
                {
                    if (stored.Contains("# Netscape HTTP Cookie File", StringComparison.Ordinal))
                        return cookieFile;
                }
                catch { }
                return null;
            }
            string tmp = Path.Combine(Path.GetTempPath(), "wdm-cookies-" + Guid.NewGuid().ToString("N") + ".txt");
            try { File.WriteAllText(tmp, plain); }
            catch { return null; }
            cookieCleanup = () => { try { File.Delete(tmp); } catch { } };
            return tmp;
        }
        catch { return null; }
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
        }, out var cookieCleanup);
        try
        {

        if (RunnerOverride is not null)
        {
            var r = await RunnerOverride(psi);
            if (r.ExitCode != 0)
            {
                var tail = string.Join("\n", r.Stderr.Split('\n').Where(l => l.Trim().Length > 0).TakeLast(6));
                throw new YtDlpException(string.IsNullOrWhiteSpace(tail) ? "yt-dlp failed to analyze this link." : tail.Trim());
            }
            if (string.IsNullOrWhiteSpace(r.Stdout))
                throw new YtDlpException("No metadata returned for this link.");
            return r.Stdout;
        }

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
        finally
        {
            try { cookieCleanup?.Invoke(); } catch { }
        }
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
