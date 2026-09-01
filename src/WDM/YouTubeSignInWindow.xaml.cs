using System.IO;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using WDM.Services;

namespace WDM;

public partial class YouTubeSignInWindow : Window
{
    private WebView2? _webView;

    public YouTubeSignInWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        Services.ThemeService.ApplyTitleBar(this);
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Pre-flight: verify an installed WebView2 Runtime exists BEFORE creating any
        // native resources. A missing/incompatible runtime can fail-fast the whole
        // process during control creation; surfacing it here keeps WDM alive.
        string? runtimeVersion = null;
        try
        {
            runtimeVersion = CoreWebView2Environment.GetAvailableBrowserVersionString();
        }
        catch (Exception ex)
        {
            StatusText.Text = "Microsoft Edge WebView2 Runtime is required for sign-in but is not installed. "
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
                // Keep the host alive if the browser process dies (network reset,
                // antivirus interference); let the user reload instead of crashing.
                core.ProcessFailed += (_, args) =>
                    StatusText.Text = "Browser process failed (" + args.ProcessFailedKind + "). Click 'Reload YouTube' to retry.";
                core.DocumentTitleChanged += (_, _) =>
                    Title = "WDM — " + (string.IsNullOrWhiteSpace(core.DocumentTitle) ? "Sign in with YouTube" : core.DocumentTitle);
                NavigateHome();
            }
        }
        catch (Exception ex)
        {
            StatusText.Text = "WebView2 runtime is unavailable or failed to initialize"
                + (runtimeVersion is null ? "" : $" (found {runtimeVersion})") + ": " + ex.Message;
            StatusText.Foreground = (System.Windows.Media.Brush)(TryFindResource("Brush.Danger") ?? System.Windows.Media.Brushes.Red);
        }
    }

    private void NavigateHome()
    {
        try
        {
            _webView?.CoreWebView2?.Navigate("https://www.youtube.com/");
        }
        catch (Exception ex)
        {
            StatusText.Text = "Failed to navigate: " + ex.Message;
            StatusText.Foreground = (System.Windows.Media.Brush)(TryFindResource("Brush.Danger") ?? System.Windows.Media.Brushes.Red);
        }
    }

    private void Reload_Click(object sender, RoutedEventArgs e) => NavigateHome();

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    private async void UseSession_Click(object sender, RoutedEventArgs e)
    {
        if (_webView?.CoreWebView2 is null)
        {
            StatusText.Text = "Browser is not ready yet.";
            return;
        }

        try
        {
            var (path, count, signedIn) = await YouTubeCookieExporter.ExportAsync(_webView.CoreWebView2.CookieManager);
            if (!signedIn)
            {
                StatusText.Text = "You're not signed in yet — sign in with your Google account inside this window first.";
                return;
            }

            var s = TaskStore.LoadSettings();
            s.YouTubeBrowserCookies = "wdm-native";
            TaskStore.SaveSettings(s);
            DialogResult = true;
        }
        catch (Exception ex)
        {
            StatusText.Text = "Failed to export cookies: " + ex.Message;
            StatusText.Foreground = (System.Windows.Media.Brush)(TryFindResource("Brush.Danger") ?? System.Windows.Media.Brushes.Red);
        }
    }
}
