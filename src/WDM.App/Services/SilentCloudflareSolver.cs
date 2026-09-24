using Microsoft.Web.WebView2.Core;

namespace WDM.Services;

/// <summary>Background Cloudflare clearance without any window, popup, or user
/// click. A hidden WebView2 controller navigates to the blocked URL; managed
/// challenges / Turnstile solve themselves in a real browser profile in most
/// cases, at which point the <c>cf_clearance</c> cookie appears and is captured.
/// Hard WAF blocks ("You have been blocked") never produce clearance, so the
/// page text is probed and the attempt aborts early instead of burning the full
/// timeout. Returns null when nothing was solved — the caller keeps the
/// engine's actionable error and never opens any UI.</summary>
public static class SilentCloudflareSolver
{
    public sealed record SolvedSession(string Cookies, string UserAgent, string? FinalUrl);

    private const string ChromeUa =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";

    /// <summary>Must be called on the UI (STA) thread: WebView2 is COM-bound.</summary>
    public static async Task<SolvedSession?> TrySolveAsync(string url, TimeSpan timeout, CancellationToken ct = default)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var target) ||
            (target.Scheme != Uri.UriSchemeHttp && target.Scheme != Uri.UriSchemeHttps))
            return null;

        string userDataDir = Path.Combine(TaskStore.AppDir, "WebView2");
        Directory.CreateDirectory(userDataDir);

        CoreWebView2Environment env;
        try
        {
            env = await CoreWebView2Environment.CreateAsync(null, userDataDir);
        }
        catch
        {
            // WebView2 runtime missing/broken — nothing to solve with.
            return null;
        }

        // Zero parent handle = invisible controller, no window ever appears.
        CoreWebView2Controller controller;
        try
        {
            controller = await env.CreateCoreWebView2ControllerAsync(IntPtr.Zero);
        }
        catch
        {
            return null;
        }

        try
        {
            var webview = controller.CoreWebView2;
            webview.Settings.IsStatusBarEnabled = false;
            webview.Settings.UserAgent = ChromeUa;

            string? downloadUri = null;
            void OnDownloadStarting(object? sender, CoreWebView2DownloadStartingEventArgs e)
            {
                try { e.Cancel = true; } catch { }
                // e.ResultFilePath is a LOCAL path, not a URL — keep the remote URI.
                if (!string.IsNullOrWhiteSpace(e.DownloadOperation.Uri))
                    downloadUri = e.DownloadOperation.Uri;
            }
            webview.DownloadStarting += OnDownloadStarting;
            try
            {
                webview.Navigate(target.ToString());
                var deadline = DateTime.UtcNow + timeout;
                var started = DateTime.UtcNow;
                bool hardBlockChecked = false;
                int poll = 0;

                while (DateTime.UtcNow < deadline)
                {
                    ct.ThrowIfCancellationRequested();
                    await Task.Delay(1000, ct).ConfigureAwait(true);

                    IReadOnlyList<CoreWebView2Cookie> cookies;
                    try
                    {
                        cookies = await webview.CookieManager.GetCookiesAsync(target.ToString());
                    }
                    catch
                    {
                        continue;
                    }

                    var clearance = cookies.FirstOrDefault(c =>
                        c.Name.Equals("cf_clearance", StringComparison.OrdinalIgnoreCase));
                    if (clearance is not null && !string.IsNullOrEmpty(clearance.Value))
                    {
                        string all = string.Join("; ", cookies.Select(c => $"{c.Name}={c.Value}"));
                        string final = downloadUri ?? webview.Source;
                        return new SolvedSession(all, ChromeUa, final);
                    }

                    // Direct file links trigger a download instead of a page: without
                    // clearance shortly after, waiting the full timeout is pointless.
                    if (downloadUri is not null && DateTime.UtcNow - started > TimeSpan.FromSeconds(8))
                        return null;

                    // One hard-block probe per attempt: a WAF ban page will never
                    // yield clearance, so stop early instead of idling 45s.
                    if (!hardBlockChecked && ++poll >= 5)
                    {
                        hardBlockChecked = true;
                        if (await LooksHardBlockedAsync(webview).ConfigureAwait(true))
                            return null;
                    }
                }
            }
            finally
            {
                webview.DownloadStarting -= OnDownloadStarting;
            }
            return null;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch
        {
            return null;
        }
        finally
        {
            try { controller.Close(); } catch { }
        }
    }

    private static async Task<bool> LooksHardBlockedAsync(CoreWebView2 webview)
    {
        try
        {
            string json = await webview.ExecuteScriptAsync(
                "() => (document && document.body && document.body.innerText || '').slice(0, 4000)");
            string text = System.Text.Json.JsonSerializer.Deserialize<string>(json) ?? "";
            return text.IndexOf("you have been blocked", StringComparison.OrdinalIgnoreCase) >= 0
                || text.IndexOf("attention required", StringComparison.OrdinalIgnoreCase) >= 0
                || (text.IndexOf("ray id", StringComparison.OrdinalIgnoreCase) >= 0
                    && text.IndexOf("cloudflare", StringComparison.OrdinalIgnoreCase) >= 0);
        }
        catch
        {
            return false;
        }
    }
}
