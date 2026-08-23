using System;
using System.Windows;
using WDM.Services;

namespace WDM;

public partial class DeleteConfirmDialog : Window
{
    public DeleteConfirmDialog(string message, bool diskChecked)
    {
        InitializeComponent();
        MessageText.Text = message;
        DiskCheckBox.IsChecked = diskChecked;
        Loaded += (_, _) => CancelButton.Focus();
    }

    public bool DeleteFromDisk => DiskCheckBox.IsChecked == true;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        ThemeService.ApplyTitleBar(this);
    }

    private void DeleteClick(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }

    private void CancelClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}