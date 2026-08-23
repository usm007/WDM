using System.Windows;
using WDM.Models;

namespace WDM;

public partial class RefreshLinkDialog : Window
{
    private readonly DownloadTask _task;

    public string NewUrl { get; private set; } = "";

    public RefreshLinkDialog(DownloadTask task)
    {
        InitializeComponent();
        _task = task;

        FileNameText.Text = task.FileName;
        ProgressText.Text = $"{task.SizeText} downloaded · {task.Progress}% ({task.SpeedText})";
        UrlBox.Text = task.Url;
        UrlBox.Focus();
        UrlBox.SelectAll();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        WDM.Services.ThemeService.ApplyTitleBar(this);
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