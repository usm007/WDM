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
            // Velopack install → nupkg only (truly silent); never fall back to Setup.exe which shows the "already installed" prompt
            if (VelopackUpdateService.IsVelopackInstalled)
            {
                var vUpdate = await VelopackUpdateService.CheckForUpdatesAsync()
                              ?? await VelopackUpdateService.CheckForUpdatesAnyAsync();
                if (vUpdate is not null)
                {
                    var semVer = vUpdate.TargetFullRelease.Version;
                    var target = VelopackUpdateService.ToSystemVersion(semVer);
                    bool isDelta = VelopackUpdateService.IsDeltaUpdate(vUpdate);
                    string desc = VelopackUpdateService.DescribeUpdate(vUpdate);
                    var synthetic = new ReleaseInfo($"v{target}", target, $"WDM {target}", $"https://github.com/usm007/WDM/releases/tag/v{target}", $"{desc} update to {target} ({(isDelta ? "1 behind, patch-only" : "2+ behind, .NET included")}).", DateTime.UtcNow, null);
                    UpdateStatusText.Text = "";
                    ShowInlineUpdate(synthetic, vUpdate);
                    return;
                }
                // No delta found via fast path — fall back to GitHub release.
                // Same as Settings > Updates: delta-only releases (no .exe yet) resolve
                // the Velopack feed so the button downloads + installs instead of
                // opening the release page.
                var check = await UpdateChecker.CheckLatestAsync();
                if (check?.Version is { } cv && cv.CompareTo(UpdateChecker.CurrentVersion) > 0)
                {
                    Velopack.UpdateInfo? any = null;
                    if (string.IsNullOrWhiteSpace(check.InstallerUrl) && !string.IsNullOrWhiteSpace(check.UpdatePackageUrl))
                    {
                        try { any = await VelopackUpdateService.CheckForUpdatesAnyAsync(); } catch { }
                    }
                    UpdateStatusText.Text = "";
                    ShowInlineUpdate(check, any);
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

    /// <summary>Called by automatic update check to surface the in-app dialog instead of a Windows balloon.</summary>
    public void ShowAvailableUpdate(ReleaseInfo release, Velopack.UpdateInfo? velopackUpdate)
    {
        ShowInlineUpdate(release, velopackUpdate);
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
            bool isDelta = VelopackUpdateService.IsDeltaUpdate(velopackUpdate);
            string desc = VelopackUpdateService.DescribeUpdate(velopackUpdate);
            InlineStatusText.Text = $"{desc}{notesSuffix} — {warn}";
            InlineProgressDetailText.Text = isDelta
                ? $"Delta update for {release.Version} — no installer needed."
                : $"Full package for {release.Version} (.NET included) — no installer needed.";
            InlineProgressStatusText.Text = isDelta ? "Downloading delta package…" : "Downloading full package…";
            InlineInstallButton.Content = isDelta ? "Download Delta & Restart" : "Download Full & Restart";
        }
        else if (string.IsNullOrWhiteSpace(release.InstallerUrl) && !string.IsNullOrWhiteSpace(release.UpdatePackageUrl))
        {
            // Delta-only release (no .exe yet): same as Settings > Updates —
            // offer Download & Install, resolved to Velopack on click if needed.
            // Never show "Open Release Page" on Velopack installs.
            InlineStatusText.Text = $"Update package ready{notesSuffix} — {warn}";
            InlineProgressDetailText.Text = $"Update package for {release.Version} — no installer needed (Setup.exe is for new users).";
            InlineProgressStatusText.Text = "Downloading update package…";
            InlineInstallButton.Content = "Download & Install";
        }
        else
        {
            InlineStatusText.Text = string.IsNullOrWhiteSpace(notes) ? warn : $"{notes} — {warn}";
            InlineProgressStatusText.Text = "Downloading full installer…";
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

        // Delta-only release with no resolved Velopack info yet (e.g. opened via
        // auto-check badge): resolve the feed now, same as Settings > Updates.
        // Never sends the user to the browser on Velopack installs.
        if (_inlineVelopack is null && string.IsNullOrWhiteSpace(_inlineRelease.InstallerUrl) && !string.IsNullOrWhiteSpace(_inlineRelease.UpdatePackageUrl))
        {
            // Portable / non-Velopack builds can't apply deltas — the portable
            // zip lives on the release page, so the browser is the only option.
            if (!VelopackUpdateService.IsVelopackInstalled)
            {
                UpdateChecker.OpenReleasesPage(_inlineRelease.Url);
                InlineStatusText.Text = "Opened release page — portable build available there.";
                return;
            }
            InlineLaterButton.IsEnabled = false;
            InlineInstallButton.IsEnabled = false;
            InlineStatusText.Text = "Resolving delta update…";
            try
            {
                var resolved = await VelopackUpdateService.CheckForUpdatesAsync()
                    ?? await VelopackUpdateService.CheckForUpdatesAnyAsync();
                if (resolved is not null)
                {
                    _inlineVelopack = resolved;
                }
                else
                {
                    InlineStatusText.Text = "Could not resolve delta update — check connection and try again.";
                    InlineLaterButton.IsEnabled = true;
                    InlineInstallButton.IsEnabled = true;
                    InlineInstallButton.Content = "Download & Install";
                    return;
                }
            }
            catch (Exception ex)
            {
                InlineStatusText.Text = $"Could not resolve delta update: {ex.Message} — try again.";
                InlineLaterButton.IsEnabled = true;
                InlineInstallButton.IsEnabled = true;
                InlineInstallButton.Content = "Download & Install";
                return;
            }
            InlineLaterButton.IsEnabled = true;
            InlineInstallButton.IsEnabled = true;
        }

        InlineLaterButton.IsEnabled = false;
        InlineInstallButton.IsEnabled = false;
        InlineInstallButton.Content = "Download & Install";
        _isDownloading = true;

        // Velopack path: delta if 1 behind, self-contained full nupkg if 2+ behind.
        if (_inlineVelopack is not null)
        {
            bool isDelta = VelopackUpdateService.IsDeltaUpdate(_inlineVelopack);
            string desc = VelopackUpdateService.DescribeUpdate(_inlineVelopack);
            InlineStatusText.Visibility = Visibility.Collapsed;
            InlineProgressPanel.Visibility = Visibility.Visible;
            InlineProgressStatusText.Text = isDelta ? "Downloading delta package…" : "Downloading full package…";
            InlineProgressDetailText.Text = $"Update package for {_inlineRelease.Version} ({desc}) — no installer needed.";
            InlineDownloadProgressBar.Value = 0;
            InlineProgressPctText.Text = "0%";
            try
            {
                await VelopackUpdateService.DownloadUpdatesAsync(_inlineVelopack, pct =>
                    Dispatcher.InvokeAsync(() =>
                    {
                        InlineDownloadProgressBar.Value = pct;
                        InlineProgressPctText.Text = $"{pct}%";
                        InlineProgressStatusText.Text = pct < 100
                            ? (isDelta ? "Downloading delta package…" : "Downloading full package…")
                            : "Download complete — applying…";
                    }));
                InlineProgressStatusText.Text = "Applying update…";
                InlineProgressDetailText.Text = "WDM will restart automatically to apply the update.";
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
                InlineStatusText.Text = $"{(isDelta ? "Delta" : "Full package")} download failed: {ex.Message}";
                InlineLaterButton.IsEnabled = true;
                InlineInstallButton.IsEnabled = true;
            }
            return;
        }

        // Full installer path
        InlineStatusText.Visibility = Visibility.Collapsed;
        InlineProgressPanel.Visibility = Visibility.Visible;
        InlineProgressStatusText.Text = "Downloading full installer…";
        InlineProgressDetailText.Text = "Downloading full installer package for this release.";
        InlineDownloadProgressBar.Value = 0;
        InlineProgressPctText.Text = "0%";
        try
        {
            string installer = await UpdateChecker.DownloadInstallerAsync(_inlineRelease, progress =>
                Dispatcher.InvokeAsync(() =>
                {
                    int pct = (int)Math.Round(progress * 100);
                    InlineDownloadProgressBar.Value = pct;
                    InlineProgressPctText.Text = $"{pct}%";
                    InlineProgressStatusText.Text = "Downloading full installer…";
                }));
            InlineProgressStatusText.Text = "Launching installer…";
            InlineDownloadProgressBar.Value = 100;
            InlineProgressPctText.Text = "100%";
            await UpdateChecker.LaunchInstallerAndWaitForStart(installer);
            Close();
        }
        catch (Exception ex)
        {
            _isDownloading = false;
            InlineProgressPanel.Visibility = Visibility.Collapsed;
            InlineStatusText.Visibility = Visibility.Visible;
            InlineStatusText.Text = $"Download failed: {ex.Message} — try again.";
            InlineLaterButton.IsEnabled = true;
            InlineInstallButton.IsEnabled = true;
            InlineInstallButton.Content = "Download & Install";
        }
    }

    private void CloseClick(object sender, RoutedEventArgs e) => Close();
}
