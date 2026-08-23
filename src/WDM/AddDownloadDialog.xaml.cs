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
    private readonly string? _prefillUrl;
    private readonly string? _prefillFileName;
    private readonly string? _prefillReferer;
    private string _lastDerivedName = "";
    private CancellationTokenSource? _probeCts;

    public AddDownloadDialog(MainViewModel viewModel, string? prefillUrl = null, string? prefillFileName = null, string? prefillReferer = null)
    {
        InitializeComponent();
        _viewModel = viewModel;
        _prefillUrl = prefillUrl;
        _prefillFileName = prefillFileName;
        _prefillReferer = prefillReferer;
        FolderBox.Text = viewModel.Settings.DownloadFolder;
        ChunksBox.SelectedIndex = Math.Clamp(ChunkIndex(viewModel.Settings.DefaultChunkCount), 0, ChunksBox.Items.Count - 1);
        CategoryBox.SelectedIndex = 0;
        UrlBox.Focus();

        Loaded += (_, _) =>
        {
            if (!string.IsNullOrWhiteSpace(_prefillUrl))
            {
                UrlBox.Text = _prefillUrl.Trim();
                if (!string.IsNullOrWhiteSpace(_prefillFileName))
                {
                    NameBox.Text = DownloadEngine.SanitizeFileName(_prefillFileName);
                }
                UrlBox.SelectAll();
                UrlBox.Focus();
            }
            else
            {
                AutoPasteClipboardUrl();
            }
        };
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        WDM.Services.ThemeService.ApplyTitleBar(this);
    }

    private void AutoPasteClipboardUrl()
    {
        try
        {
            if (Clipboard.ContainsText())
            {
                string clip = Clipboard.GetText().Trim();
                if (Uri.TryCreate(clip, UriKind.Absolute, out var uri) &&
                    (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeFtp))
                {
                    UrlBox.Text = clip;
                    UrlBox.SelectAll();
                }
            }
        }
        catch
        {
            // Clipboard access protection
        }
    }

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
        0 => 0,
        1 => 1,
        2 => 2,
        4 => 3,
        8 => 4,
        16 => 5,
        _ => 0,
    };

    private void UrlBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        string url = UrlBox.Text.Trim();
        bool isValid = Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
                       (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeFtp);

        OkButton.IsEnabled = isValid;
        StartHint.Visibility = isValid ? Visibility.Collapsed : Visibility.Visible;
        DuplicateWarning.Visibility = _viewModel.ExistingUrl(url) ? Visibility.Visible : Visibility.Collapsed;

        if (!isValid)
        {
            ProbeBadge.Visibility = Visibility.Collapsed;
            UpdateCategoryBadge();
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

        UpdateCategoryBadge();
        ProbeUrlAsync(url);
    }

    private void NameBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateCategoryBadge();
    }

    private void UpdateCategoryBadge()
    {
        if (FileCategoryIcon is null || FileCategoryLabel is null) return;
        var cat = SelectedCategory();
        FileCategoryLabel.Text = cat.ToString();
        FileCategoryIcon.Text = cat switch
        {
            DownloadCategory.Video => char.ConvertFromUtf32(0xF0381),
            DownloadCategory.Music => char.ConvertFromUtf32(0xF0387),
            DownloadCategory.Document => char.ConvertFromUtf32(0xF0219),
            DownloadCategory.Compressed => char.ConvertFromUtf32(0xF05C4),
            DownloadCategory.Program => char.ConvertFromUtf32(0xF08C6),
            _ => char.ConvertFromUtf32(0xF0224),
        };
    }

    private async void ProbeUrlAsync(string url)
    {
        _probeCts?.Cancel();
        _probeCts = new CancellationTokenSource();
        var ct = _probeCts.Token;

        ProbeBadge.Visibility = Visibility.Visible;
        ProbeIcon.Text = char.ConvertFromUtf32(0xF0349);
        ProbeText.Text = "Inspecting URL capabilities...";

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(4) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) WDM/1.0");

            var req = new HttpRequestMessage(HttpMethod.Head, url);
            var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);

            long totalBytes = resp.Content.Headers.ContentLength ?? -1;
            bool supportsRanges = resp.Headers.AcceptRanges.Any(r => r.Equals("bytes", StringComparison.OrdinalIgnoreCase));
            HttpResponseMessage? rangeResp = null;

            if (!supportsRanges && resp.IsSuccessStatusCode)
            {
                // Range test with byte=0-0 fallback probe
                var rangeReq = new HttpRequestMessage(HttpMethod.Get, url);
                rangeReq.Headers.Range = new RangeHeaderValue(0, 0);
                rangeResp = await http.SendAsync(rangeReq, HttpCompletionOption.ResponseHeadersRead, ct);
                supportsRanges = rangeResp.StatusCode == System.Net.HttpStatusCode.PartialContent;
                if (totalBytes <= 0 && rangeResp.Content.Headers.ContentRange?.Length is long len)
                    totalBytes = len;
            }

            // If the server announces a real filename via Content-Disposition, use it —
            // URLs with signed/tokenized paths (e.g. googleusercontent) don't carry an
            // extension, so DeriveName would otherwise fall back to a meaningless .bin.
            string? dispositionRaw = resp.Content.Headers.TryGetValues("Content-Disposition", out var vals) ? vals.FirstOrDefault() : null;
            string? dispositionName = FileNameHelper.ParseDispositionFileName(dispositionRaw);
            if (string.IsNullOrWhiteSpace(dispositionName) && rangeResp is not null)
            {
                string? rangeRaw = rangeResp.Content.Headers.TryGetValues("Content-Disposition", out var rvals) ? rvals.FirstOrDefault() : null;
                dispositionName = FileNameHelper.ParseDispositionFileName(rangeRaw);
            }
            if (string.IsNullOrWhiteSpace(dispositionName))
                dispositionName = FileNameHelper.FileNameFromS3Query(url);
            if (!string.IsNullOrWhiteSpace(dispositionName)
                && (string.IsNullOrWhiteSpace(NameBox.Text) || NameBox.Text == _lastDerivedName || NameBox.Text.EndsWith(".bin", StringComparison.OrdinalIgnoreCase)))
            {
                _lastDerivedName = DownloadEngine.SanitizeFileName(dispositionName);
                NameBox.Text = _lastDerivedName;
            }

            if (ct.IsCancellationRequested)
                return;

            string sizeStr = totalBytes > 0 ? DownloadTask.FormatBytes(totalBytes) : "Unknown size";
            bool isHls = resp.Content.Headers.ContentType?.MediaType is string mt
                && (mt.Contains("mpegurl", StringComparison.OrdinalIgnoreCase) || mt.Contains("m3u8", StringComparison.OrdinalIgnoreCase))
                || url.Contains(".m3u8", StringComparison.OrdinalIgnoreCase);
            if (isHls)
            {
                ProbeIcon.Text = char.ConvertFromUtf32(0xF05E0);
                ProbeText.Text = $"{sizeStr} • HLS stream (downloads as one media file)";
            }
            else if (supportsRanges)
            {
                ProbeIcon.Text = char.ConvertFromUtf32(0xF05E0);
                ProbeText.Text = $"{sizeStr} • Multi-threaded resume supported";
            }
            else
            {
                ProbeIcon.Text = char.ConvertFromUtf32(0xF05D6);
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
                ProbeIcon.Text = char.ConvertFromUtf32(0xF05E0);
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

        int chunks = 0;
        if (ChunksBox.SelectedItem is ComboBoxItem item && item.Tag is string tag && int.TryParse(tag, out int parsed))
            chunks = parsed;

        long speedLimit = 0;
        if (!long.TryParse(SpeedBox.Text.Trim(), out speedLimit) || speedLimit < 0)
            speedLimit = 0;

        string derivedName = DownloadEngine.DeriveName(url);
        string nameInput = string.IsNullOrWhiteSpace(NameBox.Text) ? derivedName : NameBox.Text.Trim();
        string finalFileName = DownloadEngine.SanitizeFileName(nameInput);
        if (string.IsNullOrWhiteSpace(finalFileName))
            finalFileName = derivedName;

        var mirrors = ParseMirrors();

        var task = new DownloadTask(Application.Current.Dispatcher)
        {
            Url = url,
            Referer = _prefillReferer,
            Mirrors = mirrors,
            SaveFolder = string.IsNullOrWhiteSpace(FolderBox.Text) ? DownloadTask.DefaultSaveFolder : FolderBox.Text,
            FileName = finalFileName,
            ChunkCount = Math.Max(0, chunks),
            SpeedLimitKbps = speedLimit,
            Category = SelectedCategory(),
        };

        _viewModel.AddTask(task);
        DialogResult = true;
        Close();
    }

    private List<string> ParseMirrors()
    {
        var mirrors = new List<string>();
        if (MirrorsBox is null)
            return mirrors;
        foreach (string line in MirrorsBox.Text.Split(
                     new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (Uri.TryCreate(line, UriKind.Absolute, out var uri) &&
                (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeFtp))
            {
                mirrors.Add(line);
            }
        }
        return mirrors;
    }

    private void CancelClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
