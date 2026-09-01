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

    private void CopyPath_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string extDir = BrowserIntegration.DeployDir;
            Clipboard.SetText(extDir);
            FeedbackText.Text = "Folder path copied to clipboard!";
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

    private void OpenFirefoxAddonPage_Click(object sender, RoutedEventArgs e)
    {
        BrowserIntegration.OpenFirefoxAddonPage();
    }

    private void CloseClick(object sender, RoutedEventArgs e)
    {
        DoneRequested?.Invoke(this, EventArgs.Empty);
    }
}
