using System;
using System.Windows;

namespace WDM;

public partial class ExtensionReloadNoticeDialog : Window
{
    public ExtensionReloadNoticeDialog(string oldVersion, string newVersion)
    {
        InitializeComponent();
        NoticeControl.Initialize(oldVersion, newVersion);
        NoticeControl.CloseRequested += (_, _) => Close();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        Services.ThemeService.ApplyTitleBar(this);
    }
}
