using System;
using System.Windows;

namespace WDM;

public partial class BrowserExtensionDialog : Window
{
    public BrowserExtensionDialog()
    {
        InitializeComponent();
        ExtensionControl.DoneRequested += (_, _) => Close();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        Services.ThemeService.ApplyTitleBar(this);
    }

    private void Window_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ButtonState == System.Windows.Input.MouseButtonState.Pressed)
        {
            DragMove();
        }
    }
}