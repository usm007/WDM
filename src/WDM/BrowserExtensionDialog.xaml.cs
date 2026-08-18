using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using WDM.Services;

namespace WDM;

public partial class BrowserExtensionDialog : Window
{
    private readonly CaptureServer? _server;

    public BrowserExtensionDialog(CaptureServer? server = null)
    {
        InitializeComponent();
        _server = server;

        if (_server is not null)
        {
            _server.ExtensionConnected += OnExtensionConnected;
            UpdateConnectionStatus(_server.IsConnected);
        }

        // Always ensure extension is deployed to AppData on load
        BrowserIntegration.DeployExtension();
        UpdateSigningStatus();
        UpdateStoreStatus();
    }

    private void UpdateStoreStatus()
    {
        if (ChromiumStatusText is not null)
        {
            var chrome = BrowserIntegration.DetectInstalledBrowsers()
                .FirstOrDefault(b => b.Kind == BrowserKind.Chromium);
            bool registered = chrome is not null && BrowserIntegration.IsChromiumStoreRegistered(chrome);
            string storeId = chrome is not null && chrome.Name.Contains("Edge", StringComparison.OrdinalIgnoreCase)
                ? BrowserIntegration.EdgeAddOnsId
                : BrowserIntegration.ChromiumWebStoreId;

            ChromiumStatusText.Text = registered
                ? "Store install is active — open the browser and confirm the “Enable extension” prompt once."
                : string.IsNullOrWhiteSpace(storeId)
                    ? "Chrome/Edge: “Install Now” loads the extension right away (session only). For a permanent install, publish the add-on once to the store, then use “Permanent Install (Store)”."
                    : "Store install is set up — next browser launch will ask you to confirm the extension.";
        }
    }

    private void UpdateSigningStatus()
    {
        string? note = null;
        if (FirefoxStatusText is not null)
        {
            note = BrowserIntegration.IsFirefoxXpiSigned()
                ? "Signed XPI bundled — Firefox will auto-install on next launch."
                : "XPI is unsigned — Firefox will block it. Upload once to AMO (self-distribution) and re-bundle the signed file.";
            FirefoxStatusText.Text = note;
        }
    }

    private void OnExtensionConnected()
    {
        Dispatcher.BeginInvoke(() => UpdateConnectionStatus(true));
    }

    private void UpdateConnectionStatus(bool connected)
    {
        if (connected)
        {
            StatusDot.Background = (Brush)FindResource("Brush.Success");
            StatusTitleText.Text = "Extension Connected & Active! 🎉";
            StatusTitleText.Foreground = (Brush)FindResource("Brush.Success");
            StatusSubText.Text = "Browser downloads will now automatically route to WDM.";
            FeedbackText.Text = "Connected! You can close this window now.";
        }
        else
        {
            StatusDot.Background = (Brush)FindResource("Brush.Warning");
            StatusTitleText.Text = "Waiting for Chrome extension...";
            StatusTitleText.Foreground = (Brush)FindResource("Brush.Warning");
            StatusSubText.Text = "WDM local server listening on http://127.0.0.1:17530";
        }
    }

    private void LaunchChrome_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var browsers = BrowserIntegration.DetectInstalledBrowsers();
            var chrome = browsers.FirstOrDefault(b => b.Name.Contains("Chrome")) ?? browsers.FirstOrDefault();
            if (chrome is null)
            {
                MessageBox.Show("No supported browser detected on your system.", "WDM Setup", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (chrome.Kind == BrowserKind.Firefox)
            {
                string result = BrowserIntegration.InstallFirefoxViaPolicy(chrome);
                FeedbackText.Text = result;
                return;
            }

            string chromeResult = BrowserIntegration.LoadChromiumViaCdp(chrome);
            FeedbackText.Text = chromeResult;
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to launch browser: {ex.Message}", "WDM Setup Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void InstallFirefox_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var firefox = BrowserIntegration.DetectInstalledBrowsers().FirstOrDefault(b => b.Kind == BrowserKind.Firefox);
            if (firefox is null)
            {
                MessageBox.Show("Mozilla Firefox is not installed.", "WDM Setup", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            string result = BrowserIntegration.InstallFirefoxViaPolicy(firefox);
            FeedbackText.Text = result;
            BrowserIntegration.OpenExtensionFolder();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to register Firefox extension: {ex.Message}", "WDM Setup Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void PreinstallChrome_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var browsers = BrowserIntegration.DetectInstalledBrowsers();
            var chrome = browsers.FirstOrDefault(b => b.Name.Contains("Chrome")) ?? browsers.FirstOrDefault();
            if (chrome is null)
            {
                MessageBox.Show("No supported browser detected on your system.", "WDM Setup", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            string result = BrowserIntegration.PreinstallChromium(chrome);
            FeedbackText.Text = result;
            UpdateStoreStatus();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to pre-install extension: {ex.Message}", "WDM Setup Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            BrowserIntegration.OpenExtensionFolder();
            FeedbackText.Text = "Opened Extension folder in File Explorer.";
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not open folder: {ex.Message}", "WDM", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void CopyPath_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string path = BrowserIntegration.DeployExtension();
            Clipboard.SetText(path);
            CopyPathButton.Content = "✓ Path Copied!";
            FeedbackText.Text = $"Copied: {path}";
        }
        catch
        {
            FeedbackText.Text = "Clipboard access failed.";
        }
    }

    private void OpenExtensions_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            BrowserIntegration.OpenExtensionsPage();
            FeedbackText.Text = "Opened chrome://extensions page.";
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not open extensions page: {ex.Message}", "WDM", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void CloseClick(object sender, RoutedEventArgs e)
    {
        Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        if (_server is not null)
        {
            _server.ExtensionConnected -= OnExtensionConnected;
        }
        base.OnClosed(e);
    }
}
