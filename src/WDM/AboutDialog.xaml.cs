using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Documents;
using WDM.Services;

namespace WDM;

public partial class AboutDialog : Window
{
    public AboutDialog()
    {
        InitializeComponent();
        var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
        VersionText.Text = $"Version {(version?.Major ?? 1)}.{(version?.Minor ?? 0)}";
    }

    private void Link_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Hyperlink link && link.NavigateUri is Uri uri)
        {
            try
            {
                Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
            }
            catch
            {
                // Ignore navigation failures.
            }
        }
    }

    private async void CheckUpdateClick(object sender, RoutedEventArgs e)
    {
        CheckUpdateButton.IsEnabled = false;
        UpdateStatusText.Text = "Checking for updates…";
        try
        {
            var latest = await UpdateChecker.CheckLatestAsync();
            if (latest is null)
            {
                UpdateStatusText.Text = "Could not reach GitHub — try again later.";
            }
            else if (latest.Version is { } version && version.CompareTo(UpdateChecker.CurrentVersion) > 0)
            {
                UpdateStatusText.Text = "";
                var dialog = new UpdateAvailableDialog(latest) { Owner = this };
                dialog.ShowDialog();
            }
            else
            {
                UpdateStatusText.Text = "You are running the latest version.";
            }
        }
        catch (Exception ex)
        {
            UpdateStatusText.Text = $"Check failed: {ex.Message}";
        }
        finally
        {
            CheckUpdateButton.IsEnabled = true;
        }
    }

    private void CloseClick(object sender, RoutedEventArgs e)
    {
        Close();
    }
}