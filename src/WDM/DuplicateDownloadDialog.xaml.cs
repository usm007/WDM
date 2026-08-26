using System;
using System.Windows;

namespace WDM;

public enum DuplicateAction
{
    RenameAndDownload,
    Skip
}

public partial class DuplicateDownloadDialog : Window
{
    public DuplicateAction SelectedAction { get; private set; } = DuplicateAction.Skip;
    public string OriginalFileName { get; }
    public string NumberedFileName { get; }

    public DuplicateDownloadDialog(string? url, string originalFileName, string numberedFileName)
    {
        InitializeComponent();
        OriginalFileName = originalFileName;
        NumberedFileName = numberedFileName;

        OriginalFileText.Text = originalFileName;
        NumberedFileText.Text = numberedFileName;
        DownloadBtn.Content = $"Download as {numberedFileName}";

        Loaded += (_, _) => DownloadBtn.Focus();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        Services.ThemeService.ApplyTitleBar(this);
    }

    private void DownloadNumbered_Click(object sender, RoutedEventArgs e)
    {
        SelectedAction = DuplicateAction.RenameAndDownload;
        DialogResult = true;
        Close();
    }

    private void Skip_Click(object sender, RoutedEventArgs e)
    {
        SelectedAction = DuplicateAction.Skip;
        DialogResult = false;
        Close();
    }
}
