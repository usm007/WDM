using System;
using System.Windows;
using WDM.Services;

namespace WDM;

/// <summary>Short native extension setup guide (separate window). The buttons do
/// the machine steps (copy path, open pages); the two remaining clicks happen
/// in the browser and are listed as text.</summary>
public partial class ExtensionGuideWindow : Window
{
    public ExtensionGuideWindow()
    {
        InitializeComponent();
    }

    protected override void OnSourceInitialized(System.EventArgs e)
    {
        base.OnSourceInitialized(e);
        ThemeService.ApplyTitleBar(this);
    }

    private void Window_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ButtonState == System.Windows.Input.MouseButtonState.Pressed)
            DragMove();
    }

    private void CopyPath_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string path = BrowserIntegration.DeployExtension();
            Clipboard.SetText(path);
            CopyFeedbackText.Text = "Copied ✓ — paste it into the “Load unpacked” dialog.";
            CopyFeedbackText.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            App.LogException(ex);
            CopyFeedbackText.Text = "Couldn't copy the path — please copy it by hand from Settings.";
            CopyFeedbackText.Visibility = Visibility.Visible;
        }
    }

    private void OpenPage_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            BrowserIntegration.OpenExtensionsPage();
        }
        catch (Exception ex)
        {
            App.LogException(ex);
            UserFriendlyError.ShowWarning(this, "Couldn't open page",
                "The extensions page couldn't be opened. Type chrome://extensions (or edge://extensions) by hand in your browser.");
        }
    }

    private void Firefox_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            BrowserIntegration.OpenFirefoxAddonPage();
        }
        catch (Exception ex)
        {
            App.LogException(ex);
            UserFriendlyError.ShowWarning(this, "Couldn't open page",
                "The Firefox store page couldn't be opened. Search for “WDM Download Catcher” by hand.");
        }
    }

    private void CloseClick(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
