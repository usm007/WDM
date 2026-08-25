using System.Diagnostics;
using System.Windows;

namespace WDM;

public partial class WelcomeWindow : Window
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
            MessageBox.Show($"Could not open the Add-ons page: {ex.Message}", "WDM", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void CopyPath_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string path = Services.BrowserIntegration.DeployExtension();
            Clipboard.SetText(path);
            CopyPathButton.Content = "✓ Copied!";
            CopyFeedbackText.Text = $"Copied: {path}";
        }
        catch
        {
            CopyFeedbackText.Text = "Clipboard access failed.";
        }
    }

    private void OpenExtensions_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Services.BrowserIntegration.OpenExtensionsPage();
            CopyFeedbackText.Text = "Opened browser! (Address 'chrome://extensions' copied to clipboard if tab is blank)";
        }
        catch (System.Exception ex)
        {
            MessageBox.Show($"Could not open the extensions page: {ex.Message}", "WDM", MessageBoxButton.OK, MessageBoxImage.Warning);
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
            MessageBox.Show(this, "Could not open YouTube Sign-In window: " + ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
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
            MessageBox.Show(this, "Failed to download YouTube engine plugins:\n" + ex.Message, "Engine Setup Error", MessageBoxButton.OK, MessageBoxImage.Error);
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
