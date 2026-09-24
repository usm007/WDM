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
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Ensure deploy dir exists and show the real path immediately so the
        // tutorial is useful even before any button is pressed.
        try
        {
            string path = BrowserIntegration.DeployExtension();
            if (ExtensionPathBox != null)
                ExtensionPathBox.Text = path;
        }
        catch
        {
            if (ExtensionPathBox != null)
                ExtensionPathBox.Text = BrowserIntegration.DeployDir;
        }
    }

    private void SetupChrome_Click(object sender, RoutedEventArgs e)
    {
        // Deploy, copy path, and open extensions page in one click.
        string path;
        try
        {
            path = BrowserIntegration.DeployExtension();
            Clipboard.SetText(path);
            if (ExtensionPathBox != null)
                ExtensionPathBox.Text = path;
        }
        catch (Exception ex)
        {
            App.LogException(ex);
            FeedbackText.Text = "The extension folder couldn't be prepared. Please try again.";
            return;
        }

        try
        {
            BrowserIntegration.OpenExtensionsPage();
        }
        catch (Exception ex)
        {
            App.LogException(ex);
            FeedbackText.Text = "The extensions page couldn't be opened. Please open it by hand in your browser.";
        }

        ChromeStepsPanel.Visibility = System.Windows.Visibility.Visible;
        CopyPathLabel.Text = "Copied ✓";
        FeedbackText.Text = "";
        // Update button label after first click so user knows they can reopen
        try { if (ChromeBtnLabel != null) ChromeBtnLabel.Text = "Open Extensions Page Again"; } catch { }
    }

    private void CopyPath_Click(object sender, RoutedEventArgs e)
    {
        string path = ExtensionPathBox?.Text?.Trim() ?? BrowserIntegration.DeployDir;
        if (string.IsNullOrWhiteSpace(path))
            path = BrowserIntegration.DeployDir;

        // Ensure folder exists before copying.
        try { path = BrowserIntegration.DeployExtension(); if (ExtensionPathBox != null) ExtensionPathBox.Text = path; } catch { }

        try
        {
            Clipboard.SetText(path);
            ChromeStepsPanel.Visibility = System.Windows.Visibility.Visible;
            CopyPathLabel.Text = "Copied ✓";
            FeedbackText.Text = "";
            // Reset label after 2.5s
            var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
            timer.Tick += (_, __) => { timer.Stop(); CopyPathLabel.Text = "Copy"; };
            timer.Start();
        }
        catch (Exception ex)
        {
            App.LogException(ex);
            FeedbackText.Text = "Couldn't copy the path — please copy it by hand from the box above.";
        }
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        try { BrowserIntegration.OpenExtensionFolder(); }
        catch (Exception ex)
        {
            App.LogException(ex);
            FeedbackText.Text = "The folder couldn't be opened. Please try again.";
        }
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
