using System.IO;
using System.Linq;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using WDM.Models;
using WDM.Services;

namespace WDM;

/// <summary>Embedded browser shown when an embed host demands human interaction
/// (captcha, device attestation, login) before releasing its stream. On retry the
/// solved session cookies are handed back so the embed resolver can re-run.</summary>
public partial class EmbedInteractionWindow : Wpf.Ui.Controls.FluentWindow
{
    private WebView2? _webView;
    private readonly DownloadTask _task;
    private readonly string _pageUrl;

    public string? ExtractedCookies { get; private set; }

    public EmbedInteractionWindow(DownloadTask task, string pageUrl)
    {
        _task = task;
        _pageUrl = pageUrl;
        InitializeComponent();
        Title = $"Browser check — {task.DisplayFileName}";
        Loaded += OnLoaded;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        Services.ThemeService.ApplyTitleBar(this);
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        string? runtimeVersion = null;
        try
        {
            runtimeVersion = CoreWebView2Environment.GetAvailableBrowserVersionString();
        }
        catch (Exception ex)
        {
            StatusText.Text = "Microsoft Edge WebView2 Runtime is required but is not installed. "
                + "Install it from https://developer.microsoft.com/microsoft-edge/webview2/ (" + ex.Message + ")";
            StatusText.Foreground = (System.Windows.Media.Brush)(TryFindResource("Brush.Danger") ?? System.Windows.Media.Brushes.Red);
            return;
        }

        try
        {
            _webView = new WebView2();
            WebViewContainer.Children.Add(_webView);

            string userDataDir = Path.Combine(TaskStore.AppDir, "WebView2");
            Directory.CreateDirectory(userDataDir);
            var env = await CoreWebView2Environment.CreateAsync(null, userDataDir);
            await _webView.EnsureCoreWebView2Async(env);

            var core = _webView.CoreWebView2;
            if (core != null)
            {
                core.Settings.AreDefaultContextMenusEnabled = false;
                core.Settings.IsStatusBarEnabled = false;
                core.ProcessFailed += (_, args) =>
                    StatusText.Text = "Browser process failed (" + args.ProcessFailedKind + "). Click 'Reload page' to retry.";
                core.DocumentTitleChanged += (_, _) =>
                    Title = "WDM — " + (string.IsNullOrWhiteSpace(core.DocumentTitle) ? "Complete browser check" : core.DocumentTitle);
                core.Navigate(_pageUrl);
                StatusText.Text = "Loading " + _pageUrl;
            }
        }
        catch (Exception ex)
        {
            StatusText.Text = "WebView2 runtime is unavailable or failed to initialize"
                + (runtimeVersion is null ? "" : $" (found {runtimeVersion})") + ": " + ex.Message;
            StatusText.Foreground = (System.Windows.Media.Brush)(TryFindResource("Brush.Danger") ?? System.Windows.Media.Brushes.Red);
        }
    }

    private void Reload_Click(object sender, RoutedEventArgs e)
    {
        try { _webView?.CoreWebView2?.Navigate(_pageUrl); }
        catch (Exception ex) { StatusText.Text = "Failed to navigate: " + ex.Message; }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    private async void Retry_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_webView?.CoreWebView2 is not null)
            {
                var cookies = await _webView.CoreWebView2.CookieManager.GetCookiesAsync(_pageUrl);
                if (cookies is { Count: > 0 })
                    ExtractedCookies = string.Join("; ", cookies.Select(c => $"{c.Name}={c.Value}"));
            }
        }
        catch
        {
            // Retry anyway — the check may be IP-based rather than cookie-based.
        }
        DialogResult = true;
    }
}
