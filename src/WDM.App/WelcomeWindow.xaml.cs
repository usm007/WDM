using System.Diagnostics;
using System.Windows;

namespace WDM;

public partial class WelcomeWindow : Wpf.Ui.Controls.FluentWindow
{
    private readonly Services.AppSettings _settings;

    public WelcomeWindow(Services.AppSettings settings)
    {
        InitializeComponent();
        _settings = settings;

        if (_settings.EnableYouTubeDownloads && WDM.Services.EngineManager.IsReady)
        {
            ActivateBtn.Content = "Activated";
            ActivateBtn.IsEnabled = false;
            YouTubeBtn.IsEnabled = true;
        }

        if (_settings.YouTubeBrowserCookies == "wdm-native")
        {
            YouTubeBtn.Content = "Signed in — Click to re-authenticate...";
        }

        Loaded += (_, __) =>
        {
            try { string p = Services.BrowserIntegration.DeployExtension(); if (ExtensionPathBox != null) ExtensionPathBox.Text = p; } catch { }
        };
    }

    protected override void OnSourceInitialized(System.EventArgs e)
    {
        base.OnSourceInitialized(e);
        WDM.Services.ThemeService.ApplyTitleBar(this);
    }

    private void FirefoxBtn_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Services.BrowserIntegration.OpenFirefoxAddonPage();
        }
        catch (System.Exception ex)
        {
            App.LogException(ex);
            Services.ErrorDialogs.ShowWarning(this, "Couldn't open page", "The browser page couldn't be opened. Please try again.");
        }
    }

    private void SetupChrome_Click(object sender, RoutedEventArgs e)
    {
        string path;
        try
        {
            path = Services.BrowserIntegration.DeployExtension();
            Clipboard.SetText(path);
            if (ExtensionPathBox != null) ExtensionPathBox.Text = path;
        }
        catch (Exception ex)
        {
            App.LogException(ex);
            CopyFeedbackText.Text = "The extension folder couldn't be prepared. Please try again.";
            return;
        }

        try { Services.BrowserIntegration.OpenExtensionsPage(); }
        catch (Exception ex)
        {
            App.LogException(ex);
            CopyFeedbackText.Text = "The extensions page couldn't be opened. Please open it by hand in your browser.";
        }

        ChromeStepsPanel.Visibility = System.Windows.Visibility.Visible;
        if (CopyPathLabel != null) CopyPathLabel.Text = "Copied ✓";
        SetupChromeBtn.Content = "Open Extensions Page Again  →";
    }

    private void CopyPath_Click(object sender, RoutedEventArgs e)
    {
        string path;
        try { path = Services.BrowserIntegration.DeployExtension(); if (ExtensionPathBox != null) ExtensionPathBox.Text = path; }
        catch { path = Services.BrowserIntegration.DeployDir; }
        try
        {
            Clipboard.SetText(path);
            ChromeStepsPanel.Visibility = System.Windows.Visibility.Visible;
            if (CopyPathLabel != null) CopyPathLabel.Text = "Copied ✓";
            var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
            timer.Tick += (_, __) => { timer.Stop(); if (CopyPathLabel != null) CopyPathLabel.Text = "Copy"; };
            timer.Start();
        }
        catch (Exception ex)
        {
            App.LogException(ex);
            CopyFeedbackText.Text = "Couldn't copy the path — please copy it by hand from the box above.";
        }
    }

    private void YouTubeBtn_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var window = new YouTubeSignInWindow { Owner = this };
            if (window.ShowDialog() == true)
            {
                YouTubeBtn.Content = "Signed in — Click to re-authenticate...";
                _settings.YouTubeBrowserCookies = "wdm-native";
                MessageBox.Show(this, "Successfully signed in to YouTube natively and exported your session. Private and age-restricted videos should now download normally.", "Sign-In Complete", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
        catch (System.Exception ex)
        {
            App.LogException(ex);
            Services.ErrorDialogs.ShowError(this, "Couldn't open sign-in", "The YouTube sign-in window couldn't be opened. Please try again.");
        }
    }

    private async void ActivateBtn_Click(object sender, RoutedEventArgs e)
    {
        _settings.EnableYouTubeDownloads = true;
        Services.TaskStore.SaveSettings(_settings);
        ActivateBtn.IsEnabled = false;
        ProgressCard.Visibility = Visibility.Visible;
        ProgressBar.Value = 0;
        ProgressPctText.Text = "0%";
        ProgressStatusText.Text = "Initializing plugin setup...";

        try
        {
            var progress = new System.Progress<Services.EngineProgress>(p =>
            {
                Dispatcher.Invoke(() =>
                {
                    ProgressStatusText.Text = p.StatusText;
                    double pct = System.Math.Clamp(p.ProgressFraction * 100, 0, 100);
                    ProgressBar.Value = pct;
                    ProgressPctText.Text = $"{pct:F0}%";
                });
            });

            await Services.EngineManager.EnsureAsync(progress);
            ActivateBtn.Content = "Activated";
            YouTubeBtn.IsEnabled = true;
        }
        catch (System.Exception ex)
        {
            App.LogException(ex);
            Services.ErrorDialogs.ShowError(this, "Couldn't set up YouTube downloads", "The YouTube downloader couldn't be set up. Check your internet connection and try again.");
            _settings.EnableYouTubeDownloads = false;
            Services.TaskStore.SaveSettings(_settings);
            ActivateBtn.IsEnabled = true;
        }
        finally
        {
            ProgressCard.Visibility = Visibility.Collapsed;
        }
    }

    private void FinishBtn_Click(object sender, RoutedEventArgs e)
    {
        _settings.HasPromptedExtensionInstall = true;
        Close();
    }
}
