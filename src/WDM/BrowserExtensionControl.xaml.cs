using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using WDM.Services;

namespace WDM;

public partial class BrowserExtensionControl : UserControl
{
    public event EventHandler? DoneRequested;

    public BrowserExtensionControl()
    {
        InitializeComponent();
    }

    private void SetupChrome_Click(object sender, RoutedEventArgs e)
    {
        // Deploy to local dir and copy path in one shot.
        string path;
        try
        {
            path = BrowserIntegration.DeployExtension();
            Clipboard.SetText(path);
        }
        catch (Exception ex)
        {
            FeedbackText.Text = $"Could not prepare extension folder: {ex.Message}";
            return;
        }

        try
        {
            BrowserIntegration.OpenExtensionsPage();
        }
        catch (Exception ex)
        {
            FeedbackText.Text = $"Could not open the extensions page: {ex.Message}";
        }

        ChromeStepsPanel.Visibility = System.Windows.Visibility.Visible;
        SetupChromeBtn.Content = "Open Extensions Page Again  \u2192";
    }

    private void OpenFirefoxAddonPage_Click(object sender, RoutedEventArgs e)
    {
        BrowserIntegration.OpenFirefoxAddonPage();
    }

    private void OpenMoreHelp_Click(object sender, RoutedEventArgs e)
    {
        BrowserIntegration.OpenExtensionGuide();
    }

    private void CloseClick(object sender, RoutedEventArgs e)
    {
        DoneRequested?.Invoke(this, EventArgs.Empty);
    }
}
