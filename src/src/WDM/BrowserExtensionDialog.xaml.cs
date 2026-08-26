using System;
using System.Windows;
using WDM.Services;

namespace WDM;

public partial class BrowserExtensionDialog : Window
{
    public BrowserExtensionDialog()
    {
        InitializeComponent();
        BrowserIntegration.DeployExtension();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        WDM.Services.ThemeService.ApplyTitleBar(this);
    }

    private void CopyPath_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string path = BrowserIntegration.DeployExtension();
            Clipboard.SetText(path);
            CopyPathButton.Content = "✓ Copied!";
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
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not open the extensions page: {ex.Message}", "WDM", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OpenFirefoxAddonPage_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            BrowserIntegration.OpenFirefoxAddonPage();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not open the Add-ons page: {ex.Message}", "WDM", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void CloseClick(object sender, RoutedEventArgs e)
    {
        Close();
    }
}