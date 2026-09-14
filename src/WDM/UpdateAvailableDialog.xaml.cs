using System;
using System.Linq;
using System.Windows;
using Velopack;
using WDM.Services;

namespace WDM;

public partial class UpdateAvailableDialog : Window
{
    private readonly ReleaseInfo _release;
    private readonly UpdateInfo? _velopackUpdate;

    private static bool _isOpen = false;
    public static bool IsDialogOpen => _isOpen;

    private static string ShortNotes(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return "";
        var lines = body.Split('\n')
            .Select(l => l.Trim().TrimStart('-', '*', '•', ' ').Trim())
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .Take(2)
            .ToArray();
        var s = string.Join(" • ", lines);
        if (string.IsNullOrWhiteSpace(s))
            s = body.Trim().Replace("\r", " ").Replace("\n", " ").Trim();
        if (s.Length > 160) s = s.Substring(0, 157) + "...";
        return s;
    }

    public UpdateAvailableDialog(ReleaseInfo release, UpdateInfo? velopackUpdate = null)
    {
        InitializeComponent();
        _release = release;
        _velopackUpdate = velopackUpdate;
        VersionLine.Text = $"WDM {release.Version} is available";
        var notes = ShortNotes(release.Body);
        var notesBlock = string.IsNullOrWhiteSpace(notes) ? "" : $"What's new: {notes}{Environment.NewLine}{Environment.NewLine}";
        var reloadWarn = "⚠️ After update, reload the browser extension: chrome://extensions (or edge://extensions) → Reload on WDM.";
        if (velopackUpdate is not null)
        {
            bool isDelta = VelopackUpdateService.IsDeltaUpdate(velopackUpdate);
            string desc = VelopackUpdateService.DescribeUpdate(velopackUpdate);
            DetailsText.Text = $"Current: {VelopackUpdateService.CurrentVersion}  →  New: {release.Version}{Environment.NewLine}" +
                               (isDelta
                                   ? $"{desc} update (patch-only, 1 version behind) — no installer needed.{Environment.NewLine}"
                                   : $"{desc} update (2+ versions behind, .NET included) — no installer needed.{Environment.NewLine}") +
                               notesBlock + reloadWarn;
            InstallButton.Content = isDelta ? "Download Delta & Restart" : "Download Full & Restart";
        }
        else if (string.IsNullOrWhiteSpace(release.InstallerUrl) && !string.IsNullOrWhiteSpace(release.UpdatePackageUrl))
        {
            DetailsText.Text = $"Current: {UpdateChecker.CurrentVersion}  →  New: {release.Version}{Environment.NewLine}" +
                               $"Update package available (Setup.exe is for new users only).{Environment.NewLine}" +
                               notesBlock + reloadWarn;
            InstallButton.Content = "Open Release Page";
        }
        else
        {
            DetailsText.Text = string.IsNullOrWhiteSpace(release.Body)
                ? $"Current: {UpdateChecker.CurrentVersion}  →  New: {release.Version}{Environment.NewLine}{Environment.NewLine}" + reloadWarn
                : $"{notesBlock}{reloadWarn}{Environment.NewLine}{Environment.NewLine}Current: {UpdateChecker.CurrentVersion}  →  New: {release.Version}";
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _isOpen = false;
        if (Owner is not null)
        {
            try { Owner.Activated -= Owner_Activated; } catch { }
        }
        base.OnClosed(e);
    }

    private void Owner_Activated(object? s, EventArgs e)
    {
        if (IsVisible)
        {
            try { Activate(); } catch { }
        }
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        WDM.Services.ThemeService.ApplyTitleBar(this);
        _isOpen = true;
        Topmost = true;
        // Ensure dialog is on top of its owner and any other dialogs
        if (Owner != null)
        {
            Owner.Activated += Owner_Activated;
        }
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

        // Velopack path: nupkg ONLY (delta if 1 behind, self-contained full if 2+ behind).
        if (_velopackUpdate is not null)
        {
            bool isDelta = VelopackUpdateService.IsDeltaUpdate(_velopackUpdate);
            string desc = VelopackUpdateService.DescribeUpdate(_velopackUpdate);
            ProgressPanel.Visibility = Visibility.Visible;
            ProgressStatusText.Text = isDelta ? "Downloading delta package…" : "Downloading full package…";
            ProgressDetailText.Text = $"Update package for { _release.Version } ({desc}) — no installer needed.";
            DownloadProgressBar.Value = 0;
            ProgressPctText.Text = "0%";
            DetailsText.Text = $"Preparing {(isDelta ? "delta" : "full")} update to { _release.Version }…";
            try
            {
                await VelopackUpdateService.DownloadUpdatesAsync(_velopackUpdate, pct =>
                    Dispatcher.InvokeAsync(() =>
                    {
                        DownloadProgressBar.Value = pct;
                        ProgressPctText.Text = $"{pct}%";
                        ProgressStatusText.Text = pct < 100
                            ? (isDelta ? "Downloading delta package…" : "Downloading full package…")
                            : "Download complete — applying…";
                    }));
                ProgressStatusText.Text = "Applying update…";
                ProgressDetailText.Text = "WDM will restart automatically to apply the update.";
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
                DetailsText.Text = $"{(isDelta ? "Delta" : "Full package")} download failed: {ex.Message}";
                InstallButton.IsEnabled = true;
                LaterButton.IsEnabled = true;
            }
            return;
        }

        // Fallback: full installer or delta-only handling
        // If only update package is available and no .exe, open release page instead of failing
        if (string.IsNullOrWhiteSpace(_release.InstallerUrl) && !string.IsNullOrWhiteSpace(_release.UpdatePackageUrl))
        {
            UpdateChecker.OpenReleasesPage(_release.Url);
            DetailsText.Text = "Opened release page — portable build available there (Setup.exe is for new users).";
            ProgressPanel.Visibility = Visibility.Collapsed;
            InstallButton.IsEnabled = true;
            LaterButton.IsEnabled = true;
            InstallButton.Content = "Open Release Page";
            return;
        }

        ProgressPanel.Visibility = Visibility.Visible;
        ProgressStatusText.Text = "Downloading full installer…";
        ProgressDetailText.Text = "Downloading full installer package for this release.";
        DownloadProgressBar.Value = 0;
        ProgressPctText.Text = "0%";
        DetailsText.Text = "Downloading the full installer…";
        try
        {
            string installer = await UpdateChecker.DownloadInstallerAsync(_release, progress =>
                Dispatcher.InvokeAsync(() =>
                {
                    int pct = (int)Math.Round(progress * 100);
                    DownloadProgressBar.Value = pct;
                    ProgressPctText.Text = $"{pct}%";
                    ProgressStatusText.Text = "Downloading full installer…";
                }));
            ProgressStatusText.Text = "Launching installer…";
            DownloadProgressBar.Value = 100;
            ProgressPctText.Text = "100%";
            UpdateChecker.LaunchInstaller(installer, silent: true);
            await Task.Delay(500);
            DialogResult = true;
            Close();
        }
        catch (Exception ex)
        {
            ProgressPanel.Visibility = Visibility.Collapsed;
            // More friendly error + offer to open release page
            DetailsText.Text = $"Download failed: {ex.Message}{Environment.NewLine}Please try again or open the release page to download manually.";
            InstallButton.IsEnabled = true;
            LaterButton.IsEnabled = true;
            InstallButton.Content = "Open Release Page";
            // Change button action to open page on next click
            InstallButton.Click -= InstallClick;
            InstallButton.Click += (s, _) => UpdateChecker.OpenReleasesPage(_release.Url);
        }
    }

    private void LaterClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}