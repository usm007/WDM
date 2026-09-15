using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using WDM.Models;
using WDM.Services;
using WDM.ViewModels;

namespace WDM;

public partial class OptionsControl : UserControl
{
    private MainViewModel? _viewModel;
    private ReleaseInfo? _latestRelease;
    private Velopack.UpdateInfo? _velopackUpdate;
    private bool _isInitializingAppearance = true;

    public event EventHandler? CloseRequested;
    public event EventHandler? OpenExtensionHelperRequested;

    public OptionsControl()
    {
        InitializeComponent();
    }

    public void Initialize(MainViewModel viewModel)
    {
        _viewModel = viewModel;
        var s = viewModel.Settings;

        _isInitializingAppearance = true;
        FolderBox.Text = s.DownloadFolder;
        ChunksBox.SelectedIndex = ChunkIndex(s.DefaultChunkCount);
        MaxConcurrentBox.SelectedIndex = Math.Clamp(s.MaxConcurrentDownloads - 1, 0, Math.Max(0, MaxConcurrentBox.Items.Count - 1));
        RetriesBox.SelectedIndex = Math.Clamp(RetryIndex(s.MaxRetries), 0, Math.Max(0, RetriesBox.Items.Count - 1));
        SpeedBox.Text = s.GlobalSpeedLimitKbps.ToString();

        RouteBox.IsChecked = s.RouteByCategory;
        VideoFolderBox.Text = s.CategoryFolders.GetValueOrDefault(DownloadCategory.Video.ToString()) ?? "";
        MusicFolderBox.Text = s.CategoryFolders.GetValueOrDefault(DownloadCategory.Music.ToString()) ?? "";
        DocumentFolderBox.Text = s.CategoryFolders.GetValueOrDefault(DownloadCategory.Document.ToString()) ?? "";
        CompressedFolderBox.Text = s.CategoryFolders.GetValueOrDefault(DownloadCategory.Compressed.ToString()) ?? "";
        ProgramFolderBox.Text = s.CategoryFolders.GetValueOrDefault(DownloadCategory.Program.ToString()) ?? "";

        ChecksumBox.IsChecked = s.ComputeChecksum;
        ScriptBox.Text = s.PostDownloadScript ?? "";
        HlsContainerBox.SelectedIndex = HlsContainerIndex(s.HlsContainer);

        NotifyBox.IsChecked = s.NotifyOnCompletion;
        TrayProgressBox.IsChecked = s.ShowTrayProgress;
        MinimizeToTrayBox.IsChecked = s.MinimizeToTray;
        if (TitleSyncBox != null) TitleSyncBox.IsChecked = s.EnableTitleSync;
        RunAtStartupBox.IsChecked = s.RunAtStartup;

        UpdateYouTubeUI();

        if (NativeSignInBtn != null)
        {
            NativeSignInBtn.Content = s.YouTubeBrowserCookies == "wdm-native" 
                ? "Signed in — Click to re-authenticate..." 
                : "Sign in to YouTube...";
        }

        CheckForUpdatesBox.IsChecked = s.CheckForUpdates;
        if (AutoUpdateBox != null) AutoUpdateBox.IsChecked = s.AutoDownloadUpdates;
        CurrentVersionText.Text = UpdateChecker.CurrentVersion.ToString();
        LatestVersionText.Text = "—";
        UpdateStatusText.Text = "Click “Check now” to look for a new release on GitHub.";

        // Appearance — dark mode
        DarkModeBox.IsChecked = s.UseDarkTheme;
        _isInitializingAppearance = false;

        PopulateBrowsers();
    }

    private void PopulateBrowsers()
    {
        if (BrowserStatusText == null) return;

        var browsers = BrowserIntegration.DetectInstalledBrowsers();
        BrowserStatusText.Text = browsers.Count == 0
            ? "No supported browsers detected on this system."
            : "Detected: " + string.Join(", ", browsers.Select(b => b.Name)) + ".";
    }

    public void SwitchTab(string tag)
    {
        if (PanelConnection != null) PanelConnection.Visibility = tag == "Connection" ? Visibility.Visible : Visibility.Collapsed;
        if (PanelFolders != null) PanelFolders.Visibility = tag == "Folders" ? Visibility.Visible : Visibility.Collapsed;
        if (PanelBrowser != null) PanelBrowser.Visibility = tag == "Browser" ? Visibility.Visible : Visibility.Collapsed;
        if (PanelYouTube != null) PanelYouTube.Visibility = tag == "YouTube" ? Visibility.Visible : Visibility.Collapsed;
        if (PanelBehavior != null) PanelBehavior.Visibility = tag == "Behavior" ? Visibility.Visible : Visibility.Collapsed;
        if (PanelAppearance != null) PanelAppearance.Visibility = tag == "Appearance" ? Visibility.Visible : Visibility.Collapsed;
        if (PanelUpdates != null) PanelUpdates.Visibility = tag == "Updates" ? Visibility.Visible : Visibility.Collapsed;
        if (PanelAdvanced != null) PanelAdvanced.Visibility = tag == "Advanced" ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Tab_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: string tag })
        {
            SwitchTab(tag);
        }
    }

    private void DarkMode_Changed(object sender, RoutedEventArgs e)
    {
        if (_isInitializingAppearance || _viewModel == null) return;
        PreviewAppearance();
        SaveCurrentSettings();
    }

    private void UpdateOption_Changed(object sender, RoutedEventArgs e)
    {
        if (_isInitializingAppearance || _viewModel == null) return;
        SaveCurrentSettings();
    }

    private void HlsContainer_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_isInitializingAppearance || _viewModel == null) return;
        SaveCurrentSettings();
    }

    private void PreviewAppearance()
    {
        bool dark = DarkModeBox.IsChecked == true;
        ThemeService.Apply(AppTheme.Default, dark);
    }

    private async void YtActivateBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel == null) return;
        var s = _viewModel.Settings;
        bool isCurrentlyActive = s.EnableYouTubeDownloads && EngineManager.IsReady;

        if (isCurrentlyActive)
        {
            s.EnableYouTubeDownloads = false;
            TaskStore.SaveSettings(s);
            UpdateYouTubeUI();
            return;
        }

        s.EnableYouTubeDownloads = true;
        TaskStore.SaveSettings(s);
        if (YtActivateBtn != null) YtActivateBtn.IsEnabled = false;
        if (YtProgressCard != null) YtProgressCard.Visibility = Visibility.Visible;
        if (YtProgressBar != null) YtProgressBar.Value = 0;
        if (YtProgressPctText != null) YtProgressPctText.Text = "0%";
        if (YtProgressStatusText != null) YtProgressStatusText.Text = "Initializing plugin setup...";

        try
        {
            var progress = new Progress<EngineProgress>(p =>
            {
                Dispatcher.Invoke(() =>
                {
                    if (YtProgressStatusText != null) YtProgressStatusText.Text = p.StatusText;
                    double pct = Math.Clamp(p.ProgressFraction * 100, 0, 100);
                    if (YtProgressBar != null) YtProgressBar.Value = pct;
                    if (YtProgressPctText != null) YtProgressPctText.Text = $"{pct:F0}%";
                });
            });

            await EngineManager.EnsureAsync(progress);
            string version = await EngineManager.GetVersionAsync();
            if (YtDlpVersionText != null) YtDlpVersionText.Text = $"v{version}";
        }
        catch (Exception ex)
        {
            MessageBox.Show("Failed to download YouTube engine plugins:\n" + ex.Message, "Engine Setup Error", MessageBoxButton.OK, MessageBoxImage.Error);
            s.EnableYouTubeDownloads = false;
            TaskStore.SaveSettings(s);
        }
        finally
        {
            if (YtProgressCard != null) YtProgressCard.Visibility = Visibility.Collapsed;
            if (YtActivateBtn != null) YtActivateBtn.IsEnabled = true;
            UpdateYouTubeUI();
        }
    }

    private async void UpdateYouTubeUI()
    {
        if (_viewModel == null) return;
        var s = _viewModel.Settings;
        bool active = s.EnableYouTubeDownloads && EngineManager.IsReady;

        if (active)
        {
            if (YtStatusBadgeTitle != null) YtStatusBadgeTitle.Text = "YouTube Downloader Active";
            if (YtStatusBadgeSub != null) YtStatusBadgeSub.Text = "Engine ready — yt-dlp & FFmpeg plugins loaded";
            if (YtActivateBtn != null) YtActivateBtn.Content = "Deactivate";
            if (YtPluginsCard != null) YtPluginsCard.Visibility = Visibility.Visible;
            if (YtAuthCard != null) YtAuthCard.Visibility = Visibility.Visible;
            if (YtDlpVersionText != null)
            {
                var versionLabel = YtDlpVersionText;
                try
                {
                    string ver = await EngineManager.GetVersionAsync();
                    versionLabel.Text = $"v{ver}";
                }
                catch (Exception ex)
                {
                    versionLabel.Text = "version unknown";
                    if (YtStatusBadgeSub != null)
                        YtStatusBadgeSub.Text = $"Engine ready, version check failed: {ex.Message}";
                }
            }
        }
        else
        {
            if (YtStatusBadgeTitle != null) YtStatusBadgeTitle.Text = "YouTube Downloader Inactive";
            if (YtStatusBadgeSub != null) YtStatusBadgeSub.Text = "Click Activate to download required plugins & enable YouTube links";
            if (YtActivateBtn != null) YtActivateBtn.Content = "Activate";
            if (YtPluginsCard != null) YtPluginsCard.Visibility = Visibility.Collapsed;
            if (YtAuthCard != null) YtAuthCard.Visibility = Visibility.Collapsed;
        }
    }

    private void NativeSignInBtn_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var window = new YouTubeSignInWindow
            {
                Owner = Window.GetWindow(this) ?? Application.Current.MainWindow
            };
            if (window.ShowDialog() == true)
            {
                if (NativeSignInBtn != null)
                {
                    NativeSignInBtn.Content = "Signed in — Click to re-authenticate...";
                }
                MessageBox.Show("Successfully signed in to YouTube natively and exported your session. Private and age-restricted videos should now download normally.", "Sign-In Complete", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show("Could not open YouTube Sign-In window: " + ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    public void SaveCurrentSettings()
    {
        if (_isInitializingAppearance || _viewModel == null) return;

        var s = _viewModel.Settings;
        s.DownloadFolder = string.IsNullOrWhiteSpace(FolderBox?.Text) ? DownloadTask.DefaultSaveFolder : FolderBox.Text.Trim();

        s.DefaultChunkCount = ChunksBox?.SelectedItem is ComboBoxItem chunks && chunks.Tag is string tag && int.TryParse(tag, out int c) ? c : 0;
        s.MaxConcurrentDownloads = MaxConcurrentBox?.SelectedItem is ComboBoxItem mc && int.TryParse(mc.Content?.ToString(), out int m) ? m : 3;
        s.MaxRetries = RetriesBox?.SelectedItem is ComboBoxItem r && int.TryParse(ExtractFirstDigit(r.Content?.ToString()!), out int retries) ? retries : 3;
        s.GlobalSpeedLimitKbps = long.TryParse(SpeedBox?.Text?.Trim(), out long speed) && speed >= 0 ? speed : 0;

        s.RouteByCategory = RouteBox?.IsChecked == true;
        if (VideoFolderBox != null)
        {
            s.CategoryFolders = new Dictionary<string, string>
            {
                [DownloadCategory.Video.ToString()] = VideoFolderBox.Text.Trim(),
                [DownloadCategory.Music.ToString()] = MusicFolderBox.Text.Trim(),
                [DownloadCategory.Document.ToString()] = DocumentFolderBox.Text.Trim(),
                [DownloadCategory.Compressed.ToString()] = CompressedFolderBox.Text.Trim(),
                [DownloadCategory.Program.ToString()] = ProgramFolderBox.Text.Trim(),
            };
        }

        if (ChecksumBox != null) s.ComputeChecksum = ChecksumBox.IsChecked == true;
        if (ScriptBox != null) s.PostDownloadScript = string.IsNullOrWhiteSpace(ScriptBox.Text) ? null : ScriptBox.Text.Trim();
        if (HlsContainerBox?.SelectedItem is ComboBoxItem hls && hls.Tag is string hlsTag
            && Enum.TryParse<HlsContainer>(hlsTag, out var container))
            s.HlsContainer = container;

        if (NotifyBox != null) s.NotifyOnCompletion = NotifyBox.IsChecked == true;
        if (TrayProgressBox != null) s.ShowTrayProgress = TrayProgressBox.IsChecked == true;
        if (MinimizeToTrayBox != null) s.MinimizeToTray = MinimizeToTrayBox.IsChecked == true;
        if (TitleSyncBox != null) s.EnableTitleSync = TitleSyncBox.IsChecked == true;
        if (RunAtStartupBox != null) s.RunAtStartup = RunAtStartupBox.IsChecked == true;
        if (CheckForUpdatesBox != null) s.CheckForUpdates = CheckForUpdatesBox.IsChecked == true;
        if (AutoUpdateBox != null) s.AutoDownloadUpdates = AutoUpdateBox.IsChecked == true;

        if (DarkModeBox != null)
        {
            s.Theme = AppTheme.Default;
            s.UseDarkTheme = DarkModeBox.IsChecked == true;
            _viewModel.SelectedTheme = AppTheme.Default;
            _viewModel.IsDarkTheme = s.UseDarkTheme;
        }

        _viewModel.PersistSettings();
    }

    private void CloseClick(object sender, RoutedEventArgs e)
    {
        SaveCurrentSettings();
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    private async void CheckNowClick(object sender, RoutedEventArgs e)
    {
        CheckNowButton.IsEnabled = false;
        OpenReleaseButton.Visibility = Visibility.Collapsed;
        DownloadInstallButton.Visibility = Visibility.Collapsed;
        UpdateStatusText.Text = "Checking for updates...";
        _latestRelease = null;
        _velopackUpdate = null;
        try
        {
            // Prefer Velopack packages when installed via Velopack (with fallback any-check).
            // 1 behind → delta only; 2+ behind → self-contained full nupkg. Setup.exe is new-users only.
            if (VelopackUpdateService.IsVelopackInstalled)
            {
                var vUpdate = await VelopackUpdateService.CheckForUpdatesAsync();
                if (vUpdate == null) vUpdate = await VelopackUpdateService.CheckForUpdatesAnyAsync();
                if (vUpdate is not null)
                {
                    _velopackUpdate = vUpdate;
                    var semVer = vUpdate.TargetFullRelease.Version;
                    var target = VelopackUpdateService.ToSystemVersion(semVer);
                    bool isDelta = VelopackUpdateService.IsDeltaUpdate(vUpdate);
                    string desc = VelopackUpdateService.DescribeUpdate(vUpdate);
                    LatestVersionText.Text = $"v{target} ({(isDelta ? "delta" : "full")})";
                    OpenReleaseButton.Visibility = Visibility.Visible;
                    DownloadInstallButton.Visibility = Visibility.Visible;
                    DownloadInstallButton.Content = isDelta ? "Download Delta & Restart" : "Download Full & Restart";
                    UpdateStatusText.Text = isDelta
                        ? $"{desc} update available: v{target} — patch-only, auto-restart."
                        : $"{desc} update available: v{target} — 2+ versions behind, full package (.NET included), auto-restart.";
                    return;
                }
            }

            _latestRelease = await UpdateChecker.CheckLatestAsync();
            if (_latestRelease is null)
            {
                UpdateStatusText.Text = "No release published yet, or GitHub is unreachable. Try again later.";
            }
            else if (_latestRelease.Version is { } version && version.CompareTo(UpdateChecker.CurrentVersion) > 0)
            {
                LatestVersionText.Text = _latestRelease.TagName;
                DownloadInstallButton.Visibility = Visibility.Visible;
                // Handle delta-only releases (no .exe yet) — try Velopack as fallback.
                // Setup.exe / portable zip are for new users only; existing Velopack installs use nupkg.
                if (string.IsNullOrWhiteSpace(_latestRelease.InstallerUrl) && !string.IsNullOrWhiteSpace(_latestRelease.UpdatePackageUrl))
                {
                    var anyUpdate = await VelopackUpdateService.CheckForUpdatesAnyAsync();
                    if (anyUpdate != null && VelopackUpdateService.IsVelopackInstalled)
                    {
                        _velopackUpdate = anyUpdate;
                        bool isDelta = VelopackUpdateService.IsDeltaUpdate(anyUpdate);
                        string desc = VelopackUpdateService.DescribeUpdate(anyUpdate);
                        // Single primary action — hide the secondary button to avoid duplicate "Open Release Page".
                        OpenReleaseButton.Visibility = Visibility.Collapsed;
                        DownloadInstallButton.Content = isDelta ? "Download Delta & Restart" : "Download Full & Restart";
                        UpdateStatusText.Text = isDelta
                            ? $"{desc} update available: {version} — patch-only, auto-restart (no installer needed)."
                            : $"{desc} update available: {version} — full package (.NET included), auto-restart (no installer needed).";
                    }
                    else
                    {
                        // Non-Velopack (portable/dev) can't apply nupkg deltas — single button to the release page.
                        // Dedupe: hide secondary button since the primary already opens the page.
                        OpenReleaseButton.Visibility = Visibility.Collapsed;
                        DownloadInstallButton.Content = "Open Release Page";
                        UpdateStatusText.Text = VelopackUpdateService.IsVelopackInstalled
                            ? $"A new version is available: {_latestRelease.TagName} — update feed unreachable, open release page (Setup.exe / portable are for new users)."
                            : $"A new version is available: {_latestRelease.TagName} — portable install can't apply delta packages. Open release page for the new portable zip (Setup.exe is for new users).";
                    }
                }
                else
                {
                    OpenReleaseButton.Visibility = Visibility.Visible;
                    DownloadInstallButton.Content = "Download & Install";
                    UpdateStatusText.Text = $"A new version is available: {_latestRelease.TagName}." +
                        (_latestRelease.PublishedAt is { } published ? $" Published {published.ToLocalTime():yyyy-MM-dd}." : "");
                }
            }
            else
            {
                LatestVersionText.Text = _latestRelease.TagName;
                UpdateStatusText.Text = "You are running the latest version.";
            }
        }
        catch (Exception ex)
        {
            _latestRelease = null;
            _velopackUpdate = null;
            LatestVersionText.Text = "—";
            UpdateStatusText.Text = $"Check failed: {ex.Message}";
        }
        finally
        {
            CheckNowButton.IsEnabled = true;
        }
    }

    private void OpenReleaseClick(object sender, RoutedEventArgs e)
    {
        UpdateChecker.OpenReleasesPage(_latestRelease?.Url);
    }

    private async void DownloadInstallClick(object sender, RoutedEventArgs e)
    {
        // Velopack path: nupkg ONLY (delta if 1 behind, self-contained full if 2+ behind).
        // Setup.exe / portable zip are for new users only — never downloaded here.
        if (_velopackUpdate is not null)
        {
            bool isDelta = VelopackUpdateService.IsDeltaUpdate(_velopackUpdate);
            string desc = VelopackUpdateService.DescribeUpdate(_velopackUpdate);
            DownloadInstallButton.IsEnabled = false;
            OpenReleaseButton.IsEnabled = false;
            CheckNowButton.IsEnabled = false;
            UpdateProgressPanel.Visibility = Visibility.Visible;
            UpdateProgressBar.Value = 0;
            UpdateProgressPctText.Text = "0%";
            UpdateProgressStatusText.Text = isDelta ? "Downloading delta package…" : "Downloading full package…";
            UpdateProgressDetailText.Text = isDelta
                ? $"Only the {desc} is being downloaded — no installer needed."
                : $"Downloading the {desc} — 2+ versions behind, full package required (no installer needed).";
            UpdateStatusText.Text = isDelta ? "Downloading delta package…" : "Downloading full package…";
            try
            {
                await VelopackUpdateService.DownloadUpdatesAsync(_velopackUpdate, pct =>
                {
                    Dispatcher.Invoke(() =>
                    {
                        UpdateProgressBar.Value = pct;
                        UpdateProgressPctText.Text = $"{pct}%";
                        UpdateProgressStatusText.Text = pct < 100
                            ? (isDelta ? "Downloading delta package…" : "Downloading full package…")
                            : "Download complete — applying…";
                        UpdateStatusText.Text = $"{(isDelta ? "Downloading delta package…" : "Downloading full package…")} {pct}%";
                    });
                });
                UpdateProgressStatusText.Text = "Applying update…";
                UpdateProgressDetailText.Text = "WDM will restart automatically to apply the update.";
                UpdateProgressBar.Value = 100;
                UpdateProgressPctText.Text = "100%";
                UpdateStatusText.Text = "Update downloaded — applying and restarting…";
                await Task.Delay(600);
                VelopackUpdateService.ApplyAndRestart(_velopackUpdate.TargetFullRelease);
            }
            catch (Exception ex)
            {
                UpdateProgressPanel.Visibility = Visibility.Collapsed;
                UpdateStatusText.Text = $"{(isDelta ? "Delta" : "Full package")} download failed: {ex.Message}";
                DownloadInstallButton.IsEnabled = true;
                OpenReleaseButton.IsEnabled = true;
                CheckNowButton.IsEnabled = true;
            }
            return;
        }

        if (_latestRelease is null)
            return;

        // No trusted installer asset (delta-only release, or portable install that can't
        // apply nupkg) — the primary button acts as "Open Release Page" in this state.
        if (string.IsNullOrWhiteSpace(_latestRelease.InstallerUrl))
        {
            UpdateChecker.OpenReleasesPage(_latestRelease.Url);
            UpdateStatusText.Text = "Opened release page — portable build available there (Setup.exe is for new users).";
            return;
        }

        DownloadInstallButton.IsEnabled = false;
        OpenReleaseButton.IsEnabled = false;
        CheckNowButton.IsEnabled = false;
        UpdateProgressPanel.Visibility = Visibility.Visible;
        UpdateProgressBar.Value = 0;
        UpdateProgressPctText.Text = "0%";
        UpdateProgressStatusText.Text = "Downloading full installer…";
        UpdateProgressDetailText.Text = "Downloading full installer package for this release.";
        UpdateStatusText.Text = "Downloading the full installer…";
        try
        {
            string installer = await UpdateChecker.DownloadInstallerAsync(_latestRelease, progress =>
                Dispatcher.Invoke(() =>
                {
                    int pct = (int)Math.Round(progress * 100);
                    UpdateProgressBar.Value = pct;
                    UpdateProgressPctText.Text = $"{pct}%";
                    UpdateProgressStatusText.Text = "Downloading full installer…";
                    UpdateStatusText.Text = $"Downloading full installer… {pct}%";
                }));
            UpdateProgressStatusText.Text = "Launching installer…";
            UpdateProgressBar.Value = 100;
            UpdateProgressPctText.Text = "100%";
            UpdateChecker.LaunchInstaller(installer, silent: true);
            UpdateStatusText.Text = "Installer downloaded — WDM will close and restart to complete the update.";
            await Task.Delay(500);
            Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            UpdateProgressPanel.Visibility = Visibility.Collapsed;
            UpdateStatusText.Text = $"Download failed: {ex.Message}";
            DownloadInstallButton.IsEnabled = true;
            OpenReleaseButton.IsEnabled = true;
            CheckNowButton.IsEnabled = true;
        }
    }

    private static int ChunkIndex(int chunks) => chunks switch
    {
        0 => 0,
        1 => 1,
        2 => 2,
        4 => 3,
        8 => 4,
        16 => 5,
        _ => 0,
    };

    private static int RetryIndex(int retries) => retries switch
    {
        0 => 0,
        1 => 1,
        2 => 2,
        5 => 4,
        10 => 5,
        _ => 3,
    };

    private static int HlsContainerIndex(HlsContainer container) => container switch
    {
        HlsContainer.Mkv => 1,
        HlsContainer.KeepTs => 2,
        _ => 0,
    };

    private void BrowseClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { InitialDirectory = FolderBox.Text };
        if (dialog.ShowDialog() == true)
        {
            FolderBox.Text = dialog.FolderName;
            SaveCurrentSettings();
        }
    }

    private void ScriptBrowseClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "Programs and scripts (*.exe;*.bat;*.cmd;*.ps1;*.py)|*.exe;*.bat;*.cmd;*.ps1;*.py|All files (*.*)|*.*",
        };
        if (dialog.ShowDialog() == true)
        {
            ScriptBox.Text = dialog.FileName;
            SaveCurrentSettings();
        }
    }

    private void OpenExtensionHelper_Click(object sender, RoutedEventArgs e)
    {
        OpenExtensionHelperRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OpenMoreHelp_Click(object sender, RoutedEventArgs e)
    {
        BrowserIntegration.OpenExtensionGuide();
    }

    private static string ExtractFirstDigit(string input)
    {
        var match = System.Text.RegularExpressions.Regex.Match(input, @"\d+");
        return match.Success ? match.Value : "0";
    }
}
