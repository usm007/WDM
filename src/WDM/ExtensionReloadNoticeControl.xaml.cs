using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using WDM.Services;

namespace WDM;

public partial class ExtensionReloadNoticeControl : UserControl
{
    public event EventHandler? CloseRequested;

    public ExtensionReloadNoticeControl()
    {
        InitializeComponent();
    }

    public void Initialize(string oldVersion, string newVersion)
    {
        TitleText.Text = $"Extension Updated (v{oldVersion} → v{newVersion})";
        SubtitleText.Text = $"The WDM browser extension has been updated to version {newVersion}. Please reload it in your browser to activate all improvements.";
    }

    private void CopyPath_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string extDir = BrowserIntegration.DeployDir;
            Clipboard.SetText(extDir);
            FeedbackText.Text = "Extension path copied to clipboard!";
        }
        catch (Exception ex)
        {
            FeedbackText.Text = "Could not copy: " + ex.Message;
        }
    }

    private void OpenExtensions_Click(object sender, RoutedEventArgs e)
    {
        BrowserIntegration.OpenExtensionsPage();
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }
}
