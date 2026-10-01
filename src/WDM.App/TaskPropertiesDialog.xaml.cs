using System.Diagnostics;
using System.IO;
using System.Windows;
using WDM.Models;
using WDM.Services;
using WDM.ViewModels;

namespace WDM;

public partial class TaskPropertiesDialog : Wpf.Ui.Controls.FluentWindow
{
    private readonly DownloadTask _task;
    private readonly MainViewModel? _viewModel;

    public TaskPropertiesDialog(DownloadTask task, MainViewModel? viewModel = null)
    {
        InitializeComponent();
        _task = task;
        _viewModel = viewModel;

        FileNameText.Text = task.FileName;
        StatusText.Text = task.StatusText;
        try
        {
            var converted = System.Windows.Media.ColorConverter.ConvertFromString(
                ViewModels.TaskStatusToColorConverter.ColorHex(task.Status));
            if (converted is System.Windows.Media.Color color)
                StatusText.Foreground = new System.Windows.Media.SolidColorBrush(color);
        }
        catch { }
        UrlText.Text = task.Url;
        RefererText.Text = task.Referer ?? "";
        FolderText.Text = task.SaveFolder;
        SizeText.Text = task.SizeText;
        ProgressBar.Value = task.Progress;
        PercentText.Text = $"{task.Progress}%";
        SpeedText.Text = task.SpeedText;
        ChunksText.Text = task.ChunkCount > 0 ? $"{task.ChunkCount} threads" : "Auto";
        ResumeCapText.Text = task.ResumeCapabilityText;
        AddedText.Text = task.AddedAt.ToString("yyyy-MM-dd HH:mm");
        CategoryText.Text = task.Category.ToString();
        PriorityText.Text = task.Priority.ToString();
        ChecksumText.Text = string.IsNullOrWhiteSpace(task.Checksum) ? "Not computed" : task.Checksum;

        if (task.Headers is { Count: > 0 })
        {
            HeadersText.Text = string.Join("\n", task.Headers.Select(kv => $"{kv.Key}: {kv.Value}"));
        }
        else
        {
            HeadersText.Text = "";
        }

        // Editing a finished download is meaningless; screenshots pass no
        // viewmodel, which also forces read-only mode.
        bool editable = _viewModel is not null && task.Status != TaskStatus.Completed;
        UrlText.IsReadOnly = !editable;
        RefererText.IsReadOnly = !editable;
        HeadersText.IsReadOnly = !editable;
        SaveButton.Visibility = editable ? Visibility.Visible : Visibility.Collapsed;

        if (task.Status == TaskStatus.Completed)
        {
            ProgressBar.Visibility = Visibility.Collapsed;
            SpeedText.Text = "-";
            PercentText.Text = task.CompletedAt.HasValue
                ? $"Completed {task.CompletedAt.Value:yyyy-MM-dd HH:mm}"
                : "Completed";
        }
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        WDM.Services.ThemeService.ApplyTitleBar(this);
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

    /// <summary>Mid-download edit (gap 9): rewrites URL/Referer/headers and
    /// resumes from existing progress via MainViewModel.ApplyTaskEditsAsync.
    /// Active tasks are paused first; paused/failed tasks stay paused.</summary>
    private async void SaveClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel is null)
            return;
        string url = UrlText.Text.Trim();
        if (!DownloadEngine.IsHttpUrl(url))
        {
            string msg = DownloadEngine.IsFtpUrl(url)
                ? "FTP downloads aren't supported yet: paste an http(s) link instead."
                : "Enter a valid http(s) URL.";
            MessageBox.Show(this, msg, "Invalid URL", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        string? referer = string.IsNullOrWhiteSpace(RefererText.Text) ? null : RefererText.Text.Trim();
        var headers = ParseHeaderLines(HeadersText.Text);
        bool sameUrl = string.Equals(url, _task.Url, StringComparison.Ordinal);
        bool sameReferer = string.Equals(referer ?? "", _task.Referer ?? "", StringComparison.Ordinal);
        bool sameHeaders = headers.Count == _task.Headers.Count &&
            headers.All(kv => _task.Headers.TryGetValue(kv.Key, out var v) && v == kv.Value);
        if (sameUrl && sameReferer && sameHeaders)
        {
            MessageBox.Show(this, "No changes to save.", "Properties", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        SaveButton.IsEnabled = false;
        try
        {
            bool ok = await _viewModel.ApplyTaskEditsAsync(_task, url, referer, headers);
            if (!ok)
            {
                MessageBox.Show(this, "The download is still stopping: try Save again in a moment.", "Properties", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            UrlText.Text = _task.Url;
            Close();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Could not apply changes: " + ex.Message, "Properties", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            SaveButton.IsEnabled = true;
        }
    }

    private static Dictionary<string, string> ParseHeaderLines(string? text)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(text))
            return headers;
        foreach (string line in text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            int colon = line.IndexOf(':');
            if (colon < 1)
                continue;
            string key = line.Substring(0, colon).Trim();
            string value = line.Substring(colon + 1).Trim();
            if (!string.IsNullOrWhiteSpace(key) && !string.IsNullOrWhiteSpace(value))
                headers[key] = value;
        }
        return headers;
    }

    private void FolderClick(object sender, RoutedEventArgs e)
    {
        // tasks.json is hand-editable: never interpolate a quote-containing
        // persisted path into a command line (argument breakout).
        string path = _task.FullPath;
        if (path.Contains('"') || (_task.SaveFolder?.Contains('"') ?? false))
            return;
        try
        {
            if (File.Exists(path))
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
            else if (Directory.Exists(_task.SaveFolder))
                Process.Start(new ProcessStartInfo(_task.SaveFolder) { UseShellExecute = true });
        }
        catch { }
    }

    private void CloseClick(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
