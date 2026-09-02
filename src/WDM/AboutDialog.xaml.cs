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
            // Prefer Velopack delta when installed via Velopack
            if (VelopackUpdateService.IsVelopackInstalled)
            {
                var vUpdate = await VelopackUpdateService.CheckForUpdatesAsync();
                if (vUpdate is not null)
                {
                    var semVer = vUpdate.TargetFullRelease.Version;
                    var target = VelopackUpdateService.ToSystemVersion(semVer);
                    var synthetic = new ReleaseInfo($"v{target}", target, $"WDM {target}", $"https://github.com/usm007/WDM/releases/tag/v{target}", $"Delta update to {target} (patch-only).", DateTime.UtcNow, null);
                    UpdateStatusText.Text = "";
                    var dialog = new UpdateAvailableDialog(synthetic, vUpdate) { Owner = this };
                    dialog.ShowDialog();
                    return;
                }
            }

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