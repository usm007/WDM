using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using WDM.Models;

namespace WDM;

public partial class CloudflareChallengeWindow : Wpf.Ui.Controls.FluentWindow
{
    private readonly DownloadTask _task;
    public string? ExtractedCookies { get; private set; }
    public string? ExtractedUserAgent { get; private set; }
    public string? FinalRedirectUrl { get; private set; }
    public bool ClearanceCaptured { get; private set; }

    /// <summary>When true, skips WebView2 initialization entirely (screenshot /
    /// test automation). Initializing WebView2 on an offscreen window aborts
    /// with E_ABORT (0x80004004) and must never pop a modal MessageBox, which
    /// would block the automation run.</summary>
    public static bool SuppressBrowserInit { get; set; }

    public CloudflareChallengeWindow(DownloadTask task)
    {
        InitializeComponent();
        _task = task;
        Title = $"Cloudflare Protection: {task.DisplayFileName}";

        Loaded += async (_, _) => await InitWebViewAsync();
        Closed += (_, _) => DetachWebView();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        Services.ThemeService.ApplyTitleBar(this);
    }

    private async Task InitWebViewAsync()
    {
        // Screenshot / test mode: no real browser: show a static placeholder
        // instead of failing with E_ABORT + a modal MessageBox.
        if (SuppressBrowserInit)
        {
            LoadingOverlay.Visibility = Visibility.Collapsed;
            StatusText.Text = "Browser preview disabled in screenshot mode: solve the check in the live app and WDM will capture clearance cookies here.";
            return;
        }
        try
        {
            string userDataDir = Path.Combine(Services.TaskStore.AppDir, "WebView2");
            Directory.CreateDirectory(userDataDir);

            var env = await CoreWebView2Environment.CreateAsync(null, userDataDir);
            await WebView.EnsureCoreWebView2Async(env);

            WebView.CoreWebView2.Settings.IsStatusBarEnabled = false;
            WebView.CoreWebView2.Settings.UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";
            ExtractedUserAgent = WebView.CoreWebView2.Settings.UserAgent;

            WebView.CoreWebView2.NavigationCompleted += WebView_NavigationCompleted;
            WebView.CoreWebView2.DownloadStarting += WebView_DownloadStarting;

            LoadingOverlay.Visibility = Visibility.Collapsed;
            if (!Uri.TryCreate(_task.Url, UriKind.Absolute, out var target) ||
                (target.Scheme != Uri.UriSchemeHttp && target.Scheme != Uri.UriSchemeHttps))
            {
                StatusText.Text = "This download has no valid page URL to solve.";
                return;
            }
            if (LooksLikeDirectFile(target))
            {
                StatusText.Text = "This looks like a direct file link (not a page). If it shows \"You have been blocked\", the signed link expired or needs browser cookies/Referer: get a fresh link from the original page instead of solving here.";
            }
            WebView.Source = target;
        }
        catch (Exception ex)
        {
            // Never modal here: a failed init (e.g. E_ABORT on an offscreen /
            // closing window, missing WebView2 runtime) is an inline status,
            // so automation runs are never blocked by a popup.
            LoadingOverlay.Visibility = Visibility.Collapsed;
            StatusText.Text = Services.UserFriendlyError.For(ex);
        }
    }

    private void DetachWebView()
    {
        try
        {
            if (WebView.CoreWebView2 is not null)
            {
                WebView.CoreWebView2.NavigationCompleted -= WebView_NavigationCompleted;
                WebView.CoreWebView2.DownloadStarting -= WebView_DownloadStarting;
            }
        }
        catch { }
        try { WebView.Dispose(); } catch { }
    }

    private bool TryComplete(bool result)
    {
        // The user may have closed the window mid-callback: setting
        // DialogResult on a closed window throws InvalidOperationException.
        try
        {
            if (!IsLoaded && !IsVisible)
                return false;
            DialogResult = result;
            Close();
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private async void WebView_NavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (WebView.CoreWebView2 is null) return;

        string currentUrl = WebView.Source.ToString();
        FinalRedirectUrl = currentUrl;

        // Extract all cookies for target URL domain
        await CaptureCookiesAsync(currentUrl);
        if (ClearanceCaptured)
            return;
        // No cf_clearance yet: check whether the page is a hard WAF block
        // ("You have been blocked") rather than a solvable checkbox, so the
        // user isn't left staring at a block with "solve" instructions.
        await FlagHardBlockAsync();
    }

    private async void WebView_DownloadStarting(object? sender, CoreWebView2DownloadStartingEventArgs e)
    {
        // If WebView2 triggers a browser download, cancel the browser download.
        // Only treat it as solved when a cf_clearance cookie was actually
        // captured: otherwise a direct file hit just requeues the same blocked
        // URL and the download never starts (solve loop).
        e.Cancel = true;
        // e.ResultFilePath is a suggested LOCAL file path, not a URL: swapping the
        // task's Url to it would corrupt the download. Use the download's remote URI
        // (the post-redirect direct link) instead.
        string? remoteUri = e.DownloadOperation.Uri;
        string candidate = string.IsNullOrWhiteSpace(remoteUri) ? WebView.Source.ToString() : remoteUri;

        // Clearance cookies live on the original host, not the redirect target.
        await CaptureCookiesAsync(WebView.Source.ToString());
        if (!ClearanceCaptured)
        {
            StatusText.Text = "Browser started the file without Cloudflare clearance: if the download still fails, the link needs a refresh (fresh URL + cookies/Referer from the original page).";
            FinalRedirectUrl = candidate;
            return;
        }
        FinalRedirectUrl = candidate;
        TryComplete(true);
    }

    private async Task CaptureCookiesAsync(string targetUrl)
    {
        try
        {
            if (WebView.CoreWebView2 is null) return;

            var cookies = await WebView.CoreWebView2.CookieManager.GetCookiesAsync(targetUrl);
            if (cookies is null || cookies.Count == 0) return;

            var parts = cookies.Select(c => $"{c.Name}={c.Value}");
            ExtractedCookies = string.Join("; ", parts);

            bool hasClearance = cookies.Any(c => c.Name.Equals("cf_clearance", StringComparison.OrdinalIgnoreCase));
            if (hasClearance)
            {
                StatusText.Text = "Cloudflare clearance captured successfully! Resuming download...";
                ClearanceCaptured = true;
                TryComplete(true);
            }
        }
        catch
        {
            // Ignore capture errors
        }
    }

    private async void ApplyClearance_Click(object sender, RoutedEventArgs e)
    {
        await CaptureCookiesAsync(WebView.Source.ToString());
        if (!ClearanceCaptured)
        {
            MessageBox.Show(this,
                "No Cloudflare clearance cookie (cf_clearance) was found. The page is still blocked (\"You have been blocked\" cannot be solved here). Get a fresh link from the original page in your browser: or capture via the WDM extension so cookies + Referer are sent: then use Refresh Link.",
                "Still blocked", MessageBoxButton.OK, MessageBoxImage.Warning);
            await FlagHardBlockAsync();
            return;
        }
        TryComplete(true);
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private static bool LooksLikeDirectFile(Uri target)
    {
        string path = target.AbsolutePath ?? "";
        return path.EndsWith(".mkv", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".avi", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".mov", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".webm", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".rar", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".7z", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Inspects the rendered page text for a hard WAF block and, when
    /// found, rewrites the header so the user knows solving here is futile.</summary>
    private async Task FlagHardBlockAsync()
    {
        try
        {
            if (WebView.CoreWebView2 is null) return;
            string json = await WebView.CoreWebView2.ExecuteScriptAsync(
                "() => (document && document.body && document.body.innerText || '').slice(0, 4000)");
            string text = System.Text.Json.JsonSerializer.Deserialize<string>(json) ?? "";
            if (text.IndexOf("you have been blocked", StringComparison.OrdinalIgnoreCase) >= 0
                || text.IndexOf("attention required", StringComparison.OrdinalIgnoreCase) >= 0
                || (text.IndexOf("ray id", StringComparison.OrdinalIgnoreCase) >= 0
                    && text.IndexOf("cloudflare", StringComparison.OrdinalIgnoreCase) >= 0))
            {
                StatusText.Text = "This page shows \"You have been blocked\": a hard site block, not a solvable check. Close this window and get a fresh link from the original page (or capture via the WDM extension), then use Refresh Link.";
            }
        }
        catch
        {
            // Best-effort hint only; the block page stays visible regardless.
        }
    }
}
