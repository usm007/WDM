using System.IO;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using WDM.Services;

namespace WDM;

public partial class YouTubeSignInWindow : Wpf.Ui.Controls.FluentWindow
{
    private WebView2? _webView;

    public YouTubeSignInWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Closed += (_, _) => DetachWebView();
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
        // Pre-flight: the version query itself throws when no runtime exists.
        try
        {
            _ = CoreWebView2Environment.GetAvailableBrowserVersionString();
        }
        catch (Exception ex)
        {
            App.LogException(ex);
            StatusText.Text = "The built-in browser part isn't installed, so sign-in can't open. Please install the WebView2 Runtime from Microsoft, then try again.";
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
                core.ProcessFailed += Core_ProcessFailed;
                core.DocumentTitleChanged += Core_DocumentTitleChanged;
                NavigateHome();
            }
        }
        catch (Exception ex)
        {
            App.LogException(ex);
            StatusText.Text = "The built-in browser couldn't start. Please try again.";
            StatusText.Foreground = (System.Windows.Media.Brush)(TryFindResource("Brush.Danger") ?? System.Windows.Media.Brushes.Red);
        }
    }

    private void Core_ProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs args)
    {
        try
        {
            StatusText.Text = "The browser part closed unexpectedly. Click 'Reload YouTube' to try again.";
        }
        catch { }
    }

    private void Core_DocumentTitleChanged(object? sender, object e)
    {
        try
        {
            var core = _webView?.CoreWebView2;
            Title = "WDM: " + (string.IsNullOrWhiteSpace(core?.DocumentTitle) ? "Sign in with YouTube" : core.DocumentTitle);
        }
        catch { }
    }

    private void DetachWebView()
    {
        try
        {
            Loaded -= OnLoaded;
            if (_webView?.CoreWebView2 is not null)
            {
                _webView.CoreWebView2.ProcessFailed -= Core_ProcessFailed;
                _webView.CoreWebView2.DocumentTitleChanged -= Core_DocumentTitleChanged;
            }
        }
        catch { }
        try { _webView?.Dispose(); } catch { }
        _webView = null;
    }

    private void NavigateHome()
    {
        try
        {
            _webView?.CoreWebView2?.Navigate("https://www.youtube.com/");
        }
        catch (Exception ex)
        {
            App.LogException(ex);
            StatusText.Text = "The page couldn't be opened. Check your connection and try again.";
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
                StatusText.Text = "You're not signed in yet: sign in with your Google account inside this window first.";
                return;
            }

            var s = TaskStore.LoadSettings();
            s.YouTubeBrowserCookies = "wdm-native";
            TaskStore.SaveSettings(s);
            DialogResult = true;
        }
        catch (Exception ex)
        {
            App.LogException(ex);
            StatusText.Text = "Couldn't save your sign-in. Please try again.";
            StatusText.Foreground = (System.Windows.Media.Brush)(TryFindResource("Brush.Danger") ?? System.Windows.Media.Brushes.Red);
        }
    }
}
