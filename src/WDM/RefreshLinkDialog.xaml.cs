using System.Diagnostics;
using System.Windows;
using WDM.Models;

namespace WDM;

public partial class RefreshLinkDialog : Window
{
    private readonly DownloadTask _task;

    public string NewUrl { get; private set; } = "";
    public Dictionary<string, string>? CapturedHeaders { get; private set; }

    public RefreshLinkDialog(DownloadTask task)
    {
        InitializeComponent();
        _task = task;

        FileNameText.Text = task.FileName;
        ProgressText.Text = $"{task.SizeText} downloaded · {task.Progress}% ({task.SpeedText})";
        UrlBox.Text = task.Url;
        UrlBox.Focus();
        UrlBox.SelectAll();

        string pageUrl = GetTargetPageUrl();
        if (!string.IsNullOrWhiteSpace(pageUrl))
        {
            OpenSourcePageBtn.Visibility = Visibility.Visible;
        }

        Loaded += (_, _) => AutoOpenBrowser();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        WDM.Services.ThemeService.ApplyTitleBar(this);
    }

    private string GetTargetPageUrl()
    {
        if (!string.IsNullOrWhiteSpace(_task.Referer) && Uri.TryCreate(_task.Referer, UriKind.Absolute, out _))
            return _task.Referer;
        if (!string.IsNullOrWhiteSpace(_task.Url) && Uri.TryCreate(_task.Url, UriKind.Absolute, out _))
            return _task.Url;
        return "";
    }

    private void AutoOpenBrowser()
    {
        string target = GetTargetPageUrl();
        if (!string.IsNullOrWhiteSpace(target))
        {
            LaunchUrl(target);
        }
        else
        {
            BannerTitle.Text = "Waiting for new link";
            BannerDescription.Text = "Enter the new download URL below or trigger the download again in your browser to resume.";
        }
    }

    private void LaunchUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
        }
        catch { }
    }

    private void OpenSourcePageClick(object sender, RoutedEventArgs e)
    {
        string target = GetTargetPageUrl();
        if (!string.IsNullOrWhiteSpace(target))
        {
            LaunchUrl(target);
        }
    }

    /// <summary>
    /// Invoked when WDM's browser extension intercepts a refreshed download request.
    /// </summary>
    public void OnLinkCaptured(string url, Dictionary<string, string> headers)
    {
        if (string.IsNullOrWhiteSpace(url)) return;

        NewUrl = url;
        CapturedHeaders = headers;
        UrlBox.Text = url;

        BannerIcon.Text = "\uF012C"; // Check icon
        BannerIcon.Foreground = (System.Windows.Media.Brush)(TryFindResource("Brush.StatusComplete") ?? System.Windows.Media.Brushes.Green);
        BannerTitle.Text = "Renewed link captured!";
        BannerDescription.Text = "Resuming download with renewed session parameters...";

        DialogResult = true;
        Close();
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

        NewUrl = url;
        DialogResult = true;
        Close();
    }

    private void CancelClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}