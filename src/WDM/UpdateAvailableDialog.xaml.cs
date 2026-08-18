using System;
using System.Windows;
using WDM.Services;

namespace WDM;

public partial class UpdateAvailableDialog : Window
{
    private readonly ReleaseInfo _release;

    public UpdateAvailableDialog(ReleaseInfo release)
    {
        InitializeComponent();
        _release = release;
        VersionLine.Text = $"WDM {release.Version} → click Download & Install to update.";
        DetailsText.Text = $"Current version: {UpdateChecker.CurrentVersion}{Environment.NewLine}" +
                           $"New version: {release.Version}{Environment.NewLine}{Environment.NewLine}" +
                           (string.IsNullOrWhiteSpace(release.Body) ? "" : release.Body.Trim() + Environment.NewLine + Environment.NewLine) +
                           "The installer downloads to your Temp folder and WDM restarts automatically after installation.";
    }

    private async void InstallClick(object sender, RoutedEventArgs e)
    {
        InstallButton.IsEnabled = false;
        LaterButton.IsEnabled = false;
        DetailsText.Text = "Downloading the new installer…";

        try
        {
            string installer = await UpdateChecker.DownloadInstallerAsync(_release);
            UpdateChecker.LaunchInstaller(installer);
            await Task.Delay(500);
            DialogResult = true;
            Close();
        }
        catch (Exception ex)
        {
            DetailsText.Text = $"Download failed: {ex.Message}";
            InstallButton.IsEnabled = true;
            LaterButton.IsEnabled = true;
        }
    }

    private void LaterClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}