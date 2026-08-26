using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Windows.Media.Imaging;
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
    private readonly Dictionary<string, string>? _prefillHeaders;
    private string _lastDerivedName = "";
    private CancellationTokenSource? _probeCts;
    private ResolvedQuery? _lastResolved;
    private List<QualityOption> _ytQualityOptions = new();

    public AddDownloadDialog(MainViewModel viewModel, string? prefillUrl = null, string? prefillFileName = null, string? prefillReferer = null, Dictionary<string, string>? prefillHeaders = null)
    {
        _viewModel = viewModel;
        InitializeComponent();
        _prefillUrl = prefillUrl;
        _prefillFileName = prefillFileName;
        _prefillReferer = prefillReferer;
        _prefillHeaders = prefillHeaders;
        FolderBox.Text = viewModel.Settings.DownloadFolder;
        ChunksBox.SelectedIndex = Math.Clamp(ChunkIndex(viewModel.Settings.DefaultChunkCount), 0, ChunksBox.Items.Count - 1);
        CategoryBox.SelectedIndex = 0;
        UrlBox.Focus();

        // Pre-fill headers from browser extension
        if (prefillHeaders is not null && prefillHeaders.Count > 0 && HeadersBox is not null)
        {
            HeadersBox.Text = string.Join("\n", prefillHeaders.Select(kv => $"{kv.Key}: {kv.Value}"));
        }

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
        if (_viewModel == null)
            return;
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
        if (_viewModel.Settings.EnableYouTubeDownloads && MediaResolver.IsYoutubeUrl(url))
        {
            ProbeYouTubeUrlAsync(url);
        }
        else
        {
            if (YouTubePanel != null) YouTubePanel.Visibility = Visibility.Collapsed;
            ProbeUrlAsync(url);
        }
    }

    private async void ProbeYouTubeUrlAsync(string url)
    {
        _probeCts?.Cancel();
        _probeCts = new CancellationTokenSource();
        var ct = _probeCts.Token;

        ProbeBadge.Visibility = Visibility.Visible;
        ProbeIcon.Text = char.ConvertFromUtf32(0xF0349);
        ProbeText.Text = "Resolving YouTube video metadata...";

        try
        {
            var res = await MediaResolver.ResolveAsync(url, ct);
            if (ct.IsCancellationRequested) return;

            if (res.Items.Count > 0)
            {
                var item = res.Items[0];
                string cleanTitle = DownloadEngine.SanitizeFileName(item.Title);
                if (string.IsNullOrWhiteSpace(cleanTitle)) cleanTitle = "YouTube_Video";
                _lastDerivedName = cleanTitle + ".mp4";
                NameBox.Text = _lastDerivedName;

                _lastResolved = res;
                _ytQualityOptions = res.QualityOptions;
                ShowYouTubePanel(res.IsPlaylist);

                ProbeIcon.Text = char.ConvertFromUtf32(0xF0381);
                string durStr = item.Duration.HasValue ? $" ({item.Duration.Value:mm\\:ss})" : "";
                ProbeText.Text = $"YouTube Media • {item.Title}{durStr}";
                CategoryBox.SelectedIndex = 1; // Video

                // Load thumbnail asynchronously.
                if (!string.IsNullOrWhiteSpace(item.ThumbnailUrl) && YtThumbnail != null)
                {
                    try
                    {
                        var bmp = new BitmapImage();
                        bmp.BeginInit();
                        bmp.UriSource = new Uri(item.ThumbnailUrl);
                        bmp.DecodePixelWidth = 144; // 2x for HiDPI
                        bmp.CacheOption = BitmapCacheOption.OnLoad;
                        bmp.EndInit();
                        bmp.Freeze();
                        YtThumbnail.Source = bmp;
                        YtThumbnail.Visibility = Visibility.Visible;
                    }
                    catch
                    {
                        YtThumbnail.Visibility = Visibility.Collapsed;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            if (!ct.IsCancellationRequested)
            {
                ProbeIcon.Text = char.ConvertFromUtf32(0xF0028);
                ProbeText.Text = "YouTube analysis: " + ex.Message;
                if (YtThumbnail != null) YtThumbnail.Visibility = Visibility.Collapsed;
                // Still show the options panel with fallback tiers so the user
                // can pick a quality even when metadata resolution fails.
                _lastResolved = null;
                _ytQualityOptions = new List<QualityOption>();
                ShowYouTubePanel(isPlaylist: false);
            }
        }
    }

    private void NameBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateCategoryBadge();
    }

    // ── YouTube options panel ──────────────────────────────────────────
    private bool IsAudioOnly => YtTypeAudioOnly?.IsChecked == true;
    private bool IsVideoOnly => YtTypeVideoOnly?.IsChecked == true;

    private void ShowYouTubePanel(bool isPlaylist)
    {
        if (YouTubePanel == null) return;
        YouTubePanel.Visibility = Visibility.Visible;
        YtPlaylist.Visibility = isPlaylist ? Visibility.Visible : Visibility.Collapsed;
        if (_ytQualityOptions.Count == 0 && _lastResolved?.QualityOptions.Count > 0)
            _ytQualityOptions = _lastResolved.QualityOptions;
        PopulateYtQuality();
    }

    private static List<QualityOption> BuildFallbackQuality() =>
        MediaResolver.Tiers.Where(t => t.Height >= 0).Select(t => new QualityOption
        {
            Label = t.Label,
            FormatArg = t.Height == 0 ? "bestvideo+bestaudio/best" : $"bestvideo[height<={t.Height}]+bestaudio/best[height<={t.Height}]",
        }).ToList();

    private void PopulateYtQuality()
    {
        if (YtQualityBox == null) return;
        if (IsAudioOnly)
        {
            YtQualityLabel.Text = "Audio quality";
            YtQualityBox.ItemsSource = new List<string> { "Best available", "320 kbps", "192 kbps", "128 kbps", "70 kbps" };
            YtQualityBox.Tag = null;
            YtQualityBox.SelectedIndex = 0;
            return;
        }
        YtQualityLabel.Text = "Quality";
        var opts = _ytQualityOptions.Count > 0 ? _ytQualityOptions : BuildFallbackQuality();
        YtQualityBox.ItemsSource = opts.Select(q => q.Label).ToList();
        YtQualityBox.Tag = opts;
        YtQualityBox.SelectedIndex = 0;
    }

    private void YtType_Changed(object sender, RoutedEventArgs e)
    {
        if (YouTubePanel == null || CategoryBox == null || NameBox == null)
            return;
        if (YtAudioFormatPanel == null || YtContainerPanel == null)
            return;
        bool audio = IsAudioOnly;
        YtAudioFormatPanel.Visibility = audio ? Visibility.Visible : Visibility.Collapsed;
        YtContainerPanel.Visibility = audio ? Visibility.Collapsed : Visibility.Visible;
        PopulateYtQuality();
        if (audio) CategoryBox.SelectedIndex = 2; // Music
        else if (CategoryBox.SelectedIndex == 2 && (sender == YtTypeVideoOnly || sender == YtTypeVideoAudio))
            CategoryBox.SelectedIndex = 1; // Video
        UpdateExtensionForType();
        UpdateCategoryBadge();
    }

    private void YtAudioFormat_Changed(object sender, SelectionChangedEventArgs e) => UpdateExtensionForType();

    private void UpdateExtensionForType()
    {
        if (YtAudioFormatBox == null || NameBox == null) return;
        string want = ".mp4";
        if (IsAudioOnly)
        {
            string fmt = YtAudioFormatBox.SelectedItem is ComboBoxItem it && it.Tag is string tg && tg != "best" ? tg : "mp3";
            want = "." + fmt;
            if (fmt == "opus") want = ".opus";
        }
        var known = new[] { ".mp4", ".mp3", ".m4a", ".opus", ".webm", ".mkv" };
        string name = NameBox.Text;
        foreach (var ext in known)
        {
            if (name.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
            {
                NameBox.Text = name.Substring(0, name.Length - ext.Length) + want;
                _lastDerivedName = NameBox.Text;
                return;
            }
        }
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
            var handler = new SocketsHttpHandler
            {
                AllowAutoRedirect = true,
                UseCookies = false,
            };
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(6) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/125.0 Safari/537.36");

            var req = new HttpRequestMessage(HttpMethod.Head, url);

            // Apply custom headers from dialog (Cookie, Referer, Authorization)
            var customHeaders = ParseHeaders();
            if (!string.IsNullOrWhiteSpace(_prefillReferer) && !customHeaders.ContainsKey("Referer"))
            {
                customHeaders["Referer"] = _prefillReferer;
            }

            foreach (var kv in customHeaders)
            {
                if (!string.IsNullOrWhiteSpace(kv.Key) && !string.IsNullOrWhiteSpace(kv.Value))
                    req.Headers.TryAddWithoutValidation(kv.Key, kv.Value);
            }

            using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);

            long totalBytes = resp.Content.Headers.ContentLength ?? -1;
            bool supportsRanges = resp.Headers.AcceptRanges.Any(r => r.Equals("bytes", StringComparison.OrdinalIgnoreCase));
            string? rangeDispositionRaw = null;

            if (!supportsRanges && resp.IsSuccessStatusCode)
            {
                // Range test with byte=0-0 fallback probe
                var rangeReq = new HttpRequestMessage(HttpMethod.Get, url);
                rangeReq.Headers.Range = new RangeHeaderValue(0, 0);
                foreach (var kv in customHeaders)
                {
                    if (!string.IsNullOrWhiteSpace(kv.Key) && !string.IsNullOrWhiteSpace(kv.Value))
                        rangeReq.Headers.TryAddWithoutValidation(kv.Key, kv.Value);
                }
                using HttpResponseMessage rangeResp = await http.SendAsync(rangeReq, HttpCompletionOption.ResponseHeadersRead, ct);
                supportsRanges = rangeResp.StatusCode == System.Net.HttpStatusCode.PartialContent;
                if (totalBytes <= 0 && rangeResp.Content.Headers.ContentRange?.Length is long len)
                    totalBytes = len;
                string? rangeRaw = rangeResp.Content.Headers.TryGetValues("Content-Disposition", out var rvals) ? rvals.FirstOrDefault() : null;
                rangeDispositionRaw = rangeRaw;
            }

            // If the server announces a real filename via Content-Disposition, use it —
            // URLs with signed/tokenized paths (e.g. googleusercontent) don't carry an
            // extension, so DeriveName would otherwise fall back to a meaningless .bin.
            string? dispositionRaw = resp.Content.Headers.TryGetValues("Content-Disposition", out var vals) ? vals.FirstOrDefault() : null;
            string? dispositionName = FileNameHelper.ParseDispositionFileName(dispositionRaw);
            if (string.IsNullOrWhiteSpace(dispositionName))
                dispositionName = FileNameHelper.ParseDispositionFileName(rangeDispositionRaw);
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
        var headers = ParseHeaders();

        bool isYouTube = _viewModel.Settings.EnableYouTubeDownloads && MediaResolver.IsYoutubeUrl(url);
        string? formatArg = null;
        var extraArgs = new List<string>();
        if (isYouTube)
        {
            bool audioOnly = IsAudioOnly;
            bool videoOnly = IsVideoOnly;

            if (audioOnly)
            {
                formatArg = "bestaudio/best";
                string fmt = YtAudioFormatBox?.SelectedItem is ComboBoxItem ai && ai.Tag is string at ? at : "best";
                extraArgs.Add("--extract-audio");
                if (fmt != "best")
                {
                    extraArgs.Add("--audio-format");
                    extraArgs.Add(fmt);
                }
                int qIdx = YtQualityBox?.SelectedIndex ?? 0;
                if (qIdx > 0)
                {
                    string[] rates = { "0", "320K", "192K", "128K", "70K" };
                    extraArgs.Add("--audio-quality");
                    extraArgs.Add(rates[Math.Min(qIdx, rates.Length - 1)]);
                }
            }
            else
            {
                string? fa = null;
                if (YtQualityBox?.Tag is List<QualityOption> opts && YtQualityBox.SelectedIndex >= 0 && YtQualityBox.SelectedIndex < opts.Count)
                    fa = opts[YtQualityBox.SelectedIndex].FormatArg;
                if (string.IsNullOrWhiteSpace(fa))
                    fa = videoOnly ? "bestvideo/best" : "bestvideo+bestaudio/best";
                if (videoOnly)
                {
                    int plus = fa.IndexOf("+bestaudio", StringComparison.OrdinalIgnoreCase);
                    if (plus > 0) fa = fa.Substring(0, plus);
                    if (!fa.StartsWith("bestvideo", StringComparison.OrdinalIgnoreCase))
                        fa = "bestvideo/best";
                }
                formatArg = fa;

                if (YtContainerBox?.SelectedItem is ComboBoxItem ci && ci.Tag is string ct && !string.IsNullOrWhiteSpace(ct))
                {
                    extraArgs.Add("--merge-output-format");
                    extraArgs.Add(ct);
                }
            }

            if (YtEmbedThumb?.IsChecked == true) extraArgs.Add("--embed-thumbnail");
            if (YtEmbedSubs?.IsChecked == true) extraArgs.Add("--embed-subs");
            if (YtPlaylist?.IsChecked == true) extraArgs.Add("--yes-playlist");
            else extraArgs.Add("--no-playlist");
        }

        var task = new DownloadTask(Application.Current.Dispatcher)
        {
            Url = url,
            Referer = _prefillReferer,
            Mirrors = mirrors,
            Headers = headers,
            SaveFolder = string.IsNullOrWhiteSpace(FolderBox.Text) ? DownloadTask.DefaultSaveFolder : FolderBox.Text,
            FileName = finalFileName,
            ChunkCount = Math.Max(0, chunks),
            SpeedLimitKbps = speedLimit,
            Category = SelectedCategory(),
            IsYouTube = isYouTube,
            YouTubeFormatArg = formatArg,
            YouTubeExtraArgs = extraArgs.Count > 0 ? string.Join("\n", extraArgs) : null,
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

    private Dictionary<string, string> ParseHeaders()
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(HeadersBox?.Text))
            return headers;
        foreach (string line in HeadersBox.Text.Split(
                     new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int colon = line.IndexOf(':');
            if (colon < 1)
                continue;
            string key = line[..colon].Trim();
            string value = line[(colon + 1)..].Trim();
            if (!string.IsNullOrWhiteSpace(key) && !string.IsNullOrWhiteSpace(value))
                headers[key] = value;
        }
        return headers;
    }

    private void CancelClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
