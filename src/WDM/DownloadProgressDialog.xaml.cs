using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using WDM.Models;
using WDM.ViewModels;

namespace WDM;

/// <summary>A single cell of the file block map: 1/64th of the file.
/// Status is derived from <see cref="Percent"/>: 0 = pending, 1 = downloading, 2 = done.</summary>
public sealed class BlockVisualItem : INotifyPropertyChanged
{
    public const int Pending = 0;
    public const int Active = 1;
    public const int Done = 2;

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

    private double _percent = 0;
    public double Percent
    {
        get => _percent;
        set
        {
            if (Math.Abs(_percent - value) > 0.01)
            {
                _percent = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(Status));
            }
        }
    }

    public int Status => Percent >= 99.5 ? Done : Percent > 0.5 ? Active : Pending;

    public static string GetToolTip(int index, double percent) => percent >= 99.5
        ? $"Block #{index}: complete"
        : percent > 0.5
            ? $"Block #{index}: downloading — {Math.Round(percent)}%"
            : $"Block #{index}: pending";

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public partial class DownloadProgressDialog : Window, INotifyPropertyChanged
{
    private readonly MainViewModel _mainViewModel;
    private double[]? _lastChunkProgress;
    private long _lastChunkUiTick;
    public DownloadTask Task { get; }

    public const int BlockCount = 64;

    public ObservableCollection<BlockVisualItem> BlockList { get; } = new();

    /// <summary>e.g. "42 / 64 done" — bound to the file-map header.</summary>
    public string BlockSummaryText
    {
        get
        {
            int done = 0;
            foreach (var b in BlockList)
                if (b.Status == BlockVisualItem.Done) done++;
            return $"{done} / {BlockCount} done";
        }
    }

    public DownloadProgressDialog(DownloadTask task, MainViewModel mainViewModel)
    {
        InitializeComponent();
        Task = task;
        _mainViewModel = mainViewModel;
        DataContext = this;

        Task.PropertyChanged += Task_PropertyChanged;
        _mainViewModel.Engine.ChunkProgressUpdated += Engine_ChunkProgressUpdated;
        _mainViewModel.PropertyChanged += ViewModel_PropertyChanged;
        UpdateState();
        SetupBlockVisuals();
        ApplyYouTubeMode();
        // The task may already be Completed when the dialog opens (fast
        // small files): no further PropertyChanged will fire, so evaluate
        // completion options once on load or CloseOnComplete/OpenOnComplete
        // silently never run.
        Loaded += (_, _) =>
        {
            if (Task.Status == TaskStatus.Completed && !_completionHandled)
            {
                _completionHandled = true;
                HandleCompletionOptions();
            }
        };
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

    private static bool IsRiskyExecutable(string path)
    {
        string ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
        return ext is ".exe" or ".msi" or ".bat" or ".cmd" or ".ps1" or ".vbs" or ".vbe"
            or ".js" or ".jse" or ".wsf" or ".wsh" or ".lnk" or ".scr" or ".com"
            or ".pif" or ".reg" or ".jar" or ".msc" or ".hta";
    }

    public string ProgressTitleText => Task.TotalBytes > 0
        ? $"{Task.Progress}% · {Task.DownloadedText} / {Task.SizeText}"
        : $"{Task.Progress}% · {Task.DownloadedText}";
    public string DownloadedDetailText => Task.TotalBytes > 0
        ? $"{Task.DownloadedText} / {Task.SizeText} ({Task.Progress}%)"
        : $"{Task.DownloadedText} ({Task.Progress}%)";
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
            }
            else if (Task.SpeedLimitKbps <= 0)
            {
                Task.SpeedLimitKbps = 500;
            }
            OnPropertyChanged();
            OnPropertyChanged(nameof(TaskSpeedLimit));
        }
    }

    public long TaskSpeedLimit
    {
        get => Task.SpeedLimitKbps;
        set
        {
            Task.SpeedLimitKbps = Math.Max(0, value);
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsSpeedLimitEnabled));
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

    /// <summary>Global flag (stored on the MainViewModel): "shutdown after ALL
    /// active downloads finish". Delegated so checking the box in any dialog
    /// survives that dialog being closed, stays in sync across open dialogs,
    /// and fires even if this task already finished before the box was checked.</summary>
    public bool ShutdownOnComplete
    {
        get => _mainViewModel.ShutdownWhenQueueComplete;
        set
        {
            if (_mainViewModel.ShutdownWhenQueueComplete != value)
            {
                _mainViewModel.ShutdownWhenQueueComplete = value;
                OnPropertyChanged();
            }
            else if (value)
            {
                // Already on (e.g. enabled from another dialog): re-evaluate in
                // case everything already finished.
                _mainViewModel.MaybeShutdownOnQueueComplete();
            }
        }
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.ShutdownWhenQueueComplete))
            OnPropertyChanged(nameof(ShutdownOnComplete));
    }

    private bool _completionHandled = false;

    private void SetupBlockVisuals()
    {
        BlockList.Clear();
        for (int i = 0; i < BlockCount; i++)
        {
            BlockList.Add(new BlockVisualItem
            {
                Index = i + 1,
                ToolTip = BlockVisualItem.GetToolTip(i + 1, 0),
                Percent = 0
            });
        }
        UpdateBlockVisuals();
    }

    private void Engine_ChunkProgressUpdated(DownloadTask task, double[] progress)
    {
        if (task.Id != Task.Id)
            return;
        if (!IsLoaded || Visibility != Visibility.Visible)
            return;
        // Coalesce 4Hz engine ticks: block visuals at ~2Hz are indistinguishable.
        long now = Environment.TickCount64;
        if (now - _lastChunkUiTick < 500)
            return;
        _lastChunkUiTick = now;
        Dispatcher.BeginInvoke(() =>
        {
            _lastChunkProgress = progress;
            UpdateBlockVisuals();
        });
    }

    private void UpdateBlockVisuals()
    {
        if (BlockList.Count == 0) return;

        // Aggregate the engine's per-segment progress (binary 0/100 over
        // potentially hundreds of file segments) onto the fixed 64-block map.
        // Block identity is file position, not thread — threads pull from a
        // shared pool, so per-thread bars were synthetic anyway.
        if (Task.Status == TaskStatus.Completed)
        {
            for (int i = 0; i < BlockCount; i++)
                SetBlock(i, 100);
        }
        else if (_lastChunkProgress is { Length: > 0 })
        {
            for (int i = 0; i < BlockCount; i++)
            {
                double from = i * (_lastChunkProgress.Length / (double)BlockCount);
                double to = (i + 1) * (_lastChunkProgress.Length / (double)BlockCount);
                int start = (int)Math.Floor(from);
                int end = (int)Math.Ceiling(to);
                if (end <= start) end = start + 1;
                double sum = 0;
                for (int j = start; j < end && j < _lastChunkProgress.Length; j++)
                    sum += _lastChunkProgress[j];
                double pct = Math.Clamp(sum / (end - start), 0, 100);
                SetBlock(i, pct);
            }
        }
        else
        {
            // Fallback when no chunked state exists yet (single stream / probing /
            // restored state): fill proportionally from overall progress.
            double currentPercent = Task.Progress;
            for (int i = 0; i < BlockCount; i++)
            {
                double fill = Math.Clamp((currentPercent - i * (100.0 / BlockCount)) * BlockCount, 0, 100);
                SetBlock(i, fill);
            }
        }

        OnPropertyChanged(nameof(BlockSummaryText));
    }

    private void SetBlock(int index, double percent)
    {
        var block = BlockList[index];
        block.Percent = percent;
        block.ToolTip = BlockVisualItem.GetToolTip(index + 1, percent);
    }

    private void Task_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!IsLoaded || Visibility != Visibility.Visible)
            return;
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
                // Block map is file-fixed (64 blocks), so thread-count changes
                // don't rebuild it — just refresh from current state.
            }
            // Only update block visuals for HTTP tasks; YouTube uses single bar via YouTubeProgress* bindings.
            if (!Task.IsYouTube)
                UpdateBlockVisuals();
            else
                ApplyYouTubeMode();

            if (Task.Status == TaskStatus.Completed)
            {
                Title = Task.FileName;
                if (!_completionHandled)
                {
                    _completionHandled = true;
                    HandleCompletionOptions();
                }
            }
            else
            {
                Title = Task.FileName;
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

            // Never auto-launch executables on completion — the user can still
            // open them manually from the Complete dialog (with a warning).
            if (System.IO.File.Exists(fullPath) && !IsRiskyExecutable(fullPath))
            {
                try
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(fullPath) { UseShellExecute = true });
                }
                catch { }
            }
        }

        // Global one-shot shutdown: only fires when no active/queued downloads
        // remain (see MainViewModel.MaybeShutdownOnQueueComplete). Checking the
        // box after completion is handled by the ShutdownOnComplete setter.
        _mainViewModel.MaybeShutdownOnQueueComplete();

        if (CloseOnComplete)
        {
            Close();
        }
    }

    private void UpdateState()
    {
        Title = Task.FileName;
    }

    private void Tab_Checked(object sender, RoutedEventArgs e)
    {
        if (PanelLimiter == null || PanelOptions == null || PanelDetails == null)
            return;

        if (PanelStatus != null) PanelStatus.Visibility = Visibility.Collapsed;
        PanelDetails.Visibility = Visibility.Collapsed;
        PanelLimiter.Visibility = Visibility.Collapsed;
        PanelOptions.Visibility = Visibility.Collapsed;

        if (sender == TabDetails)
            PanelDetails.Visibility = Visibility.Visible;
        else if (sender == TabLimiter)
            PanelLimiter.Visibility = Visibility.Visible;
        else if (sender == TabOptions)
            PanelOptions.Visibility = Visibility.Visible;
    }

    /// <summary>Clicking the active tab button again collapses its panel.</summary>
    private void Tab_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is System.Windows.Controls.RadioButton rb && rb.IsChecked == true)
        {
            rb.IsChecked = false;
            if (PanelDetails != null) PanelDetails.Visibility = Visibility.Collapsed;
            if (PanelLimiter != null) PanelLimiter.Visibility = Visibility.Collapsed;
            if (PanelOptions != null) PanelOptions.Visibility = Visibility.Collapsed;
            e.Handled = true;
        }
    }

    private void Tab_Unchecked(object sender, RoutedEventArgs e)
    {
        if (PanelDetails != null) PanelDetails.Visibility = Visibility.Collapsed;
        if (PanelLimiter != null) PanelLimiter.Visibility = Visibility.Collapsed;
        if (PanelOptions != null) PanelOptions.Visibility = Visibility.Collapsed;
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
        _mainViewModel.PropertyChanged -= ViewModel_PropertyChanged;
        Task.PropertyChanged -= Task_PropertyChanged;
        base.OnClosed(e);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
