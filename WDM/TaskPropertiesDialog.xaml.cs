using System.Diagnostics;
using System.IO;
using System.Windows;
using WDM.Models;

namespace WDM;

public partial class TaskPropertiesDialog : Window
{
    private readonly DownloadTask _task;

    public TaskPropertiesDialog(DownloadTask task)
    {
        InitializeComponent();
        _task = task;

        FileNameText.Text = task.FileName;
        StatusText.Text = task.StatusText;
        StatusText.Foreground = new System.Windows.Media.SolidColorBrush(
            (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(
                ViewModels.TaskStatusToColorConverter.ColorHex(task.Status)));
        UrlText.Text = task.Url;
        FolderText.Text = task.SaveFolder;
        SizeText.Text = task.SizeText;
        ProgressBar.Value = task.Progress;
        PercentText.Text = $"{task.Progress}%";
        SpeedText.Text = task.SpeedText;
        ChunksText.Text = $"{task.ChunkCount} threads";
        AddedText.Text = task.AddedAt.ToString("yyyy-MM-dd HH:mm");
        CategoryText.Text = task.Category.ToString();
        PriorityText.Text = task.Priority.ToString();
        ChecksumText.Text = string.IsNullOrWhiteSpace(task.Checksum) ? "Not computed" : task.Checksum;
    }

    private void CopyUrlClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(_task.Url);
        }
        catch
        {
            // Clipboard lock fallback
        }
    }

    private void FolderClick(object sender, RoutedEventArgs e)
    {
        string path = _task.FullPath;
        if (File.Exists(path))
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
        else if (Directory.Exists(_task.SaveFolder))
            Process.Start(new ProcessStartInfo(_task.SaveFolder) { UseShellExecute = true });
    }
}
