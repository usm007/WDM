using System;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Documents;
using WDM.Services;

namespace WDM;

public partial class AboutDialog : Window
{
    private ReleaseInfo? _inlineRelease;
    private Velopack.UpdateInfo? _inlineVelopack;
    private bool _isDownloading;

    public AboutDialog()
    {
        InitializeComponent();
        var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
        VersionText.Text = $"Version {(version?.Major ?? 1)}.{(version?.Minor ?? 0)}.{(version?.Build ?? 0)}";
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        WDM.Services.ThemeService.ApplyTitleBar(this);
    }

    private void Window_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ButtonState == System.Windows.Input.MouseButtonState.Pressed)
            DragMove();
    }

    private void Link_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Hyperlink link && link.NavigateUri is Uri uri)
        {
            try { Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }); }
            catch { }
        }
    }

    private async void CheckUpdateClick(object sender, RoutedEventArgs e)
    {
        CheckUpdateButton.IsEnabled = false;
        UpdateStatusText.Text = "Checking for updates…";
        InlineUpdatePanel.Visibility = Visibility.Collapsed;
        UpdateActionStrip.Visibility = Visibility.Collapsed;
        DefaultStrip.Visibility = Visibility.Visible;
        try
        {
            // Velopack install → delta-only (truly silent); never fall back to Setup.exe which shows the "already installed" prompt
            if (VelopackUpdateService.IsVelopackInstalled)
            {
                var vUpdate = await VelopackUpdateService.CheckForUpdatesAsync()
                              ?? await VelopackUpdateService.CheckForUpdatesAnyAsync();
                if (vUpdate is not null)
                {
                    var semVer = vUpdate.TargetFullRelease.Version;
                    var target = VelopackUpdateService.ToSystemVersion(semVer);
                    var synthetic = new ReleaseInfo($"v{target}", target, $"WDM {target}", $"https://github.com/usm007/WDM/releases/tag/v{target}", $"Delta update to {target} (patch-only).", DateTime.UtcNow, null);
                    UpdateStatusText.Text = "";
                    ShowInlineUpdate(synthetic, vUpdate);
                    return;
                }
                // No delta found — don't offer Setup.exe (would show the modal in the screenshot); delta will appear shortly
                var check = await UpdateChecker.CheckLatestAsync();
                if (check?.Version is { } cv && cv.CompareTo(UpdateChecker.CurrentVersion) > 0)
                {
                    UpdateStatusText.Text = $"WDM {cv} is available — delta is being prepared, please try again shortly.";
                    return;
                }
                UpdateStatusText.Text = "You are running the latest version.";
                return;
            }

            var latest = await UpdateChecker.CheckLatestAsync();
            if (latest is null)
            {
                UpdateStatusText.Text = "Could not reach GitHub — try again later.";
            }
            else if (latest.Version is { } version && version.CompareTo(UpdateChecker.CurrentVersion) > 0)
            {
                UpdateStatusText.Text = "";
                ShowInlineUpdate(latest, null);
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

    private static string ShortNotes(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return "";
        var lines = body.Split('\n').Select(l => l.Trim().TrimStart('-', '*', '•', ' ').Trim()).Where(l => !string.IsNullOrWhiteSpace(l)).Take(2).ToArray();
        var s = string.Join(" • ", lines);
        if (string.IsNullOrWhiteSpace(s)) s = body.Trim().Replace("\r", " ").Replace("\n", " ").Trim();
        if (s.Length > 140) s = s.Substring(0, 137) + "...";
        return s;
    }

    private void ShowInlineUpdate(ReleaseInfo release, Velopack.UpdateInfo? velopackUpdate)
    {
        _inlineRelease = release;
        _inlineVelopack = velopackUpdate;
        _isDownloading = false;

        InlineVersionLine.Text = $"WDM {release.Version} is available";
        InlineProgressPanel.Visibility = Visibility.Collapsed;
        InlineStatusText.Visibility = Visibility.Visible;
        InlineDownloadProgressBar.Value = 0;
        InlineProgressPctText.Text = "0%";
        var notes = ShortNotes(release.Body);
        var warn = "⚠️ Reload the extension after update: chrome://extensions → Reload";
        var notesSuffix = string.IsNullOrWhiteSpace(notes) ? "" : $" — {notes}";

        if (velopackUpdate is not null)
        {
            InlineStatusText.Text = $"Delta (~2-5 MB){notesSuffix} — {warn}";
            InlineProgressDetailText.Text = $"Delta update for {release.Version} — not the full installer.";
            InlineProgressStatusText.Text = "Downloading update package…";
            InlineInstallButton.Content = "Download & Install";
        }
        else if (string.IsNullOrWhiteSpace(release.InstallerUrl) && !string.IsNullOrWhiteSpace(release.UpdatePackageUrl))
        {
            InlineStatusText.Text = $"Update package ready{notesSuffix} — {warn}";
            InlineInstallButton.Content = "Open Release Page";
        }
        else
        {
            InlineStatusText.Text = string.IsNullOrWhiteSpace(notes) ? warn : $"{notes} — {warn}";
            InlineProgressStatusText.Text = "Downloading full installer...";
            InlineProgressDetailText.Text = "Downloading full installer package for this release.";
            InlineInstallButton.Content = "Download & Install";
        }

        InlineUpdatePanel.Visibility = Visibility.Visible;
        DefaultStrip.Visibility = Visibility.Collapsed;
        UpdateActionStrip.Visibility = Visibility.Visible;
        InlineLaterButton.IsEnabled = true;
        InlineInstallButton.IsEnabled = true;
    }

    private void HideInlineUpdate()
    {
        InlineUpdatePanel.Visibility = Visibility.Collapsed;
        UpdateActionStrip.Visibility = Visibility.Collapsed;
        DefaultStrip.Visibility = Visibility.Visible;
        InlineProgressPanel.Visibility = Visibility.Collapsed;
        InlineStatusText.Visibility = Visibility.Visible;
        _inlineRelease = null;
        _inlineVelopack = null;
        _isDownloading = false;
    }

    private void InlineDismissClick(object sender, RoutedEventArgs e)
    {
        if (_isDownloading) return; // block dismiss while downloading
        HideInlineUpdate();
    }

    private async void InlineInstallClick(object sender, RoutedEventArgs e)
    {
        if (_inlineRelease is null) return;

        // Delta-only but no installer -> open release page
        if (_inlineVelopack is null && string.IsNullOrWhiteSpace(_inlineRelease.InstallerUrl) && !string.IsNullOrWhiteSpace(_inlineRelease.UpdatePackageUrl))
        {
            UpdateChecker.OpenReleasesPage(_inlineRelease.Url);
            InlineStatusText.Text = "Opened release page — portable build available there.";
            return;
        }

        // If button was turned into "Open Release Page" after a failure, open it
        if (InlineInstallButton.Content is string c && c == "Open Release Page")
        {
            UpdateChecker.OpenReleasesPage(_inlineRelease.Url);
            return;
        }

        InlineLaterButton.IsEnabled = false;
        InlineInstallButton.IsEnabled = false;
        _isDownloading = true;

        // Velopack delta path
        if (_inlineVelopack is not null)
        {
            InlineStatusText.Visibility = Visibility.Collapsed;
            InlineProgressPanel.Visibility = Visibility.Visible;
            InlineProgressStatusText.Text = "Downloading update package…";
            InlineProgressDetailText.Text = $"Update package for {_inlineRelease.Version} (delta, ~15 KB - 5 MB) — not the full installer.";
            InlineDownloadProgressBar.Value = 0;
            InlineProgressPctText.Text = "0%";
            try
            {
                await VelopackUpdateService.DownloadUpdatesAsync(_inlineVelopack, pct =>
                    Dispatcher.Invoke(() =>
                    {
                        InlineDownloadProgressBar.Value = pct;
                        InlineProgressPctText.Text = $"{pct}%";
                        InlineProgressStatusText.Text = pct < 100 ? "Downloading update package…" : "Download complete — applying…";
                    }));
                InlineProgressStatusText.Text = "Applying update…";
                InlineProgressDetailText.Text = "WDM will restart automatically to apply the patch.";
                InlineDownloadProgressBar.Value = 100;
                InlineProgressPctText.Text = "100%";
                await Task.Delay(600);
                VelopackUpdateService.ApplyAndRestart(_inlineVelopack.TargetFullRelease);
                Close();
            }
            catch (Exception ex)
            {
                _isDownloading = false;
                InlineProgressPanel.Visibility = Visibility.Collapsed;
                InlineStatusText.Visibility = Visibility.Visible;
                InlineStatusText.Text = $"Delta download failed: {ex.Message}";
                InlineLaterButton.IsEnabled = true;
                InlineInstallButton.IsEnabled = true;
            }
            return;
        }

        // Full installer path
        InlineStatusText.Visibility = Visibility.Collapsed;
        InlineProgressPanel.Visibility = Visibility.Visible;
        InlineProgressStatusText.Text = "Downloading full installer...";
        InlineProgressDetailText.Text = "Downloading full installer package for this release.";
        InlineDownloadProgressBar.Value = 0;
        InlineProgressPctText.Text = "0%";
        try
        {
            string installer = await UpdateChecker.DownloadInstallerAsync(_inlineRelease, progress =>
                Dispatcher.Invoke(() =>
                {
                    int pct = (int)Math.Round(progress * 100);
                    InlineDownloadProgressBar.Value = pct;
                    InlineProgressPctText.Text = $"{pct}%";
                    InlineProgressStatusText.Text = "Downloading full installer...";
                }));
            InlineProgressStatusText.Text = "Launching installer…";
            InlineDownloadProgressBar.Value = 100;
            InlineProgressPctText.Text = "100%";
            UpdateChecker.LaunchInstaller(installer, silent: true);
            await Task.Delay(500);
            Close();
        }
        catch (Exception ex)
        {
            _isDownloading = false;
            InlineProgressPanel.Visibility = Visibility.Collapsed;
            InlineStatusText.Visibility = Visibility.Visible;
            InlineStatusText.Text = $"Download failed: {ex.Message} — try again or open the release page.";
            InlineLaterButton.IsEnabled = true;
            InlineInstallButton.IsEnabled = true;
            InlineInstallButton.Content = "Open Release Page";
        }
    }

    private void CloseClick(object sender, RoutedEventArgs e) => Close();
}
