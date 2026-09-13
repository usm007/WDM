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

    public CloudflareChallengeWindow(DownloadTask task)
    {
        InitializeComponent();
        _task = task;
        Title = $"Cloudflare Protection — {task.DisplayFileName}";

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
                MessageBox.Show(this, "This download has no valid page URL to solve.", "WebView Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            WebView.Source = target;
        }
        catch (Exception ex)
        {
            LoadingOverlay.Visibility = Visibility.Collapsed;
            MessageBox.Show(this, $"Failed to initialize browser engine: {ex.Message}", "WebView Error", MessageBoxButton.OK, MessageBoxImage.Error);
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
        // The user may have closed the window mid-callback — setting
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
    }

    private async void WebView_DownloadStarting(object? sender, CoreWebView2DownloadStartingEventArgs e)
    {
        // If WebView2 triggers a browser download, cancel the browser download and capture the clearance!
        e.Cancel = true;
        // e.ResultFilePath is a suggested LOCAL file path, not a URL — swapping the
        // task's Url to it would corrupt the download. Use the download's remote URI
        // (the post-redirect direct link) instead.
        string? remoteUri = e.DownloadOperation.Uri;
        FinalRedirectUrl = string.IsNullOrWhiteSpace(remoteUri) ? WebView.Source.ToString() : remoteUri;

        // Clearance cookies live on the original host, not the redirect target.
        await CaptureCookiesAsync(WebView.Source.ToString());
        ClearanceCaptured = true;
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
        TryComplete(true);
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
