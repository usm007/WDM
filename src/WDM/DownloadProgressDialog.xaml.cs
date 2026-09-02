using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using WDM.Models;
using WDM.ViewModels;

namespace WDM;

public sealed class ChunkVisualItem : INotifyPropertyChanged
{
    public int Index { get; set; }

    private string _toolTip = "";
    public string ToolTip
    {
        get => _toolTip;
        set
        {
            if (_toolTip != value)
            {
                _toolTip = value;
                OnPropertyChanged();
            }
        }
    }

    private double _widthPercent = 0;
    public double WidthPercent
    {
        get => _widthPercent;
        set
        {
            if (Math.Abs(_widthPercent - value) > 0.01)
            {
                _widthPercent = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(FillGridLength));
                OnPropertyChanged(nameof(RemainingGridLength));
            }
        }
    }

    public GridLength FillGridLength => new GridLength(Math.Clamp(WidthPercent, 0, 100), GridUnitType.Star);
    public GridLength RemainingGridLength => new GridLength(Math.Max(0, 100 - Math.Clamp(WidthPercent, 0, 100)), GridUnitType.Star);

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public partial class DownloadProgressDialog : Window, INotifyPropertyChanged
{
    private readonly MainViewModel _mainViewModel;
    private double[]? _lastChunkProgress;
    public DownloadTask Task { get; }

    public ObservableCollection<ChunkVisualItem> ChunkList { get; } = new();

    public DownloadProgressDialog(DownloadTask task, MainViewModel mainViewModel)
    {
        InitializeComponent();
        Task = task;
        _mainViewModel = mainViewModel;
        DataContext = this;

        Task.PropertyChanged += Task_PropertyChanged;
        _mainViewModel.Engine.ChunkProgressUpdated += Engine_ChunkProgressUpdated;
        UpdateState();
        SetupChunkVisuals();
        ApplyYouTubeMode();
    }

    // ── YouTube-specific "different system" bindings ─────────────────────
    public string YouTubeEngineText => Task.IsYouTube ? "yt-dlp" : ChunkCountText;
    public string YouTubeStatusHint
    {
        get
        {
            if (!Task.IsYouTube) return "";
            if (Task.Status == TaskStatus.Completed) return "Completed via yt-dlp";
            if (Task.Status == TaskStatus.Failed) return Task.Error ?? "Failed";
            if (Task.Progress >= 99 && Task.Status == TaskStatus.Downloading) return "Merging streams via ffmpeg…";
            if (Task.Status == TaskStatus.Downloading) return $"Downloading via yt-dlp — {Task.Progress}%";
            return "YouTube download";
        }
    }
    public GridLength YouTubeProgressFill => new GridLength(Math.Clamp(Task.Progress, 0, 100), GridUnitType.Star);
    public GridLength YouTubeProgressRemaining => new GridLength(Math.Max(0, 100 - Math.Clamp(Task.Progress, 0, 100)), GridUnitType.Star);

    private void ApplyYouTubeMode()
    {
        bool isYt = Task.IsYouTube;
        if (HttpExtraPanel != null) HttpExtraPanel.Visibility = isYt ? Visibility.Collapsed : Visibility.Visible;
        if (YouTubeExtraPanel != null) YouTubeExtraPanel.Visibility = isYt ? Visibility.Visible : Visibility.Collapsed;
        if (isYt && ResumeLabel != null) ResumeLabel.Text = "Engine";
        // For YouTube tasks, the ResumeCapabilityText is set by RunYouTubeSessionAsync to
        // "YouTube — via yt-dlp (single stream)" so the dialog never shows
        // "Checking server support..." (the bug in the screenshot).
        OnPropertyChanged(nameof(YouTubeEngineText));
        OnPropertyChanged(nameof(YouTubeStatusHint));
        OnPropertyChanged(nameof(YouTubeProgressFill));
        OnPropertyChanged(nameof(YouTubeProgressRemaining));
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

    private void CloseClick(object sender, RoutedEventArgs e)
    {
        Close();
    }

    public string ProgressTitleText => $"{Task.Progress}%";
    public string DownloadedDetailText => $"{Task.DownloadedText} ({Task.Progress}%)";
    public string ChunkCountText => Task.ChunkCount > 0 ? $"{Task.ChunkCount} threads" : "Auto";
    public bool CanPause => Task.Status is TaskStatus.Downloading or TaskStatus.Queued;
    public bool CanResume => Task.Status is TaskStatus.Paused or TaskStatus.Failed;

    public bool IsSpeedLimitEnabled
    {
        get => Task.SpeedLimitKbps > 0;
        set
        {
            if (!value)
            {
                Task.SpeedLimitKbps = 0;
                OnPropertyChanged();
            }
            else if (Task.SpeedLimitKbps <= 0)
            {
                Task.SpeedLimitKbps = 500;
                OnPropertyChanged();
            }
        }
    }

    public long TaskSpeedLimit
    {
        get => Task.SpeedLimitKbps;
        set
        {
            Task.SpeedLimitKbps = Math.Max(0, value);
            OnPropertyChanged();
        }
    }

    private bool _closeOnComplete = true;
    public bool CloseOnComplete
    {
        get => _closeOnComplete;
        set
        {
            if (_closeOnComplete != value)
            {
                _closeOnComplete = value;
                OnPropertyChanged();
            }
        }
    }

    private bool _openOnComplete = false;
    public bool OpenOnComplete
    {
        get => _openOnComplete;
        set
        {
            if (_openOnComplete != value)
            {
                _openOnComplete = value;
                OnPropertyChanged();
            }
        }
    }

    private bool _shutdownOnComplete = false;
    public bool ShutdownOnComplete
    {
        get => _shutdownOnComplete;
        set
        {
            if (_shutdownOnComplete != value)
            {
                _shutdownOnComplete = value;
                OnPropertyChanged();
            }
        }
    }

    private bool _completionHandled = false;

    private void SetupChunkVisuals()
    {
        ChunkList.Clear();
        int count = Math.Max(1, Task.ChunkCount);
        for (int i = 0; i < count; i++)
        {
            ChunkList.Add(new ChunkVisualItem
            {
                Index = i + 1,
                ToolTip = $"Thread #{i + 1}",
                WidthPercent = 0
            });
        }
        UpdateChunkVisuals();
    }

    private void Engine_ChunkProgressUpdated(DownloadTask task, double[] progress)
    {
        if (task.Id != Task.Id)
            return;
        Dispatcher.BeginInvoke(() =>
        {
            _lastChunkProgress = progress;
            UpdateChunkVisuals();
        });
    }

    private void UpdateChunkVisuals()
    {
        if (ChunkList.Count == 0) return;

        // Use the real per-chunk progress emitted by the engine when available,
        // aggregating the (potentially many) dynamic segments onto the visible bars.
        if (_lastChunkProgress is { Length: > 0 })
        {
            int bars = ChunkList.Count;
            for (int i = 0; i < bars; i++)
            {
                double from = i * (_lastChunkProgress.Length / (double)bars);
                double to = (i + 1) * (_lastChunkProgress.Length / (double)bars);
                int start = (int)Math.Floor(from);
                int end = (int)Math.Ceiling(to);
                if (end <= start) end = start + 1;
                double sum = 0;
                for (int j = start; j < end && j < _lastChunkProgress.Length; j++)
                    sum += _lastChunkProgress[j];
                double pct = Math.Clamp(sum / (end - start), 0, 100);
                ChunkList[i].WidthPercent = pct;
                ChunkList[i].ToolTip = $"Thread #{i + 1}: {Math.Round(pct)}%";
            }
            return;
        }

        // Fallback when no chunked state exists yet (single stream / probing / restored state).
        double currentPercent = Task.Progress;
        int count = ChunkList.Count;
        for (int i = 0; i < count; i++)
        {
            double chunkFill = Math.Clamp((currentPercent - i * (100.0 / count)) * count, 0, 100);
            ChunkList[i].WidthPercent = chunkFill;
            ChunkList[i].ToolTip = $"Thread #{i + 1}: {Math.Round(chunkFill)}%";
        }
    }

    private void Task_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        Dispatcher.BeginInvoke(() =>
        {
            OnPropertyChanged(nameof(ProgressTitleText));
            OnPropertyChanged(nameof(DownloadedDetailText));
            OnPropertyChanged(nameof(CanPause));
            OnPropertyChanged(nameof(CanResume));
            OnPropertyChanged(nameof(YouTubeEngineText));
            OnPropertyChanged(nameof(YouTubeStatusHint));
            OnPropertyChanged(nameof(YouTubeProgressFill));
            OnPropertyChanged(nameof(YouTubeProgressRemaining));
            if (e.PropertyName == nameof(DownloadTask.ChunkCount))
            {
                OnPropertyChanged(nameof(ChunkCountText));
                SetupChunkVisuals();
            }
            // Only update chunk visuals for HTTP tasks; YouTube uses single bar via YouTubeProgress* bindings.
            if (!Task.IsYouTube)
                UpdateChunkVisuals();
            else
                ApplyYouTubeMode();

            if (Task.Status == TaskStatus.Completed)
            {
                Title = "100% — Done";
                if (!_completionHandled)
                {
                    _completionHandled = true;
                    HandleCompletionOptions();
                }
            }
            else
            {
                Title = $"{Task.Progress}% {Task.FileName}";
            }
        });
    }

    private void HandleCompletionOptions()
    {
        if (OpenOnComplete)
        {
            string fullPath = Task.FullPath;
            if (string.IsNullOrWhiteSpace(fullPath) || !System.IO.File.Exists(fullPath))
                fullPath = System.IO.Path.Combine(Task.SaveFolder, Task.FileName);

            if (System.IO.File.Exists(fullPath))
            {
                try
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(fullPath) { UseShellExecute = true });
                }
                catch { }
            }
        }

        if (ShutdownOnComplete)
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("shutdown", "/s /t 30 /c \"WDM: Download completed. Shutting down system in 30 seconds.\"")
                {
                    CreateNoWindow = true,
                    UseShellExecute = false
                });
            }
            catch { }
        }

        if (CloseOnComplete)
        {
            Close();
        }
    }

    private void UpdateState()
    {
        Title = $"{Task.Progress}% {Task.FileName}";
    }

    private void Tab_Checked(object sender, RoutedEventArgs e)
    {
        if (PanelStatus == null || PanelLimiter == null || PanelOptions == null)
            return;

        PanelStatus.Visibility = Visibility.Collapsed;
        PanelLimiter.Visibility = Visibility.Collapsed;
        PanelOptions.Visibility = Visibility.Collapsed;

        if (sender == TabStatus)
            PanelStatus.Visibility = Visibility.Visible;
        else if (sender == TabLimiter)
            PanelLimiter.Visibility = Visibility.Visible;
        else if (sender == TabOptions)
            PanelOptions.Visibility = Visibility.Visible;
    }

    private void CopyUrl_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(Task.Url);
        }
        catch
        {
            // Ignore clipboard access errors
        }
    }

    private void PauseClick(object sender, RoutedEventArgs e)
    {
        _mainViewModel.Engine.Pause(Task);
        _mainViewModel.SaveTasksSoon();
        OnPropertyChanged(nameof(CanPause));
        OnPropertyChanged(nameof(CanResume));
    }

    private void ResumeClick(object sender, RoutedEventArgs e)
    {
        Task.Error = null;
        Task.Eta = "";
        _mainViewModel.Engine.Start(Task);
        _mainViewModel.SaveTasksSoon();
        OnPropertyChanged(nameof(CanPause));
        OnPropertyChanged(nameof(CanResume));
    }

    private void SpeedLimit_Changed(object sender, RoutedEventArgs e)
    {
        OnPropertyChanged(nameof(IsSpeedLimitEnabled));
    }

    private void MinimizeClick(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void RevealClick(object sender, RoutedEventArgs e)
    {
        string path = Task.FullPath;
        try
        {
            if (!string.IsNullOrWhiteSpace(path))
            {
                if (System.IO.File.Exists(path))
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
                else if (System.IO.Directory.Exists(Task.SaveFolder))
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Task.SaveFolder) { UseShellExecute = true });
            }
        }
        catch { }
    }

    private void CancelClick(object sender, RoutedEventArgs e)
    {
        if (Task.Status == TaskStatus.Downloading || Task.Status == TaskStatus.Queued)
            _mainViewModel.Engine.Pause(Task);
        Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        _mainViewModel.Engine.ChunkProgressUpdated -= Engine_ChunkProgressUpdated;
        Task.PropertyChanged -= Task_PropertyChanged;
        base.OnClosed(e);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
