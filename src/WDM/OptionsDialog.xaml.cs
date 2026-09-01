using System;
using System.Windows;
using WDM.ViewModels;

namespace WDM;

public partial class OptionsDialog : Window
{
    public OptionsDialog(MainViewModel viewModel)
    {
        InitializeComponent();
        SettingsControl.Initialize(viewModel);
        SettingsControl.CloseRequested += (_, _) =>
        {
            DialogResult = true;
            Close();
        };
        SettingsControl.OpenExtensionHelperRequested += (_, _) =>
        {
            var helper = new BrowserExtensionDialog { Owner = this };
            helper.ShowDialog();
        };
    }

    public void SwitchTab(string tag) => SettingsControl.SwitchTab(tag);

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        WDM.Services.ThemeService.ApplyTitleBar(this);
    }
}
