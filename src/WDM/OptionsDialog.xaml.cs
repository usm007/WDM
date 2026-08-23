using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using WDM.Models;
using WDM.Services;
using WDM.ViewModels;

namespace WDM;

public partial class OptionsDialog : Window
{
    private readonly MainViewModel _viewModel;
    private ReleaseInfo? _latestRelease;
    private readonly AppTheme _originalTheme;
    private readonly bool _originalDark;
    private bool _isInitializingAppearance = true;

    public OptionsDialog(MainViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        var s = viewModel.Settings;
        _originalTheme = s.Theme;
        _originalDark = s.UseDarkTheme;

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

        CheckForUpdatesBox.IsChecked = s.CheckForUpdates;
        CurrentVersionText.Text = UpdateChecker.CurrentVersion.ToString();
        LatestVersionText.Text = "—";
        UpdateStatusText.Text = "Click “Check now” to look for a new release on GitHub.";

        // Appearance — theme family + dark mode (preview, saved on Save)
        ThemeBox.SelectedIndex = s.Theme == AppTheme.WdmOriginal ? 1 : 0;
        DarkModeBox.IsChecked = s.UseDarkTheme;
        _isInitializingAppearance = false;

        PopulateBrowsers();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        WDM.Services.ThemeService.ApplyTitleBar(this);
    }

    private void PopulateBrowsers()
    {
        if (BrowserStatusText == null) return;

        var browsers = BrowserIntegration.DetectInstalledBrowsers();
        BrowserStatusText.Text = browsers.Count == 0
            ? "No supported browsers detected on this system."
            : "Detected: " + string.Join(", ", browsers.Select(b => b.Name)) + ".";
    }

    private void Tab_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: string tag })
        {
            if (PanelConnection != null) PanelConnection.Visibility = tag == "Connection" ? Visibility.Visible : Visibility.Collapsed;
            if (PanelFolders != null) PanelFolders.Visibility = tag == "Folders" ? Visibility.Visible : Visibility.Collapsed;
            if (PanelBrowser != null) PanelBrowser.Visibility = tag == "Browser" ? Visibility.Visible : Visibility.Collapsed;
            if (PanelBehavior != null) PanelBehavior.Visibility = tag == "Behavior" ? Visibility.Visible : Visibility.Collapsed;
            if (PanelAppearance != null) PanelAppearance.Visibility = tag == "Appearance" ? Visibility.Visible : Visibility.Collapsed;
            if (PanelUpdates != null) PanelUpdates.Visibility = tag == "Updates" ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void ThemeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isInitializingAppearance) return;
        PreviewAppearance();
    }

    private void DarkMode_Changed(object sender, RoutedEventArgs e)
    {
        if (_isInitializingAppearance) return;
        PreviewAppearance();
    }

    private void PreviewAppearance()
    {
        if (ThemeBox.SelectedItem is not ComboBoxItem item || item.Tag is not string tag)
            return;
        var theme = tag == "WdmOriginal" ? AppTheme.WdmOriginal : AppTheme.Default;
        bool dark = DarkModeBox.IsChecked == true;
        ThemeService.Apply(theme, dark);
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
            DialogResult = false;
            Close();
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
            FolderBox.Text = dialog.FolderName;
    }

    private void ScriptBrowseClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "Programs and scripts (*.exe;*.bat;*.cmd;*.ps1;*.py)|*.exe;*.bat;*.cmd;*.ps1;*.py|All files (*.*)|*.*",
        };
        if (dialog.ShowDialog() == true)
            ScriptBox.Text = dialog.FileName;
    }

    private void OpenExtensionHelper_Click(object sender, RoutedEventArgs e)
    {
        var mainWin = Application.Current.Windows.OfType<MainWindow>().FirstOrDefault();
        if (mainWin is not null)
        {
            mainWin.ShowExtensionInstallerDialog();
        }
        else
        {
            var helper = new BrowserExtensionDialog { Owner = this };
            helper.ShowDialog();
        }
    }

    private void SaveClick(object sender, RoutedEventArgs e)
    {
        var s = _viewModel.Settings;
        s.DownloadFolder = string.IsNullOrWhiteSpace(FolderBox.Text)
            ? DownloadTask.DefaultSaveFolder
            : FolderBox.Text;

        s.DefaultChunkCount = ChunksBox.SelectedItem is ComboBoxItem chunks && chunks.Tag is string tag && int.TryParse(tag, out int c)
            ? c
            : 0;

        s.MaxConcurrentDownloads = MaxConcurrentBox.SelectedItem is ComboBoxItem mc && int.TryParse(mc.Content.ToString(), out int m)
            ? m
            : 3;

        s.MaxRetries = RetriesBox.SelectedItem is ComboBoxItem r && int.TryParse(ExtractFirstDigit(r.Content.ToString()!), out int retries)
            ? retries
            : 3;

        s.GlobalSpeedLimitKbps = long.TryParse(SpeedBox.Text.Trim(), out long speed) && speed >= 0
            ? speed
            : 0;

        s.RouteByCategory = RouteBox.IsChecked == true;
        s.CategoryFolders = new Dictionary<string, string>
        {
            [DownloadCategory.Video.ToString()] = VideoFolderBox.Text.Trim(),
            [DownloadCategory.Music.ToString()] = MusicFolderBox.Text.Trim(),
            [DownloadCategory.Document.ToString()] = DocumentFolderBox.Text.Trim(),
            [DownloadCategory.Compressed.ToString()] = CompressedFolderBox.Text.Trim(),
            [DownloadCategory.Program.ToString()] = ProgramFolderBox.Text.Trim(),
        };

        s.ComputeChecksum = ChecksumBox.IsChecked == true;
        s.PostDownloadScript = string.IsNullOrWhiteSpace(ScriptBox.Text) ? null : ScriptBox.Text.Trim();

        s.NotifyOnCompletion = NotifyBox.IsChecked == true;
        s.ShowTrayProgress = TrayProgressBox.IsChecked == true;
        s.MinimizeToTray = MinimizeToTrayBox.IsChecked == true;
        s.RunAtStartup = RunAtStartupBox.IsChecked == true;
        s.CheckForUpdates = CheckForUpdatesBox.IsChecked == true;

        // Appearance — persist theme family and dark mode (already previewed, now save)
        var selectedTheme = s.Theme;
        var selectedDark = s.UseDarkTheme;
        if (ThemeBox.SelectedItem is ComboBoxItem tItem && tItem.Tag is string tTag)
        {
            selectedTheme = tTag == "WdmOriginal" ? AppTheme.WdmOriginal : AppTheme.Default;
        }
        selectedDark = DarkModeBox.IsChecked == true;
        bool themeFamilyChanged = selectedTheme != _originalTheme;
        s.Theme = selectedTheme;
        s.UseDarkTheme = selectedDark;
        // Apply via ViewModel to keep IsDarkTheme/SelectedTheme notifications in sync (palette + Theme.xaml)
        _viewModel.SelectedTheme = s.Theme;
        _viewModel.IsDarkTheme = s.UseDarkTheme;
        TaskStore.SaveSettings(s);

        // Whole UI (MainWindow layout, WdmWindow chrome) is family-specific — needs restart to reload the correct Window XAML
        if (themeFamilyChanged)
        {
            var result = MessageBox.Show(
                "The UI theme (layout) has changed. WDM needs to restart to apply the new window chrome and toolbar. Restart now?",
                "Restart required",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            if (result == MessageBoxResult.Yes)
            {
                // Relaunch and exit — App.OnStartup will pick the new Theme and create the correct MainWindow
                try
                {
                    var exe = Environment.ProcessPath ?? System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
                    if (!string.IsNullOrWhiteSpace(exe))
                        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe) { UseShellExecute = true });
                }
                catch { }
                Application.Current.Shutdown();
                return;
            }
        }

        DialogResult = true;
        Close();
    }

    private static string ExtractFirstDigit(string input)
    {
        var match = System.Text.RegularExpressions.Regex.Match(input, @"\d+");
        return match.Success ? match.Value : "0";
    }

    private void CancelClick(object sender, RoutedEventArgs e)
    {
        // Revert previewed theme if user cancelled
        if (ThemeService.CurrentTheme != _originalTheme || ThemeService.IsDark != _originalDark)
        {
            ThemeService.Apply(_originalTheme, _originalDark);
        }
        DialogResult = false;
        Close();
    }
}
