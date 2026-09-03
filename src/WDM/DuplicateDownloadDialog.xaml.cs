using System;
using System.Windows;

namespace WDM;

public enum DuplicateAction
{
    RenameAndDownload,
    Overwrite,
    Cancel
}

public partial class DuplicateDownloadDialog : Window
{
    public DuplicateAction SelectedAction { get; private set; } = DuplicateAction.Cancel;
    public string OriginalFileName { get; }
    public string NumberedFileName { get; }

    public DuplicateDownloadDialog(string? url, string originalFileName, string numberedFileName)
    {
        InitializeComponent();
        OriginalFileName = originalFileName;
        NumberedFileName = numberedFileName;

        OriginalFileText.Text = originalFileName;
        NumberedFileText.Text = numberedFileName;
        RenameBtnText.Text = $"Rename to {numberedFileName}";
        RenameBtn.ToolTip = $"Download as {numberedFileName}";

        Loaded += (_, _) => RenameBtn.Focus();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        Services.ThemeService.ApplyTitleBar(this);
    }

    private void Window_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ButtonState == System.Windows.Input.MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void RenameAndDownload_Click(object sender, RoutedEventArgs e)
    {
        SelectedAction = DuplicateAction.RenameAndDownload;
        DialogResult = true;
        Close();
    }

    private void Overwrite_Click(object sender, RoutedEventArgs e)
    {
        SelectedAction = DuplicateAction.Overwrite;
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        SelectedAction = DuplicateAction.Cancel;
        DialogResult = false;
        Close();
    }
}
