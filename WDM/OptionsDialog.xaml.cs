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

        ThrottleBox.IsChecked = s.ThrottleScheduleEnabled;
        ThrottleStartBox.Text = s.ThrottleStart;
        ThrottleEndBox.Text = s.ThrottleEnd;
        ThrottleLimitBox.Text = s.ThrottleLimitKbps.ToString();

        WindowBox.IsChecked = s.DownloadWindowEnabled;
        WindowStartBox.Text = s.WindowStart;
        WindowEndBox.Text = s.WindowEnd;

        RouteBox.IsChecked = s.RouteByCategory;
        VideoFolderBox.Text = s.CategoryFolders.GetValueOrDefault(DownloadCategory.Video.ToString()) ?? "";
        MusicFolderBox.Text = s.CategoryFolders.GetValueOrDefault(DownloadCategory.Music.ToString()) ?? "";
        DocumentFolderBox.Text = s.CategoryFolders.GetValueOrDefault(DownloadCategory.Document.ToString()) ?? "";
        CompressedFolderBox.Text = s.CategoryFolders.GetValueOrDefault(DownloadCategory.Compressed.ToString()) ?? "";
        ProgramFolderBox.Text = s.CategoryFolders.GetValueOrDefault(DownloadCategory.Program.ToString()) ?? "";

        ChecksumBox.IsChecked = s.ComputeChecksum;
        ScriptBox.Text = s.PostDownloadScript ?? "";

        MonitorClipboardBox.IsChecked = s.MonitorClipboard;
        NotifyBox.IsChecked = s.NotifyOnCompletion;
        MinimizeToTrayBox.IsChecked = s.MinimizeToTray;
        RunAtStartupBox.IsChecked = s.RunAtStartup;
    }

    private void Tab_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: string tag })
        {
            if (PanelConnection != null) PanelConnection.Visibility = tag == "Connection" ? Visibility.Visible : Visibility.Collapsed;
            if (PanelFolders != null) PanelFolders.Visibility = tag == "Folders" ? Visibility.Visible : Visibility.Collapsed;
            if (PanelBrowser != null) PanelBrowser.Visibility = tag == "Browser" ? Visibility.Visible : Visibility.Collapsed;
            if (PanelScheduler != null) PanelScheduler.Visibility = tag == "Scheduler" ? Visibility.Visible : Visibility.Collapsed;
            if (PanelBehavior != null) PanelBehavior.Visibility = tag == "Behavior" ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private static int ChunkIndex(int chunks) => chunks switch
    {
        1 => 0,
        2 => 1,
        8 => 3,
        16 => 4,
        _ => 2,
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

    private void SaveClick(object sender, RoutedEventArgs e)
    {
        var s = _viewModel.Settings;
        s.DownloadFolder = string.IsNullOrWhiteSpace(FolderBox.Text)
            ? DownloadTask.DefaultSaveFolder
            : FolderBox.Text;

        s.DefaultChunkCount = ChunksBox.SelectedItem is ComboBoxItem chunks && int.TryParse(ExtractFirstDigit(chunks.Content.ToString()!), out int c)
            ? c
            : 4;

        s.MaxConcurrentDownloads = MaxConcurrentBox.SelectedItem is ComboBoxItem mc && int.TryParse(mc.Content.ToString(), out int m)
            ? m
            : 3;

        s.MaxRetries = RetriesBox.SelectedItem is ComboBoxItem r && int.TryParse(ExtractFirstDigit(r.Content.ToString()!), out int retries)
            ? retries
            : 3;

        s.GlobalSpeedLimitKbps = long.TryParse(SpeedBox.Text.Trim(), out long speed) && speed >= 0
            ? speed
            : 0;

        s.ThrottleScheduleEnabled = ThrottleBox.IsChecked == true;
        s.ThrottleStart = ThrottleStartBox.Text.Trim();
        s.ThrottleEnd = ThrottleEndBox.Text.Trim();
        s.ThrottleLimitKbps = long.TryParse(ThrottleLimitBox.Text.Trim(), out long throttleLimit) && throttleLimit >= 0
            ? throttleLimit
            : 0;

        s.DownloadWindowEnabled = WindowBox.IsChecked == true;
        s.WindowStart = WindowStartBox.Text.Trim();
        s.WindowEnd = WindowEndBox.Text.Trim();

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

        s.MonitorClipboard = MonitorClipboardBox.IsChecked == true;
        s.NotifyOnCompletion = NotifyBox.IsChecked == true;
        s.MinimizeToTray = MinimizeToTrayBox.IsChecked == true;
        s.RunAtStartup = RunAtStartupBox.IsChecked == true;

        DialogResult = true;
        Close();
    }

    private static string ExtractFirstDigit(string input)
    {
        var match = System.Text.RegularExpressions.Regex.Match(input, @"\d+");
        return match.Success ? match.Value : "0";
    }
}
