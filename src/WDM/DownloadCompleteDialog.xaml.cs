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

    private void Window_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ButtonState == System.Windows.Input.MouseButtonState.Pressed)
        {
            DragMove();
        }
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

    private static bool IsRiskyExecutable(string path)
    {
        string ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
        return ext is ".exe" or ".msi" or ".bat" or ".cmd" or ".ps1" or ".vbs" or ".vbe"
            or ".js" or ".jse" or ".wsf" or ".wsh" or ".lnk" or ".scr" or ".com"
            or ".pif" or ".reg" or ".jar" or ".msc" or ".hta";
    }
}
