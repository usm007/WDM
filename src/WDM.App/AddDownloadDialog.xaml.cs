using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
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
using Wpf.Ui.Controls;

namespace WDM;

public partial class AddDownloadDialog : Window
{
    private readonly MainViewModel _viewModel;
    private readonly string? _prefillUrl;
    private readonly string? _prefillFileName;
    private readonly string? _prefillReferer;
    private readonly Dictionary<string, string>? _prefillHeaders;
    private string _lastDerivedName = "";
    private long _probeTotalBytes = -1;
    // True when the current probe target is a stream (HLS): playlist bytes are
    // not media bytes, so _probeTotalBytes must never seed the task size.
    private CancellationTokenSource? _probeCts;
    private bool _probeIsStream;
    private ResolvedQuery? _lastResolved;
    // Form POST replay + proxy mirror carried from a browser capture. No UI
    // box: replayed silently by the engine at start; the probe badge notes it.
    private string? _prefillPostData;
    private string? _prefillPostContentType;
    private string? _prefillProxyHost;
    private int _prefillProxyPort;
    private string? _prefillProxyType;
    private bool _prefillFullSession;
    // Normalized URL the stashed POST body belongs to (ctor prefill or
    // UpdatePrefill). A body is only replayed/shown while the box still
    // holds exactly this URL — retyping must never send a stale form.
    private string? _postDataUrl;
    private List<QualityOption> _ytQualityOptions = new();
    private List<PlaylistPick> _playlistPicks = new();
    private List<HlsDownloader.HlsVariantInfo> _hlsVariants = new();
    private string _originalHlsUrl = "";
    private bool _suppressHlsSelection;

    public AddDownloadDialog(MainViewModel viewModel, string? prefillUrl = null, string? prefillFileName = null, string? prefillReferer = null, Dictionary<string, string>? prefillHeaders = null,
        string? postData = null, string? postContentType = null, string? proxyHost = null, int proxyPort = 0, string? proxyType = null, bool fullSession = false)
    {
        _viewModel = viewModel;
        InitializeComponent();
        _prefillUrl = prefillUrl;
        _prefillFileName = prefillFileName;
        _prefillReferer = prefillReferer;
        _prefillHeaders = prefillHeaders;
        _prefillPostData = postData;
        _prefillPostContentType = postContentType;
        _prefillProxyHost = proxyHost;
        _prefillProxyPort = proxyPort;
        _prefillProxyType = proxyType;
        _prefillFullSession = fullSession;
        _postDataUrl = !string.IsNullOrWhiteSpace(postData) && !string.IsNullOrWhiteSpace(prefillUrl)
            ? NormalizePastedUrl(prefillUrl) : null;
        FolderBox.Text = viewModel.Settings.DownloadFolder;
        ChunksBox.SelectedIndex = Math.Clamp(ChunkIndex(viewModel.Settings.DefaultChunkCount), 0, ChunksBox.Items.Count - 1);
        CategoryBox.SelectedIndex = 0;
        UrlBox.Focus();

        // Pre-fill headers from browser extension
        if (prefillHeaders is not null && prefillHeaders.Count > 0 && HeadersBox is not null)
        {
            HeadersBox.Text = string.Join("\n", prefillHeaders.Select(kv => $"{kv.Key}: {kv.Value}"));
        }
        Closed += (_, _) =>
        {
            try { _probeCts?.Cancel(); } catch { }
            try { _probeCts?.Dispose(); } catch { }
            _probeCts = null;
        };

        Loaded += (_, _) =>
        {
            if (!string.IsNullOrWhiteSpace(_prefillUrl))
            {
                UrlBox.Text = NormalizePastedUrl(_prefillUrl);
                if (!string.IsNullOrWhiteSpace(_prefillFileName))
                {
                    _lastDerivedName = DownloadEngine.SanitizeFileName(_prefillFileName);
                    NameBox.Text = _lastDerivedName;
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

    public bool IsEmpty => string.IsNullOrWhiteSpace(UrlBox.Text);

    public void UpdatePrefill(string? url, string? fileName = null, string? referer = null, Dictionary<string, string>? headers = null,
        string? postData = null, string? postContentType = null, string? proxyHost = null, int proxyPort = 0, string? proxyType = null, bool fullSession = false)
    {
        if (!string.IsNullOrWhiteSpace(url))
        {
            // Replay fields first: assigning UrlBox.Text fires TextChanged
            // synchronously, and the probe reads them.
            _prefillPostData = postData;
            _prefillPostContentType = postContentType;
            _prefillProxyHost = proxyHost;
            _prefillProxyPort = proxyPort;
            _prefillProxyType = proxyType;
            _prefillFullSession = fullSession;
            _postDataUrl = !string.IsNullOrWhiteSpace(postData) && !string.IsNullOrWhiteSpace(url)
                ? NormalizePastedUrl(url) : null;
            UrlBox.Text = NormalizePastedUrl(url);
            if (!string.IsNullOrWhiteSpace(fileName))
            {
                _lastDerivedName = DownloadEngine.SanitizeFileName(fileName);
                NameBox.Text = _lastDerivedName;
            }
            if (headers is not null && headers.Count > 0 && HeadersBox is not null)
            {
                HeadersBox.Text = string.Join("\n", headers.Select(kv => $"{kv.Key}: {kv.Value}"));
            }
            UrlBox.SelectAll();
            UrlBox.Focus();
        }
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
                string clip = NormalizePastedUrl(Clipboard.GetText());
                if (IsSupportedHttpUrl(clip))
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

    // ponytail: naive first-URL extraction, upgrade to full parser if pastes get wilder
    private static string NormalizePastedUrl(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return "";
        string text = (input ?? "").Trim();
        // Pasted blob with extra text / line-breaks: pull the first http(s) link.
        foreach (string line in text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            string t = line.Trim().Trim('"', '\'', '<', '>', '`', '“', '”', '‘', '’');
            if (t.Length == 0)
                continue;
            int httpIdx = t.IndexOf("https://", StringComparison.OrdinalIgnoreCase);
            int http2Idx = t.IndexOf("http://", StringComparison.OrdinalIgnoreCase);
            int idx = -1;
            if (httpIdx >= 0 && http2Idx >= 0) idx = Math.Min(httpIdx, http2Idx);
            else if (httpIdx >= 0) idx = httpIdx;
            else if (http2Idx >= 0) idx = http2Idx;
            if (idx >= 0)
            {
                string sub = t[idx..].Trim();
                // Cut trailing junk: space, quote, bracket, comma.
                int end = sub.Length;
                foreach (char c in new[] { ' ', '\t', '"', '\'', '<', '>', '`', ',', ';', ')' })
                {
                    int p = sub.IndexOf(c);
                    if (p >= 0 && p < end) end = p;
                }
                string found = sub[..end].Trim().Trim('"', '\'', '<', '>', '`');
                if (found.Length > 0)
                    return found;
            }
        }
        // No embedded http(s): take first non-empty line (magnet/udp/ftp/etc so we can explain it).
        foreach (string line in text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            string t = line.Trim().Trim('"', '\'', '<', '>', '`', '“', '”', '‘', '’');
            if (t.Length == 0)
                continue;
            // Missing scheme like example.com/file.zip → add https://
            string withScheme = TryPrependHttps(t);
            return withScheme;
        }
        return "";
    }

    private static string TryPrependHttps(string t)
    {
        string s = t.Trim().Trim('"', '\'', '<', '>', '`');
        if (s.Length == 0 || s.Contains(' ') || s.Contains('\t'))
            return s;
        if (s.Contains("://"))
            return s;
        // Looks like domain + path but no scheme: example.com/file.zip, www.site.com/x
        int dot = s.IndexOf('.');
        if (dot > 0 && dot < s.Length - 1 && !s.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase))
        {
            string hostPart = s.Split('/')[0];
            if (hostPart.Contains('.') && !hostPart.Contains(':') || hostPart.Contains(':') && hostPart.Split(':')[0].Contains('.'))
                return "https://" + s;
        }
        return s;
    }

    private static bool IsSupportedHttpUrl(string? url)
    {
        try
        {
            string t = (url ?? "").Trim();
            if (Uri.TryCreate(t, UriKind.Absolute, out var u) &&
                (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps ||
                 u.Scheme == Uri.UriSchemeFtp))
                return true;
            // Explorer verb: an existing local file is a valid source.
            if (Uri.TryCreate(t, UriKind.Absolute, out var f) && f.Scheme == Uri.UriSchemeFile)
            {
                try { return File.Exists(f.LocalPath); } catch { return false; }
            }
            return false;
        }
        catch { return false; }
    }

    private static string GetPasteError(string rawBoxText, string normalized)
    {
        if (string.IsNullOrWhiteSpace(rawBoxText))
            return "Paste a link to get started: the box is empty.";
        string t = normalized.Trim();
        string lower = t.ToLowerInvariant();
        if (lower.StartsWith("magnet:", StringComparison.Ordinal))
            return "Magnet links aren't supported: paste a direct http(s) file link instead.";
        if (lower.StartsWith("udp://", StringComparison.Ordinal) || lower.StartsWith("wss://", StringComparison.Ordinal) || lower.StartsWith("ws://", StringComparison.Ordinal) ||
            lower.StartsWith("tracker:", StringComparison.Ordinal) || (lower.Contains("/announce", StringComparison.Ordinal) && !lower.StartsWith("http", StringComparison.Ordinal)))
            return "Torrent tracker links aren't supported: paste a direct http(s) file link instead.";
        if (lower.StartsWith("ftp://", StringComparison.Ordinal) || lower.StartsWith("ftps://", StringComparison.Ordinal))
            return "FTPS (encrypted FTP) isn't supported: use plain ftp:// or http(s) instead.";
        if (lower.StartsWith("file:", StringComparison.Ordinal))
            return "That local file can't be found: check the path and try again.";
        if (lower.StartsWith("data:", StringComparison.Ordinal) || lower.StartsWith("blob:", StringComparison.Ordinal) ||
            lower.StartsWith("javascript:", StringComparison.Ordinal) || lower.StartsWith("about:", StringComparison.Ordinal))
            return "That link type isn't downloadable: paste an http(s) file link.";
        if (t.Contains(' ') || t.Contains('\t') || t.Contains('\n') || t.Contains('\r'))
            return "That doesn't look like a single link: check for extra spaces or line breaks.";
        if (t.Length > 0 && !t.Contains("://", StringComparison.Ordinal))
            return "That doesn't look like a valid http(s) link: check the address and try again.";
        return "That doesn't look like a valid http(s) link: check the address and try again.";
    }

    private void ShowPasteError(string message)
    {
        try
        {
            ProbeBadge.Visibility = Visibility.Visible;
            try { StopProbeAnimation(); } catch { }
            ProbeIcon.Symbol = SymbolRegular.Warning24;
            ProbeText.Text = message;
            YtSignInButton.Visibility = Visibility.Collapsed;
        }
        catch { }
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
        string name = string.IsNullOrWhiteSpace(NameBox.Text) ? DownloadEngine.DeriveName(NormalizePastedUrl(UrlBox.Text)) : NameBox.Text;
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
        string boxRaw = UrlBox.Text ?? "";
        // Always check whatever was pasted/typed: clean it, then validate.
        string url = NormalizePastedUrl(boxRaw);
        bool isValid = IsSupportedHttpUrl(url);

        OkButton.IsEnabled = isValid;
        if (DownloadLaterButton != null) DownloadLaterButton.IsEnabled = isValid;
        if (isValid)
        {
            StartHint.Visibility = Visibility.Collapsed;
        }
        else
        {
            string err = GetPasteError(boxRaw, url);
            StartHint.Text = err;
            StartHint.Visibility = Visibility.Visible;
        }
        // Drop the previous URL's probed size: the verdict below is keystroke-time
        // (size unknown → generic text) and the probe refines it when it lands.
        _probeTotalBytes = -1;
        _probeIsStream = false;
        UpdateDuplicateWarning(string.IsNullOrWhiteSpace(url) ? boxRaw.Trim() : url);

        if (!isValid)
        {
            if (string.IsNullOrWhiteSpace(boxRaw))
            {
                ProbeBadge.Visibility = Visibility.Collapsed;
            }
            else
            {
                ShowPasteError(GetPasteError(boxRaw, url));
            }
            YtSignInButton.Visibility = Visibility.Collapsed;
            if (HlsPanel != null) HlsPanel.Visibility = Visibility.Collapsed;
            _hlsVariants = new List<HlsDownloader.HlsVariantInfo>();
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
        // Form POST replay: a HEAD probe without the body would return garbage
        // (405/HTML), so skip probing and say what happens instead. Only while
        // the box still holds the URL the body was captured for.
        bool formPost = !string.IsNullOrWhiteSpace(_prefillPostData) && !string.IsNullOrWhiteSpace(_postDataUrl) &&
            string.Equals(url, _postDataUrl, StringComparison.Ordinal);
        if (formPost)
        {
            if (YouTubePanel != null) YouTubePanel.Visibility = Visibility.Collapsed;
            if (HlsPanel != null) HlsPanel.Visibility = Visibility.Collapsed;
            _hlsVariants = new List<HlsDownloader.HlsVariantInfo>();
            ClearPlaylist();
            _lastResolved = null;
            ProbeBadge.Visibility = Visibility.Visible;
            ProbeIcon.Symbol = SymbolRegular.Info24;
            ProbeText.Text = "Form data replays at start • single connection" +
                ((!string.IsNullOrWhiteSpace(_prefillProxyHost) && _prefillProxyPort >= 1) ? " • via browser proxy" : "");
        }
        else if (_viewModel.Settings.EnableYouTubeDownloads && YouTubeResolver.IsYoutubeUrl(url))
        {
            if (HlsPanel != null) HlsPanel.Visibility = Visibility.Collapsed;
            _hlsVariants = new List<HlsDownloader.HlsVariantInfo>();
            ProbeYouTubeUrlAsync(url);
        }
        else
        {
            if (YouTubePanel != null) YouTubePanel.Visibility = Visibility.Collapsed;
            ClearPlaylist();
            _lastResolved = null;
            ProbeUrlAsync(url);
        }
    }

    /// <summary>Size-aware duplicate warning (1DM <c>i.sh6</c> fingerprint).
    /// Same URL + same known size: true duplicate. Same URL + different known
    /// sizes: the link was probably refreshed — still warn, but say so so the user
    /// doesn't dismiss a fresh link as a dup. Unknown sizes: legacy generic text.</summary>
    private void UpdateDuplicateWarning(string url)
    {
        var existing = _viewModel.FindByUrl(url);
        if (existing is null)
        {
            DuplicateWarning.Visibility = Visibility.Collapsed;
            return;
        }
        DuplicateWarning.Visibility = Visibility.Visible;
        if (_probeTotalBytes > 0 && existing.TotalBytes > 0 && existing.TotalBytes != _probeTotalBytes)
        {
            DuplicateWarning.Text = $"Same link, but the file size changed (was {DownloadTask.FormatBytes(existing.TotalBytes)}, now {DownloadTask.FormatBytes(_probeTotalBytes)}): the link may have been refreshed.";
        }
        else if (_probeTotalBytes > 0 && existing.TotalBytes > 0)
        {
            DuplicateWarning.Text = $"This URL is already in your download list (size {DownloadTask.FormatBytes(existing.TotalBytes)} matches).";
        }
        else
        {
            DuplicateWarning.Text = "This URL is already in your download list.";
        }
    }

    private void YtSignInButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var win = new YouTubeSignInWindow { Owner = this };
            if (win.ShowDialog() == true)
            {
                YtSignInButton.Visibility = Visibility.Collapsed;
                var url = NormalizePastedUrl(UrlBox.Text);
                if (!string.IsNullOrWhiteSpace(url) && YouTubeResolver.IsYoutubeUrl(url))
                    ProbeYouTubeUrlAsync(url);
            }
        }
        catch { }
    }

    private static bool IsYouTubeSignInRequired(string msg) =>
        msg.IndexOf("Sign in to confirm", StringComparison.OrdinalIgnoreCase) >= 0
        || msg.IndexOf("not a bot", StringComparison.OrdinalIgnoreCase) >= 0
        || msg.IndexOf("cookies-from-browser", StringComparison.OrdinalIgnoreCase) >= 0;

    private async void ProbeYouTubeUrlAsync(string url)
    {
        try { _probeCts?.Cancel(); } catch { }
        try { _probeCts?.Dispose(); } catch { }
        _probeCts = new CancellationTokenSource();
        var ct = _probeCts.Token;

        ProbeBadge.Visibility = Visibility.Visible;
        YtSignInButton.Visibility = Visibility.Collapsed;
        ProbeIcon.Symbol = SymbolRegular.VideoClip24;
        ProbeText.Text = "Resolving YouTube video metadata...";

        try
        {
            var res = await YouTubeResolver.ResolveAsync(url, ct);
            if (ct.IsCancellationRequested || !IsLoaded) return;

            if (res.Items.Count > 0)
            {
                _lastResolved = res;
                _ytQualityOptions = res.QualityOptions;

                if (res.IsPlaylist)
                {
                    PopulatePlaylist(res.Items, res.PlaylistTitle);
                    string playlistName = string.IsNullOrWhiteSpace(res.PlaylistTitle) ? "Playlist" : res.PlaylistTitle.Trim();
                    string cleanPlaylist = DownloadEngine.SanitizeFileName(playlistName);
                    if (string.IsNullOrWhiteSpace(cleanPlaylist)) cleanPlaylist = "YouTube_Playlist";
                    _lastDerivedName = cleanPlaylist + ".mp4";
                    NameBox.Text = _lastDerivedName;
                    ShowYouTubePanel(isPlaylist: true);

                    ProbeIcon.Symbol = SymbolRegular.PlayCircle24;
                    ProbeText.Text = $"YouTube Playlist • {res.Items.Count} videos • {playlistName}";
                    CategoryBox.SelectedIndex = 1; // Video
                    if (YtThumbnail != null) YtThumbnail.Visibility = Visibility.Collapsed;
                    return;
                }

                ClearPlaylist();
                var item = res.Items[0];
                string cleanTitle = DownloadEngine.SanitizeFileName(item.Title);
                if (string.IsNullOrWhiteSpace(cleanTitle)) cleanTitle = "YouTube_Video";
                _lastDerivedName = cleanTitle + ".mp4";
                NameBox.Text = _lastDerivedName;

                ShowYouTubePanel(res.IsPlaylist);

                ProbeIcon.Symbol = SymbolRegular.PlayCircle24;
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
                ProbeIcon.Symbol = SymbolRegular.Warning24;
                if (IsYouTubeSignInRequired(ex.Message))
                {
                    ProbeText.Text = "Sign in required: YouTube needs you to sign in to confirm you're not a bot.";
                    YtSignInButton.Visibility = Visibility.Visible;
                }
                else
                {
                    App.LogException(ex);
                    ProbeText.Text = "Couldn't check that YouTube link: " + UserFriendlyError.For(ex);
                    YtSignInButton.Visibility = Visibility.Collapsed;
                }
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

    internal void ShowYouTubePanel(bool isPlaylist)
    {
        if (YouTubePanel == null) return;
        YouTubePanel.Visibility = Visibility.Visible;
        if (YtPlaylistPanel != null)
            YtPlaylistPanel.Visibility = isPlaylist && _playlistPicks.Count > 0
                ? Visibility.Visible : Visibility.Collapsed;
        if (!isPlaylist)
            ClearPlaylist();
        if (_ytQualityOptions.Count == 0 && _lastResolved?.QualityOptions.Count > 0)
            _ytQualityOptions = _lastResolved.QualityOptions;
        PopulateYtQuality();
    }

    private static List<QualityOption> BuildFallbackQuality() =>
        YouTubeResolver.Tiers.Where(t => t.Height >= 0).Select(t => new QualityOption
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
        FileCategoryIcon.Symbol = cat switch
        {
            DownloadCategory.Video => SymbolRegular.Video24,
            DownloadCategory.Music => SymbolRegular.MusicNote224,
            DownloadCategory.Document => SymbolRegular.Document24,
            DownloadCategory.Compressed => SymbolRegular.FolderZip24,
            DownloadCategory.Program => SymbolRegular.AppGeneric24,
            _ => SymbolRegular.Document24,
        };
    }

    private async void ProbeUrlAsync(string url)
    {
        try { _probeCts?.Cancel(); } catch { }
        try { _probeCts?.Dispose(); } catch { }
        _probeCts = new CancellationTokenSource();
        var ct = _probeCts.Token;

        ProbeBadge.Visibility = Visibility.Visible;
        ProbeIcon.Symbol = SymbolRegular.ArrowSync24;
        ProbeText.Text = "Checking link…";
        _probeTotalBytes = -1;
        _probeIsStream = false;
        // FTP: SIZE probe over a control connection (no download). Anonymous
        // or URL-embedded credentials; failures fall through to the engine.
        if (DownloadEngine.IsFtpUrl(url))
        {
            try
            {
                if (FtpTransport.TryParseUrl(url, out string fhost, out int fport,
                        out string fuser, out string fpass, out string fpath))
                {
                    using var ftpProbe = new FtpTransport(fhost, fport, fuser, fpass);
                    using var ftpCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    ftpCts.CancelAfter(TimeSpan.FromSeconds(8));
                    await ftpProbe.ConnectAsync(ftpCts.Token);
                    long fsize = await ftpProbe.GetSizeAsync(fpath, ftpCts.Token);
                    if (fsize > 0)
                    {
                        _probeTotalBytes = fsize;
                        if (string.Equals(NormalizePastedUrl(UrlBox.Text), url, StringComparison.Ordinal))
                            UpdateDuplicateWarning(url);
                        ProbeIcon.Symbol = SymbolRegular.CheckmarkCircle24;
                        ProbeText.Text = $"{DownloadTask.FormatBytes(fsize)} • FTP download (single connection)";
                        StopProbeAnimation();
                        SetButtonsProbing(false);
                        return;
                    }
                }
            }
            catch { }
        }
        // Local files: size comes from the filesystem, no network probe.
        if (DownloadEngine.IsFileUrl(url))
        {
            try
            {
                string local = new Uri(url).LocalPath;
                if (File.Exists(local))
                {
                    long len = new FileInfo(local).Length;
                    _probeTotalBytes = len;
                    if (string.Equals(NormalizePastedUrl(UrlBox.Text), url, StringComparison.Ordinal))
                        UpdateDuplicateWarning(url);
                    ProbeIcon.Symbol = SymbolRegular.CheckmarkCircle24;
                    ProbeText.Text = $"{DownloadTask.FormatBytes(len)} • Local file (managed copy)";
                    StopProbeAnimation();
                    SetButtonsProbing(false);
                    await TryAutoSyncTitleAsync(url, ct);
                    return;
                }
            }
            catch { }
        }
        StartProbeAnimation();
        SetButtonsProbing(true);

        // Note: embed/player pages are caught via the browser extension (overlay
        // "Resolve in WDM" or auto-captured streams), not by pasting. The engine
        // still resolves page URLs sent by the extension when the download starts.

        try
        {
            var handler = new SocketsHttpHandler
            {
                AllowAutoRedirect = true,
                UseCookies = false,
            };
            // Probe through the captured browser proxy when one rides along
            // (http/https only); otherwise a refused direct connection would
            // misreport a reachable-behind-proxy link as dead.
            if (!string.IsNullOrWhiteSpace(_prefillProxyHost) && _prefillProxyPort >= 1 && _prefillProxyPort <= 65535 &&
                (_prefillProxyType ?? "http").StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    handler.Proxy = new System.Net.WebProxy($"http://{_prefillProxyHost.Trim()}:{_prefillProxyPort}");
                    handler.UseProxy = true;
                }
                catch { }
            }
            // Route the probe (and the HLS playlist fetch below) through the manual proxy when set.
            var proxy = ProxyHelper.BuildProxy(_viewModel.Settings);
            if (proxy is not null)
            {
                handler.Proxy = proxy;
                handler.UseProxy = true;
            }
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

            if (!supportsRanges || !resp.IsSuccessStatusCode)
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

            if (ct.IsCancellationRequested || !IsLoaded)
                return;

            // Late landing from a superseded keystroke must not poison the
            // current URL's size (same guard as the duplicate refresh below).
            if (!string.Equals(NormalizePastedUrl(UrlBox.Text), url, StringComparison.Ordinal))
                return;
            _probeTotalBytes = totalBytes;
            // The probe learned the size after the keystroke handler ran — refresh the
            // duplicate verdict for the URL that was actually probed.
            if (string.Equals(NormalizePastedUrl(UrlBox.Text), url, StringComparison.Ordinal))
                UpdateDuplicateWarning(url);

            string sizeStr = totalBytes > 0 ? DownloadTask.FormatBytes(totalBytes) : "Unknown size";
            bool isHls = resp.Content.Headers.ContentType?.MediaType is string mt
                && (mt.Contains("mpegurl", StringComparison.OrdinalIgnoreCase) || mt.Contains("m3u8", StringComparison.OrdinalIgnoreCase))
                || url.Contains(".m3u8", StringComparison.OrdinalIgnoreCase);
            _probeIsStream = isHls;
            if (isHls)
            {
                ProbeIcon.Symbol = SymbolRegular.CheckmarkCircle24;
                ProbeText.Text = $"{sizeStr} • HLS stream (downloads as one media file)";
                _ = LoadHlsVariantsAsync(url, http, ct);
            }
            else if (supportsRanges)
            {
                ProbeIcon.Symbol = SymbolRegular.CheckmarkCircle24;
                ProbeText.Text = $"{sizeStr} • Multi-threaded resume supported";
                if (HlsPanel != null) HlsPanel.Visibility = Visibility.Collapsed;
            }
            else
            {
                ProbeIcon.Symbol = SymbolRegular.Info24;
                ProbeText.Text = $"{sizeStr} • Single-thread download (Server doesn't support resuming)";
                if (HlsPanel != null) HlsPanel.Visibility = Visibility.Collapsed;
            }

            await TryAutoSyncTitleAsync(url, ct);
        }
        catch (OperationCanceledException)
        {
            // Cancelled due to new typing
        }
        catch
        {
            if (!ct.IsCancellationRequested)
            {
                ProbeIcon.Symbol = SymbolRegular.CheckmarkCircle24;
                ProbeText.Text = "URL ready for download";
            }
        }
        finally
        {
            StopProbeAnimation();
            SetButtonsProbing(false);
        }
    }

    private void StartProbeAnimation()
    {
        try
        {
            var anim = new System.Windows.Media.Animation.DoubleAnimation(0, 360, TimeSpan.FromSeconds(0.9))
            {
                RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever
            };
            ProbeSpin.BeginAnimation(System.Windows.Media.RotateTransform.AngleProperty, anim);
        }
        catch { }
    }

    private void StopProbeAnimation()
    {
        try { ProbeSpin.BeginAnimation(System.Windows.Media.RotateTransform.AngleProperty, null); ProbeSpin.Angle = 0; } catch { }
    }

    private void SetButtonsProbing(bool probing)
    {
        try
        {
            Dispatcher.Invoke(() =>
            {
                bool urlOk = IsSupportedHttpUrl(NormalizePastedUrl(UrlBox.Text));
                OkButton.IsEnabled = !probing && urlOk;
                if (DownloadLaterButton != null) DownloadLaterButton.IsEnabled = !probing && urlOk;
                OkButton.Content = probing ? "Checking…" : "Start download";
                if (DownloadLaterButton != null) DownloadLaterButton.Content = probing ? "Checking…" : "Download later";
            });
        }
        catch { }
    }

    /// <summary>HLS rendition picker (gap 4): lists master-playlist variants so
    /// the user can choose e.g. 720p instead of best-only. "Best available"
    /// (index 0) keeps today's behavior; panels hide on miss/single variant.</summary>
    private static bool IsGenericName(string name) =>
        string.IsNullOrWhiteSpace(name)
        || FileNameHelper.IsGenericStem(Path.GetFileNameWithoutExtension(name.Trim()))
        || name.Trim().StartsWith("download_", StringComparison.OrdinalIgnoreCase)
        || name.Trim().EndsWith(".bin", StringComparison.OrdinalIgnoreCase);

    /// <summary>Automatic title sync after probing: only when the name is still
    /// generic (never touches user edits or real filenames) and a source page is known.</summary>
    private async Task TryAutoSyncTitleAsync(string url, CancellationToken ct)
    {
        if (!_viewModel.Settings.EnableMediaFetching
            || !_viewModel.Settings.EnableTitleSync || ct.IsCancellationRequested)
            return;
        string current = NameBox.Text.Trim();
        // NEVER overwrite a real, specific filename with a webpage title!
        if (!IsGenericName(current))
            return;
        string? synced = await FetchSyncedTitleAsync(ct).ConfigureAwait(true);
        if (ct.IsCancellationRequested || string.IsNullOrWhiteSpace(synced))
            return;
        string live = NameBox.Text.Trim();
        if (!string.IsNullOrWhiteSpace(live) && !IsGenericName(live))
            return; // user typed meanwhile
        if (ApplySyncedTitle(synced))
            ProbeText.Text += " • Title synced from the page (toggle in Settings)";
    }

    private async void SyncTitle_Click(object sender, RoutedEventArgs e)
    {
        string? synced;
        try
        {
            SyncTitleButton.IsEnabled = false;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            synced = await FetchSyncedTitleAsync(timeout.Token).ConfigureAwait(true);
        }
        catch
        {
            synced = null;
        }
        finally
        {
            SyncTitleButton.IsEnabled = true;
        }
        if (string.IsNullOrWhiteSpace(synced))
        {
            ProbeIcon.Symbol = SymbolRegular.Warning24;
            ProbeText.Text = string.IsNullOrWhiteSpace(_prefillReferer)
                ? "No source page: title sync needs a browser capture"
                : "Could not sync a title from the page";
            return;
        }
        if (ApplySyncedTitle(synced))
        {
            ProbeIcon.Symbol = SymbolRegular.CheckmarkCircle24;
            ProbeText.Text = "Title synced from the page";
        }
    }

    private async Task<string?> FetchSyncedTitleAsync(CancellationToken ct)
    {
        if (ct.IsCancellationRequested)
            return null;
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in ParseHeaders())
            headers[kv.Key] = kv.Value;
        if (!string.IsNullOrWhiteSpace(_prefillReferer) && !headers.ContainsKey("Referer"))
            headers["Referer"] = _prefillReferer;
        if (_prefillHeaders is not null)
        {
            foreach (var kv in _prefillHeaders)
                headers.TryAdd(kv.Key, kv.Value);
        }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        using var http = Services.TitleSync.TitleFetcher.CreateClient();
        try
        {
            return await Services.TitleSync.TitleFetcher.TryFetchTitleAsync(
                _prefillReferer, _prefillReferer, headers, http, timeout.Token).ConfigureAwait(true);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Applies a synced title keeping the current extension. Returns false
    /// when the result is unusable (still generic).</summary>
    private bool ApplySyncedTitle(string title)
    {
        string current = NameBox.Text.Trim();
        string ext = Path.GetExtension(string.IsNullOrWhiteSpace(current) ? _lastDerivedName : current);
        string cleaned = FileNameHelper.CleanPageTitle(title);
        if (string.IsNullOrWhiteSpace(cleaned))
            return false;
        string synced = DownloadEngine.SanitizeFileName(cleaned + ext, referer: _prefillReferer);
        if (string.IsNullOrWhiteSpace(synced) || IsGenericName(synced))
            return false;
        _lastDerivedName = synced;
        NameBox.Text = synced;
        return true;
    }

    private void CategoryBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => ApplyRouting();

    // ── HLS variant picker ─────────────────────────────────────────────
    private async Task LoadHlsVariantsAsync(string url, HttpClient http, CancellationToken ct)
    {
        try
        {
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in ParseHeaders())
                headers[kv.Key] = kv.Value;
            if (!string.IsNullOrWhiteSpace(_prefillReferer) && !headers.ContainsKey("Referer"))
                headers["Referer"] = _prefillReferer;
            if (_prefillHeaders is not null)
            {
                foreach (var kv in _prefillHeaders)
                    headers.TryAdd(kv.Key, kv.Value);
            }

            var variants = await HlsDownloader.ParseMasterVariantsAsync(http, url, _prefillReferer, headers, ct);
            if (ct.IsCancellationRequested || !IsLoaded || variants.Count == 0)
                return;

            _hlsVariants = variants;
            _originalHlsUrl = url;
            _suppressHlsSelection = true;
            HlsQualityBox.ItemsSource = variants.Select(v => v.Label).ToList();
            HlsQualityBox.SelectedIndex = variants.Count - 1; // best quality
            _suppressHlsSelection = false;
            HlsPanel.Visibility = Visibility.Visible;
        }
        catch
        {
            // Not a master playlist or fetch failed — no quality picker needed.
        }
    }

    private void HlsQualityBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressHlsSelection || _hlsVariants.Count == 0)
            return;
        int idx = HlsQualityBox.SelectedIndex;
        if (idx < 0 || idx >= _hlsVariants.Count)
            return;
        string selected = _hlsVariants[idx].VariantUrl;
        if (!string.IsNullOrWhiteSpace(selected) && UrlBox is not null)
        {
            UrlBox.Text = selected;
        }
    }

    private void BrowseClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            InitialDirectory = FolderBox.Text,
        };
        if (dialog.ShowDialog() == true)
            FolderBox.Text = dialog.FolderName;
    }

    /// <summary>Shared YouTube format/extra-arg computation for single and
    /// playlist-batch tasks. Always single-item mode: every queue row is
    /// exactly one video.</summary>
    private void BuildYouTubeArgs(out string? formatArg, out List<string> extraArgs)
    {
        extraArgs = new List<string>();
        formatArg = null;
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
        extraArgs.Add("--no-playlist");
    }

    private string AudioExtension()
    {
        string fmt = YtAudioFormatBox?.SelectedItem is ComboBoxItem ai && ai.Tag is string at && at != "best" ? at : "mp3";
        return fmt == "opus" ? ".opus" : "." + fmt;
    }

    private void OkClick(object sender, RoutedEventArgs e) => CreateAndAddTask(startImmediately: true);

    private void DownloadLaterClick(object sender, RoutedEventArgs e) => CreateAndAddTask(startImmediately: false);

    private void CreateAndAddTask(bool startImmediately)
    {
        string url = NormalizePastedUrl(UrlBox.Text);
        if (!IsSupportedHttpUrl(url))
        {
            string msg = GetPasteError(UrlBox.Text ?? "", url);
            ShowPasteError(msg);
            StartHint.Text = msg;
            StartHint.Visibility = Visibility.Visible;
            System.Windows.MessageBox.Show(this, msg, "Invalid URL", System.Windows.MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // HLS rendition picker: a non-default selection rewrites the master URL
        // to the chosen variant media playlist; the engine then downloads it
        // with the normal HLS path (variant is already a media playlist).
        if (HlsPanel != null && HlsPanel.Visibility == Visibility.Visible &&
            HlsQualityBox != null && HlsQualityBox.SelectedIndex > 0 &&
            HlsQualityBox.SelectedIndex - 1 < _hlsVariants.Count)
        {
            url = _hlsVariants[HlsQualityBox.SelectedIndex - 1].VariantUrl;
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

        string saveFolder = string.IsNullOrWhiteSpace(FolderBox.Text) ? DownloadTask.DefaultSaveFolder : FolderBox.Text;

        // Playlist batch: one queue row per checked video. Each row is a
        // single video download (own title, own progress) so one bad item
        // can no longer fail the whole playlist.
        bool isPlaylistBatch = _viewModel.Settings.EnableYouTubeDownloads
            && YouTubeResolver.IsYoutubeUrl(url)
            && _lastResolved?.IsPlaylist == true
            && YtPlaylistPanel?.Visibility == Visibility.Visible
            && _playlistPicks.Count > 0;
        if (isPlaylistBatch)
        {
            var picked = _playlistPicks.Where(p => p.IsSelected).Select(p => p.Media).ToList();
            if (picked.Count == 0)
            {
                const string msg = "Select at least one video from the playlist to download.";
                StartHint.Text = msg;
                StartHint.Visibility = Visibility.Visible;
                System.Windows.MessageBox.Show(this, msg, "No videos selected",
                    System.Windows.MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            BuildYouTubeArgs(out string? batchFormat, out List<string> batchExtra);
            bool batchAudio = IsAudioOnly;
            string batchExt = batchAudio ? AudioExtension() : ".mp4";
            DownloadCategory batchCat;
            if (CategoryBox.SelectedItem is ComboBoxItem catItem && catItem.Tag is string catTag
                && catTag != "Auto" && Enum.TryParse<DownloadCategory>(catTag, out var parsedCat))
                batchCat = parsedCat;
            else
                batchCat = batchAudio ? DownloadCategory.Music : DownloadCategory.Video;

            var batchHeaders = ParseHeaders();
            var batch = new List<DownloadTask>();
            foreach (var media in picked)
            {
                string stem = DownloadEngine.SanitizeFileName(media.Title);
                if (string.IsNullOrWhiteSpace(stem))
                    stem = $"YouTube_{media.Id}";
                string candidate = stem + batchExt;
                int copy = 1;
                while (_viewModel.IsDuplicateFile(candidate, saveFolder)
                    || batch.Any(t => string.Equals(t.FileName, candidate, StringComparison.OrdinalIgnoreCase)))
                {
                    copy++;
                    candidate = $"{stem} ({copy}){batchExt}";
                }
                batch.Add(new DownloadTask()
                {
                    Url = media.Url,
                    Referer = _prefillReferer,
                    ProxyHost = _prefillProxyHost,
                    ProxyPort = _prefillProxyPort,
                    ProxyType = _prefillProxyType,
                    TotalBytes = -1,
                    Headers = new Dictionary<string, string>(batchHeaders, StringComparer.OrdinalIgnoreCase),
                    SaveFolder = saveFolder,
                    FileName = candidate,
                    ChunkCount = Math.Max(0, chunks),
                    SpeedLimitKbps = speedLimit,
                    Category = batchCat,
                    IsYouTube = true,
                    YouTubeFormatArg = batchFormat,
                    YouTubeExtraArgs = batchExtra.Count > 0 ? string.Join("\n", batchExtra) : null,
                    Status = startImmediately ? TaskStatus.Queued : TaskStatus.Paused,
                });
            }

            _viewModel.AddTasks(batch, showDialogForFirst: startImmediately);
            Close();
            return;
        }

        if (_viewModel.IsDuplicateFile(finalFileName, saveFolder))
        {
            string numberedFileName = _viewModel.GetNumberedFileName(finalFileName, saveFolder);
            var dupDialog = new DuplicateDownloadDialog(url, finalFileName, numberedFileName);
            if (!App.IsTestMode)
            {
                dupDialog.Owner = this;
            }

            bool? dupResult = dupDialog.ShowDialog();
            if (dupResult == true)
            {
                if (dupDialog.SelectedAction == DuplicateAction.RenameAndDownload)
                {
                    finalFileName = dupDialog.NumberedFileName;
                    NameBox.Text = finalFileName;
                }
                else if (dupDialog.SelectedAction == DuplicateAction.Overwrite)
                {
                    finalFileName = dupDialog.OriginalFileName;
                    NameBox.Text = finalFileName;
                }
                else
                {
                    return;
                }
            }
            else
            {
                // User cancelled or closed dialog
                return;
            }
        }

        var mirrors = ParseMirrors();
        var headers = ParseHeaders();

        bool isYouTube = _viewModel.Settings.EnableYouTubeDownloads && YouTubeResolver.IsYoutubeUrl(url);
        string? formatArg = null;
        var extraArgs = new List<string>();
        if (isYouTube)
            BuildYouTubeArgs(out formatArg, out extraArgs);

        var task = new DownloadTask()
        {
            Url = url,
            Referer = _prefillReferer,
            // Form POST replay + proxy mirror, guarded to the exact captured
            // URL (HLS rewrites and retyped URLs drop the body; the proxy is
            // browser-level and survives retyping).
            PostData = !string.IsNullOrWhiteSpace(_prefillPostData) && !string.IsNullOrWhiteSpace(_postDataUrl) &&
                string.Equals(NormalizePastedUrl(url), _postDataUrl, StringComparison.Ordinal)
                ? _prefillPostData : null,
            PostContentType = !string.IsNullOrWhiteSpace(_prefillPostData) && !string.IsNullOrWhiteSpace(_postDataUrl) &&
                string.Equals(NormalizePastedUrl(url), _postDataUrl, StringComparison.Ordinal)
                ? _prefillPostContentType : null,
            ProxyHost = _prefillProxyHost,
            ProxyPort = _prefillProxyPort,
            ProxyType = _prefillProxyType,
            FullSessionReplay = _prefillFullSession,
            // Seed the pre-start size from the dialog probe (e.g. 170 MB shows
            // in the queue immediately). Streams excluded: playlist bytes are
            // not media bytes. The engine re-probes authoritatively at start
            // and overwrites this either way.
            TotalBytes = _probeTotalBytes > 0 && !_probeIsStream &&
                string.Equals(NormalizePastedUrl(UrlBox.Text), NormalizePastedUrl(url), StringComparison.Ordinal)
                ? _probeTotalBytes : -1,
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
            Status = startImmediately ? TaskStatus.Queued : TaskStatus.Paused,
        };

        _viewModel.AddTask(task);
        // Modeless window (shown via Show(), not ShowDialog()): setting
        // DialogResult here throws InvalidOperationException and crashes the
        // app — just close. No caller reads a dialog result from this window.
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
            if (DownloadEngine.IsHttpUrl(line))
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
        // See note in CreateAndAddTask: modeless window, no DialogResult.
        Close();
    }

    // ── Playlist picker ──────────────────────────────────────────────
    private void PopulatePlaylist(List<MediaItem> items, string? playlistTitle)
    {
        foreach (var old in _playlistPicks)
            old.PropertyChanged -= PlaylistPick_Changed;
        _playlistPicks = items.Select((m, i) =>
        {
            string dur = m.Duration.HasValue ? $" ({m.Duration.Value:mm\\:ss})" : "";
            var pick = new PlaylistPick
            {
                Media = m,
                IsSelected = true,
                Display = $"{i + 1}. {m.Title}{dur}",
            };
            pick.PropertyChanged += PlaylistPick_Changed;
            return pick;
        }).ToList();
        if (YtPlaylistBox != null)
            YtPlaylistBox.ItemsSource = _playlistPicks;
        UpdatePlaylistCount(playlistTitle);
    }

    private void ClearPlaylist()
    {
        foreach (var old in _playlistPicks)
            old.PropertyChanged -= PlaylistPick_Changed;
        _playlistPicks = new List<PlaylistPick>();
        if (YtPlaylistBox != null)
            YtPlaylistBox.ItemsSource = null;
    }

    private void PlaylistPick_Changed(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PlaylistPick.IsSelected))
            UpdatePlaylistCount(_lastResolved?.PlaylistTitle);
    }

    private void UpdatePlaylistCount(string? playlistTitle)
    {
        if (YtPlaylistCount == null)
            return;
        int selected = _playlistPicks.Count(p => p.IsSelected);
        string name = string.IsNullOrWhiteSpace(playlistTitle) ? "Playlist" : playlistTitle.Trim();
        YtPlaylistCount.Text = $"{name} • {selected} of {_playlistPicks.Count} selected";
    }

    private void YtSelectAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var p in _playlistPicks)
            p.IsSelected = true;
        UpdatePlaylistCount(_lastResolved?.PlaylistTitle);
    }

    private void YtSelectNone_Click(object sender, RoutedEventArgs e)
    {
        foreach (var p in _playlistPicks)
            p.IsSelected = false;
        UpdatePlaylistCount(_lastResolved?.PlaylistTitle);
    }
}

/// <summary>One row in the playlist picker. Checked by default; the dialog
/// queues one download per checked row.</summary>
internal sealed class PlaylistPick : INotifyPropertyChanged
{
    public MediaItem Media { get; init; } = new MediaItem();
    public string Display { get; init; } = "";
    private bool _isSelected = true;
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value)
                return;
            _isSelected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
        }
    }
    public event PropertyChangedEventHandler? PropertyChanged;
}
