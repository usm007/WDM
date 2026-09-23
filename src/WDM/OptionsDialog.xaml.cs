using System;
using System.Windows;
using WDM.ViewModels;

namespace WDM;

public partial class OptionsDialog : Wpf.Ui.Controls.FluentWindow
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
    }

    public void SwitchTab(string tag) => SettingsControl.SwitchTab(tag);

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        WDM.Services.ThemeService.ApplyTitleBar(this);
    }
}
