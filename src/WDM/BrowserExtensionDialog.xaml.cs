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
}