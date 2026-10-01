using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using WDM.Models;
using WDM.Services;

namespace WDM;

public partial class DownloadCompleteDialog : Window
{
    public DownloadTask Task { get; }

    public DownloadCompleteDialog(DownloadTask task)
    {
        InitializeComponent();
        Task = task;

        FileTitleText.Text = task.DisplayFileName;
        UrlBox.Text = task.Url;
        PathBox.Text = task.FullPath;
        SizeText.Text = task.SizeText;
        DateText.Text = task.CompletedAt?.ToString("g") ?? DateTime.Now.ToString("g");

        FileChipName.Text = task.DisplayFileName;
        FileChip.ToolTip = $"Drag \"{task.DisplayFileName}\" to a folder to copy it there";
        if (!File.Exists(task.FullPath))
            MarkFileMissing();
    }

    private void MarkFileMissing()
    {
        FileChip.IsEnabled = false;
        FileChip.Cursor = Cursors.Arrow;
        FileChip.Opacity = 0.45;
        FileChip.ToolTip = "The downloaded file could not be found.";
        if (TryFindResource("Brush.TextDim") is System.Windows.Media.Brush muted)
        {
            FileChipName.Foreground = muted;
            FileChipIcon.Foreground = muted;
        }
    }

    private void FileChip_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (!FileChip.IsEnabled)
            return;

        string path = Task.FullPath;
        if (!File.Exists(path))
        {
            MarkFileMissing();
            return;
        }

        try
        {
            var data = new DataObject();
            data.SetData(DataFormats.FileDrop, new[] { path });
            var effect = DragDrop.DoDragDrop(this, data,
                DragDropEffects.Copy | DragDropEffects.Move | DragDropEffects.Link);

            // Drop landed somewhere: the hand-off is done, so close the popup.
            // Cancelling (Esc / dropping nowhere) leaves it open.
            if (effect != DragDropEffects.None)
                Close();
        }
        catch (Exception ex)
        {
            App.LogException(ex);
        }
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        WDM.Services.ThemeService.ApplyTitleBar(this);
    }

    private void CopyUrl_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(UrlBox.Text);
        }
        catch
        {
            // Clipboard protection
        }
    }

    private void CopyPath_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(PathBox.Text);
        }
        catch
        {
            // Clipboard protection
        }
    }

    private void OpenFile_Click(object sender, RoutedEventArgs e)
    {
        string path = Task.FullPath;
        if (File.Exists(path))
        {
            if (IsRiskyExecutable(path))
            {
                var answer = MessageBox.Show(this,
                    $"\"{Task.DisplayFileName}\" is an executable downloaded from the internet.\n\nOnly open it if you trust the source.\n\nOpen it now?",
                    "Security warning", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (answer != MessageBoxResult.Yes)
                    return;
            }
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = path,
                    UseShellExecute = true,
                });
                Close();
            }
            catch (Exception ex)
            {
                App.LogException(ex);
                ErrorDialogs.ShowError(this, "Couldn't open file", "The file couldn't be opened. It may have been moved or deleted.");
            }
        }
        else
        {
            MessageBox.Show(this, "The downloaded file could not be found.", "File Not Found", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        string path = Task.FullPath;
        string folder = Task.SaveFolder;
        try
        {
            if (File.Exists(path))
            {
                Process.Start("explorer.exe", $"/select,\"{path}\"");
            }
            else if (Directory.Exists(folder))
            {
                Process.Start("explorer.exe", $"\"{folder}\"");
            }
            else
            {
                MessageBox.Show(this, "The save folder does not exist.", "Folder Not Found", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            Close();
        }
        catch (Exception ex)
        {
            App.LogException(ex);
            ErrorDialogs.ShowError(this, "Couldn't open folder", "The folder couldn't be opened. It may have been moved or deleted.");
        }
    }

    private void CloseClick(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private static bool IsRiskyExecutable(string path)
    {
        string ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
        return ext is ".exe" or ".msi" or ".bat" or ".cmd" or ".ps1" or ".vbs" or ".vbe"
            or ".js" or ".jse" or ".wsf" or ".wsh" or ".lnk" or ".scr" or ".com"
            or ".pif" or ".reg" or ".jar" or ".msc" or ".hta";
    }
}
