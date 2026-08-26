using System;
using System.Windows;
using WDM.Services;

namespace WDM;

public partial class ExtensionReloadNoticeDialog : Window
{
    public ExtensionReloadNoticeDialog(string oldVersion, string newVersion)
    {
        InitializeComponent();
        TitleText.Text = $"WDM Updated to v{newVersion}";
        SubtitleText.Text = string.IsNullOrWhiteSpace(oldVersion)
            ? "Please reload your browser extension to apply the latest updates."
            : $"Updated from v{oldVersion} → v{newVersion}. Please reload your browser extension.";
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        ThemeService.ApplyTitleBar(this);
    }

    private void OpenExtensions_Click(object sender, RoutedEventArgs e)
    {
        BrowserIntegration.OpenExtensionsPage();
        FeedbackText.Text = "Opened extensions page & copied URL";
    }

    private void CopyPath_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string path = BrowserIntegration.DeployExtension();
            Clipboard.SetText(path);
            FeedbackText.Text = "Extension path copied to clipboard!";
        }
        catch
        {
            FeedbackText.Text = "Could not copy to clipboard.";
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
