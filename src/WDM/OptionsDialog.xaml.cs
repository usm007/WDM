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

    public OptionsDialog(MainViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        var s = viewModel.Settings;

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

        PopulateBrowsers();
    }

    private void PopulateBrowsers()
    {
        if (BrowserList == null) return;

        var browsers = BrowserIntegration.DetectInstalledBrowsers();
        if (browsers.Count == 0)
        {
            BrowserStatusText.Text = "No supported browsers detected on this system.";
            return;
        }

        BrowserStatusText.Text = $"Detected {browsers.Count} browser(s):";

        foreach (var browser in browsers)
        {
            var row = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 0, 0, 8),
            };

            var icon = new TextBlock
            {
                Text = browser.Kind == BrowserKind.Firefox ? "\uE7B4" : "\uE774",
                FontFamily = new System.Windows.Media.FontFamily("Segoe MDL2 Assets"),
                FontSize = 14,
                Foreground = (System.Windows.Media.Brush)FindResource("Brush.TextDim"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0),
            };

            var label = new TextBlock
            {
                Text = browser.Name,
                FontSize = 13,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 12, 0),
            };

            var status = new TextBlock
            {
                Text = "Not installed",
                FontSize = 11,
                Foreground = (System.Windows.Media.Brush)FindResource("Brush.TextDim"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 12, 0),
            };

            var button = new Button
            {
                Content = "Install",
                Style = (Style)FindResource("Button.Secondary"),
                Height = 28,
                MinWidth = 80,
                Padding = new Thickness(14, 0, 14, 0),
            };

            button.Click += (_, _) => InstallBrowser(browser, status);
            status.Text = IsBrowserInjected(browser) ? "Installed" : "Not installed";

            row.Children.Add(icon);
            row.Children.Add(label);
            row.Children.Add(status);
            row.Children.Add(button);
            BrowserList.Children.Add(row);
        }
    }

    private void InstallBrowser(InstalledBrowser browser, TextBlock status)
    {
        try
        {
            string message = browser.Kind == BrowserKind.Firefox
                ? BrowserIntegration.InstallFirefoxViaPolicy(browser)
                : BrowserIntegration.LoadChromiumViaCdp(browser);
            status.Text = "Installed";
            status.Foreground = (System.Windows.Media.Brush)FindResource("Brush.Success");
            BrowserStatusText.Text = message;
        }
        catch (Exception ex)
        {
            status.Text = "Failed";
            status.Foreground = (System.Windows.Media.Brush)FindResource("Brush.Danger");
            BrowserStatusText.Text = ex.Message;
        }
    }

    private static bool IsBrowserInjected(InstalledBrowser browser)
    {
        return browser.Kind switch
        {
            BrowserKind.Firefox => BrowserIntegration.IsFirefoxPolicyRegistered(),
            _ => BrowserIntegration.IsChromiumStoreRegistered(browser),
        };
    }

    private void Tab_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: string tag })
        {
            if (PanelConnection != null) PanelConnection.Visibility = tag == "Connection" ? Visibility.Visible : Visibility.Collapsed;
            if (PanelFolders != null) PanelFolders.Visibility = tag == "Folders" ? Visibility.Visible : Visibility.Collapsed;
            if (PanelBrowser != null) PanelBrowser.Visibility = tag == "Browser" ? Visibility.Visible : Visibility.Collapsed;
            if (PanelBehavior != null) PanelBehavior.Visibility = tag == "Behavior" ? Visibility.Visible : Visibility.Collapsed;
            if (PanelUpdates != null) PanelUpdates.Visibility = tag == "Updates" ? Visibility.Visible : Visibility.Collapsed;
        }
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
        DialogResult = false;
        Close();
    }
}
