using System;
using System.Windows;
using Velopack;
using WDM.Services;

namespace WDM;

public partial class UpdateAvailableDialog : Window
{
    private readonly ReleaseInfo _release;
    private readonly UpdateInfo? _velopackUpdate;

    public UpdateAvailableDialog(ReleaseInfo release, UpdateInfo? velopackUpdate = null)
    {
        InitializeComponent();
        _release = release;
        _velopackUpdate = velopackUpdate;
        VersionLine.Text = $"WDM {release.Version} is available";
        if (velopackUpdate is not null)
        {
            DetailsText.Text = $"Current: {VelopackUpdateService.CurrentVersion}  →  New: {release.Version}{Environment.NewLine}" +
                               $"Delta update (patch-only, ~2-5 MB) — no full installer needed.{Environment.NewLine}" +
                               (string.IsNullOrWhiteSpace(release.Body) ? "" : $"{Environment.NewLine}{release.Body.Trim()}");
            InstallButton.Content = "Download Delta & Restart";
        }
        else
        {
            DetailsText.Text = string.IsNullOrWhiteSpace(release.Body)
                ? $"Current version: {UpdateChecker.CurrentVersion}{Environment.NewLine}" +
                  $"New version: {release.Version}{Environment.NewLine}{Environment.NewLine}" +
                  "Click Download & Install to automatically update and restart WDM."
                : $"{release.Body.Trim()}{Environment.NewLine}{Environment.NewLine}" +
                  $"Current: {UpdateChecker.CurrentVersion}  →  New: {release.Version}";
        }
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        WDM.Services.ThemeService.ApplyTitleBar(this);
    }

    private void Window_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ButtonState == System.Windows.Input.MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private async void InstallClick(object sender, RoutedEventArgs e)
    {
        InstallButton.IsEnabled = false;
        LaterButton.IsEnabled = false;

        // Velopack delta path: patch-only update package, NOT full installer — shows progress bar and restarts
        if (_velopackUpdate is not null)
        {
            ProgressPanel.Visibility = Visibility.Visible;
            ProgressStatusText.Text = "Downloading update package…";
            ProgressDetailText.Text = $"Update package for { _release.Version } (delta, ~15 KB - 5 MB) — not the full installer.";
            DownloadProgressBar.Value = 0;
            ProgressPctText.Text = "0%";
            DetailsText.Text = $"Preparing delta update to { _release.Version }…";
            try
            {
                await VelopackUpdateService.DownloadUpdatesAsync(_velopackUpdate, pct =>
                    Dispatcher.Invoke(() =>
                    {
                        DownloadProgressBar.Value = pct;
                        ProgressPctText.Text = $"{pct}%";
                        ProgressStatusText.Text = pct < 100 ? "Downloading update package…" : "Download complete — applying…";
                    }));
                ProgressStatusText.Text = "Applying update…";
                ProgressDetailText.Text = "WDM will restart automatically to apply the patch.";
                DownloadProgressBar.Value = 100;
                ProgressPctText.Text = "100%";
                DetailsText.Text = "Applying update — WDM will restart…";
                await Task.Delay(600);
                VelopackUpdateService.ApplyAndRestart(_velopackUpdate.TargetFullRelease);
                DialogResult = true;
                Close();
            }
            catch (Exception ex)
            {
                ProgressPanel.Visibility = Visibility.Collapsed;
                DetailsText.Text = $"Delta download failed: {ex.Message}";
                InstallButton.IsEnabled = true;
                LaterButton.IsEnabled = true;
            }
            return;
        }

        // Fallback: full installer (only when not Velopack-installed)
        ProgressPanel.Visibility = Visibility.Visible;
        ProgressStatusText.Text = "Downloading full installer…";
        ProgressDetailText.Text = "Fallback path — Velopack not available, downloading full setup.";
        DownloadProgressBar.Value = 0;
        ProgressPctText.Text = "0%";
        DetailsText.Text = "Downloading the full installer…";
        try
        {
            string installer = await UpdateChecker.DownloadInstallerAsync(_release, progress =>
                Dispatcher.Invoke(() =>
                {
                    int pct = (int)Math.Round(progress * 100);
                    DownloadProgressBar.Value = pct;
                    ProgressPctText.Text = $"{pct}%";
                    ProgressStatusText.Text = $"Downloading full installer… {pct}%";
                }));
            ProgressStatusText.Text = "Launching installer…";
            DownloadProgressBar.Value = 100;
            ProgressPctText.Text = "100%";
            UpdateChecker.LaunchInstaller(installer);
            await Task.Delay(500);
            DialogResult = true;
            Close();
        }
        catch (Exception ex)
        {
            ProgressPanel.Visibility = Visibility.Collapsed;
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