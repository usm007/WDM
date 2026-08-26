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
        VersionLine.Text = $"WDM {release.Version} is available";
        DetailsText.Text = string.IsNullOrWhiteSpace(release.Body)
            ? $"Current version: {UpdateChecker.CurrentVersion}{Environment.NewLine}" +
              $"New version: {release.Version}{Environment.NewLine}{Environment.NewLine}" +
              "Click Download & Install to automatically update and restart WDM."
            : $"{release.Body.Trim()}{Environment.NewLine}{Environment.NewLine}" +
              $"Current: {UpdateChecker.CurrentVersion}  →  New: {release.Version}";
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        WDM.Services.ThemeService.ApplyTitleBar(this);
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