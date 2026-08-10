using System.Net.Http;
using System.Net.Http.Headers;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using WDM.Models;
using WDM.Services;
using WDM.ViewModels;

namespace WDM;

public partial class AddDownloadDialog : Window
{
    private readonly MainViewModel _viewModel;
    private string _lastDerivedName = "";
    private CancellationTokenSource? _probeCts;

    public AddDownloadDialog(MainViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        FolderBox.Text = viewModel.Settings.DownloadFolder;
        ChunksBox.SelectedIndex = Math.Clamp(ChunkIndex(viewModel.Settings.DefaultChunkCount), 0, ChunksBox.Items.Count - 1);
        CategoryBox.SelectedIndex = 0;
        LaterTimeBox.Text = DateTime.Now.AddMinutes(5).ToString("yyyy-MM-dd HH:mm");
        UrlBox.Focus();
    }

    private void StartNow_Checked(object sender, RoutedEventArgs e) { if (LaterTimeBox != null) LaterTimeBox.IsEnabled = false; }

    private void StartLater_Checked(object sender, RoutedEventArgs e) { if (LaterTimeBox != null) LaterTimeBox.IsEnabled = true; }

    private void ApplyRouting()
    {
        if (!_viewModel.Settings.RouteByCategory)
            return;
        var category = SelectedCategory();
        if (category == DownloadCategory.Other)
            return;
        if (_viewModel.Settings.CategoryFolders.TryGetValue(category.ToString(), out string? folder) &&
            !string.IsNullOrWhiteSpace(folder))
        {
            FolderBox.Text = folder;
        }
    }

    private DownloadCategory SelectedCategory()
    {
        if (CategoryBox.SelectedItem is ComboBoxItem item && item.Tag is string tag && tag != "Auto")
        {
            if (Enum.TryParse<DownloadCategory>(tag, out var category))
                return category;
        }
        string name = string.IsNullOrWhiteSpace(NameBox.Text) ? DownloadEngine.DeriveName(UrlBox.Text.Trim()) : NameBox.Text;
        return DownloadTask.Categorize(name);
    }

    private static int ChunkIndex(int chunks) => chunks switch
    {
        1 => 0,
        2 => 1,
        8 => 3,
        16 => 4,
        _ => 2,
    };

    private void UrlBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        string url = UrlBox.Text.Trim();
        bool isValid = Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
                       (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeFtp);

        OkButton.IsEnabled = isValid;
        DuplicateWarning.Visibility = _viewModel.ExistingUrl(url) ? Visibility.Visible : Visibility.Collapsed;

        if (!isValid)
        {
            ProbeBadge.Visibility = Visibility.Collapsed;
            return;
        }

        string derived = DownloadEngine.DeriveName(url);
        if (derived != _lastDerivedName)
        {
            if (string.IsNullOrWhiteSpace(NameBox.Text) || NameBox.Text == _lastDerivedName)
                NameBox.Text = derived;
            _lastDerivedName = derived;
            ApplyRouting();
        }

        ProbeUrlAsync(url);
    }

    private async void ProbeUrlAsync(string url)
    {
        _probeCts?.Cancel();
        _probeCts = new CancellationTokenSource();
        var ct = _probeCts.Token;

        ProbeBadge.Visibility = Visibility.Visible;
        ProbeIcon.Text = "\uE916";
        ProbeText.Text = "Inspecting URL capabilities...";

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(4) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) WDM/1.0");

            var req = new HttpRequestMessage(HttpMethod.Head, url);
            var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);

            long totalBytes = resp.Content.Headers.ContentLength ?? -1;
            bool supportsRanges = resp.Headers.AcceptRanges.Any(r => r.Equals("bytes", StringComparison.OrdinalIgnoreCase));

            if (!supportsRanges && resp.IsSuccessStatusCode)
            {
                // Range test with byte=0-0 fallback probe
                var rangeReq = new HttpRequestMessage(HttpMethod.Get, url);
                rangeReq.Headers.Range = new RangeHeaderValue(0, 0);
                var rangeResp = await http.SendAsync(rangeReq, HttpCompletionOption.ResponseHeadersRead, ct);
                supportsRanges = rangeResp.StatusCode == System.Net.HttpStatusCode.PartialContent;
                if (totalBytes <= 0 && rangeResp.Content.Headers.ContentRange?.Length is long len)
                    totalBytes = len;
            }

            if (ct.IsCancellationRequested)
                return;

            string sizeStr = totalBytes > 0 ? DownloadTask.FormatBytes(totalBytes) : "Unknown size";
            if (supportsRanges)
            {
                ProbeIcon.Text = "\uE946";
                ProbeText.Text = $"{sizeStr} • Multi-threaded resume supported";
            }
            else
            {
                ProbeIcon.Text = "\uEA39";
                ProbeText.Text = $"{sizeStr} • Single-thread download (Server doesn't support resuming)";
            }
        }
        catch (OperationCanceledException)
        {
            // Cancelled due to new typing
        }
        catch
        {
            if (!ct.IsCancellationRequested)
            {
                ProbeIcon.Text = "\uE946";
                ProbeText.Text = "URL ready for download";
            }
        }
    }

    private void CategoryBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => ApplyRouting();

    private void BrowseClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            InitialDirectory = FolderBox.Text,
        };
        if (dialog.ShowDialog() == true)
            FolderBox.Text = dialog.FolderName;
    }

    private void OkClick(object sender, RoutedEventArgs e)
    {
        string url = UrlBox.Text.Trim();
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeFtp))
        {
            MessageBox.Show(this, "Enter a valid http(s) or ftp URL.", "Invalid URL", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        int chunks = 4;
        if (ChunksBox.SelectedItem is ComboBoxItem item && int.TryParse(item.Content.ToString(), out int parsed))
            chunks = parsed;

        long speedLimit = 0;
        if (!long.TryParse(SpeedBox.Text.Trim(), out speedLimit) || speedLimit < 0)
            speedLimit = 0;

        DateTime? scheduledStart = null;
        if (StartLater.IsChecked == true &&
            DateTime.TryParse(LaterTimeBox.Text.Trim(), out DateTime later))
        {
            scheduledStart = later;
        }

        var task = new DownloadTask(Application.Current.Dispatcher)
        {
            Url = url,
            SaveFolder = string.IsNullOrWhiteSpace(FolderBox.Text) ? DownloadTask.DefaultSaveFolder : FolderBox.Text,
            FileName = string.IsNullOrWhiteSpace(NameBox.Text) ? "" : DownloadEngine.SanitizeFileName(NameBox.Text.Trim()),
            ChunkCount = Math.Max(1, chunks),
            SpeedLimitKbps = speedLimit,
            Category = SelectedCategory(),
            ScheduledStart = scheduledStart,
        };

        _viewModel.AddTask(task);
        DialogResult = true;
        Close();
    }
}
