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
        MaxConcurrentBox.SelectedIndex = Math.Clamp(s.MaxConcurrentDownloads - 1, 0, MaxConcurrentBox.Items.Count - 1);
        RetriesBox.SelectedIndex = Math.Clamp(RetryIndex(s.MaxRetries), 0, RetriesBox.Items.Count - 1);
        SpeedBox.Text = s.GlobalSpeedLimitKbps.ToString();

        RouteBox.IsChecked = s.RouteByCategory;
        VideoFolderBox.Text = s.CategoryFolders.GetValueOrDefault(DownloadCategory.Video.ToString()) ?? "";
        MusicFolderBox.Text = s.CategoryFolders.GetValueOrDefault(DownloadCategory.Music.ToString()) ?? "";
        DocumentFolderBox.Text = s.CategoryFolders.GetValueOrDefault(DownloadCategory.Document.ToString()) ?? "";
        CompressedFolderBox.Text = s.CategoryFolders.GetValueOrDefault(DownloadCategory.Compressed.ToString()) ?? "";
        ProgramFolderBox.Text = s.CategoryFolders.GetValueOrDefault(DownloadCategory.Program.ToString()) ?? "";

        ChecksumBox.IsChecked = s.ComputeChecksum;
        ScriptBox.Text = s.PostDownloadScript ?? "";

        NotifyBox.IsChecked = s.NotifyOnCompletion;
        TrayProgressBox.IsChecked = s.ShowTrayProgress;
        MinimizeToTrayBox.IsChecked = s.MinimizeToTray;
        RunAtStartupBox.IsChecked = s.RunAtStartup;

        UpdateYouTubeUI();

        if (NativeSignInBtn != null)
        {
            NativeSignInBtn.Content = s.YouTubeBrowserCookies == "wdm-native" 
                ? "Signed in — Click to re-authenticate..." 
                : "Sign in to YouTube...";
        }

        CheckForUpdatesBox.IsChecked = s.CheckForUpdates;
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

        if (NotifyBox != null) s.NotifyOnCompletion = NotifyBox.IsChecked == true;
        if (TrayProgressBox != null) s.ShowTrayProgress = TrayProgressBox.IsChecked == true;
        if (MinimizeToTrayBox != null) s.MinimizeToTray = MinimizeToTrayBox.IsChecked == true;
        if (RunAtStartupBox != null) s.RunAtStartup = RunAtStartupBox.IsChecked == true;
        if (CheckForUpdatesBox != null) s.CheckForUpdates = CheckForUpdatesBox.IsChecked == true;

        if (DarkModeBox != null)
        {
            s.Theme = AppTheme.Default;
            s.UseDarkTheme = DarkModeBox.IsChecked == true;
            _viewModel.SelectedTheme = AppTheme.Default;
            _viewModel.IsDarkTheme = s.UseDarkTheme;
        }

        TaskStore.SaveSettings(s);
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
        UpdateStatusText.Text = "Checking for updates...";
        try
        {
            _latestRelease = await UpdateChecker.CheckLatestAsync();
            if (_latestRelease is null)
            {
                UpdateStatusText.Text = "No release published yet, or GitHub is unreachable. Try again later.";
            }
            else if (_latestRelease.Version is { } version && version.CompareTo(UpdateChecker.CurrentVersion) > 0)
            {
                LatestVersionText.Text = _latestRelease.TagName;
                OpenReleaseButton.Visibility = Visibility.Visible;
                DownloadInstallButton.Visibility = Visibility.Visible;
                UpdateStatusText.Text = $"A new version is available: {_latestRelease.TagName}." +
                    (_latestRelease.PublishedAt is { } published ? $" Published {published.ToLocalTime():yyyy-MM-dd}." : "");
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
        if (_latestRelease is null)
            return;

        DownloadInstallButton.IsEnabled = false;
        OpenReleaseButton.IsEnabled = false;
        UpdateStatusText.Text = "Downloading the new installer…";

        try
        {
            string installer = await UpdateChecker.DownloadInstallerAsync(_latestRelease);
            UpdateChecker.LaunchInstaller(installer);
            UpdateStatusText.Text = "Installer downloaded — WDM will close and restart to complete the update.";
            await Task.Delay(500);
            Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            UpdateStatusText.Text = $"Download failed: {ex.Message}";
            DownloadInstallButton.IsEnabled = true;
            OpenReleaseButton.IsEnabled = true;
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
