using System.Diagnostics;
using System.IO;
using System.Windows;
using WDM.Models;

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
                MessageBox.Show(this, $"Could not open file:\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
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
            MessageBox.Show(this, $"Could not open folder:\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void CloseClick(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
