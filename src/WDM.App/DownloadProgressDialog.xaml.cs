using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using WDM.Models;
using WDM.Services;
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
            ? $"Block #{index}: downloading: {Math.Round(percent)}%"
            : $"Block #{index}: pending";

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>One live connection row: the planned byte range, its progress and
/// its recent throughput. Mirrors the connection list in IDM's progress view.</summary>
public sealed class ConnectionVisualItem : INotifyPropertyChanged
{
    private string _label = "";
    private string _rangeText = "";
    private double _percent;
    private string _speedText = "";

    public string Label
    {
        get => _label;
        set
        {
            if (_label != value) { _label = value; OnPropertyChanged(); }
        }
    }

    public string RangeText
    {
        get => _rangeText;
        set
        {
            if (_rangeText != value) { _rangeText = value; OnPropertyChanged(); }
        }
    }

    public double Percent
    {
        get => _percent;
        set
        {
            if (Math.Abs(_percent - value) > 0.05)
            {
                _percent = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(PercentText));
            }
        }
    }

    public string PercentText => $"{Math.Round(_percent)}%";

    public string SpeedText
    {
        get => _speedText;
        set
        {
            if (_speedText != value) { _speedText = value; OnPropertyChanged(); }
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public partial class DownloadProgressDialog : Window, INotifyPropertyChanged
{
    private readonly MainViewModel _mainViewModel;
    private double[]? _lastChunkProgress;
    private long _lastChunkUiTick;
    private readonly DispatcherTimer _speedTimer;
    private readonly Queue<double> _speedSamples = new();
    private long[] _lastConnBytes = Array.Empty<long>();
    private long _lastConnTick;
    public DownloadTask Task { get; }

    public const int BlockCount = 64;

    public ObservableCollection<BlockVisualItem> BlockList { get; } = new();

    /// <summary>IDM-style per-connection rows. The IDM core plans at most eight
    /// ranges, and each range is one connection, so this list is the live
    /// connection view rather than a synthetic thread count.</summary>
    public ObservableCollection<ConnectionVisualItem> Connections { get; } = new();

    public string ConnectionSummaryText { get; private set; } = "waiting";

    /// <summary>e.g. "42 / 64 done": bound to the file-map header.</summary>
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
        SetupConnections(1);
        UpdateConnectionFallback();
        ApplyYouTubeMode();
        _speedTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(500)
        };
        _speedTimer.Tick += (_, _) => SampleSpeed();
        // The task may already be Completed when the dialog opens (fast
        // small files): no further PropertyChanged will fire, so evaluate
        // completion options once on load or CloseOnComplete/OpenOnComplete
        // silently never run.
        Loaded += (_, _) =>
        {
            _speedTimer.Start();
            DrawSpeedGraph();
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
            if (Task.Status == TaskStatus.Downloading) return $"Downloading via yt-dlp: {Task.Progress}%";
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
        // "YouTube: via yt-dlp (single stream)" so the dialog never shows
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

    /// <summary>Index of the queue-complete action: 0 = nothing, 1 = exit,
    /// 2 = shut down, 3 = sleep. Delegated to the MainViewModel so the choice
    /// survives this dialog, stays in sync across open progress windows, and
    /// fires even if this task already finished before it was selected.</summary>
    public int QueueCompleteIndex
    {
        get => (int)_mainViewModel.QueueCompleteAction;
        set
        {
            var action = (QueueCompleteAction)Math.Clamp(value, 0, 3);
            if (_mainViewModel.QueueCompleteAction != action)
            {
                _mainViewModel.QueueCompleteAction = action;
                OnPropertyChanged();
            }
            else if (action != QueueCompleteAction.None)
            {
                // Already selected (e.g. from another dialog): re-evaluate in
                // case everything already finished.
                _mainViewModel.MaybeShutdownOnQueueComplete();
            }
        }
    }

    /// <summary>Compatibility alias for the old shutdown-only checkbox.</summary>
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
                _mainViewModel.MaybeShutdownOnQueueComplete();
            }
        }
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.ShutdownWhenQueueComplete))
        {
            OnPropertyChanged(nameof(ShutdownOnComplete));
            OnPropertyChanged(nameof(QueueCompleteIndex));
        }
        else if (e.PropertyName == nameof(MainViewModel.QueueCompleteAction))
        {
            OnPropertyChanged(nameof(QueueCompleteIndex));
        }
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

    private void SetupConnections(int count)
    {
        Connections.Clear();
        for (int i = 0; i < count; i++)
        {
            Connections.Add(new ConnectionVisualItem
            {
                Label = count == 1 ? "Connection 1" : $"Connection {i + 1}",
                RangeText = "waiting"
            });
        }
        _lastConnBytes = new long[count];
        _lastConnTick = 0;
        ConnectionSummaryText = count == 1 ? "single connection" : $"{count} connections";
        OnPropertyChanged(nameof(ConnectionSummaryText));
    }

    /// <summary>Until the first chunk update arrives, show overall progress on a
    /// single row so the list never looks empty during probing.</summary>
    private void UpdateConnectionFallback()
    {
        if (Connections.Count != 1)
            SetupConnections(1);
        var row = Connections[0];
        row.RangeText = Task.TotalBytes > 0 ? Task.SizeText : "waiting for size";
        row.Percent = Math.Clamp(Task.Progress, 0, 100);
        row.SpeedText = Task.Status == TaskStatus.Downloading ? Task.SpeedText : "";
        ConnectionSummaryText = "1 connection";
        OnPropertyChanged(nameof(ConnectionSummaryText));
    }

    /// <summary>Refreshes the per-range rows from the engine's 0-100 progress
    /// array. Per-connection speed is the byte delta since the previous tick.
    /// The IDM core plans at most eight ranges; larger arrays come from the
    /// legacy chunk state, which has no per-connection mapping, so they fall
    /// back to a single overall row.</summary>
    private void UpdateConnections(double[] progress)
    {
        int n = progress.Length;
        if (n == 0 || n > 8)
        {
            UpdateConnectionFallback();
            return;
        }

        if (Connections.Count != n)
        {
            Connections.Clear();
            for (int i = 0; i < n; i++)
                Connections.Add(new ConnectionVisualItem { Label = $"Connection {i + 1}" });
            _lastConnBytes = new long[n];
            _lastConnTick = 0;
        }

        long total = Task.TotalBytes;
        long baseSize = total > 0 ? total / n : 0;
        long now = Environment.TickCount64;
        double elapsed = _lastConnTick > 0 ? Math.Max(0.001, (now - _lastConnTick) / 1000.0) : 0;

        for (int i = 0; i < n; i++)
        {
            var row = Connections[i];
            double pct = Math.Clamp(progress[i], 0, 100);
            long segStart = i * baseSize;
            long segEnd = i == n - 1 ? total : (i + 1) * baseSize;
            long segBytes = Math.Max(1, segEnd - segStart);
            long current = segStart + (long)(segBytes * pct / 100.0);

            double speed = 0;
            if (elapsed > 0 && i < _lastConnBytes.Length && current >= _lastConnBytes[i])
                speed = (current - _lastConnBytes[i]) / elapsed;
            if (i < _lastConnBytes.Length)
                _lastConnBytes[i] = current;

            row.RangeText = total > 0
                ? $"{DownloadTask.FormatBytes(segStart)} - {DownloadTask.FormatBytes(segEnd)}"
                : "shared range";
            row.Percent = pct;
            row.SpeedText = pct >= 99.5
                ? "done"
                : speed > 0 ? $"{DownloadTask.FormatBytes((long)speed)}/s" : "";
        }
        _lastConnTick = now;

        int done = 0;
        int active = 0;
        foreach (var row in Connections)
        {
            if (row.Percent >= 99.5) done++;
            else if (row.Percent > 0) active++;
        }
        ConnectionSummaryText = $"{active} active - {done} done";
        OnPropertyChanged(nameof(ConnectionSummaryText));
    }

    private void SampleSpeed()
    {
        if (!IsLoaded || Visibility != Visibility.Visible)
            return;
        _speedSamples.Enqueue(Math.Max(0, Task.SpeedBps));
        while (_speedSamples.Count > 60)
            _speedSamples.Dequeue();
        DrawSpeedGraph();
    }

    private void DrawSpeedGraph()
    {
        if (SpeedCanvas is null || SpeedLine is null)
            return;
        double width = SpeedCanvas.ActualWidth;
        double height = SpeedCanvas.ActualHeight;
        if (width <= 2 || height <= 2)
            return;

        var samples = _speedSamples.ToArray();
        double peak = 1;
        foreach (var value in samples)
            if (value > peak) peak = value;

        var points = new PointCollection();
        for (int i = 0; i < samples.Length; i++)
        {
            double x = i * (width / 59.0);
            double y = height - 1 - (samples[i] / peak) * (height - 2);
            points.Add(new Point(x, Math.Clamp(y, 1, height - 1)));
        }
        SpeedLine.Points = points;
        if (SpeedBaseline is not null)
        {
            SpeedBaseline.X2 = width;
            SpeedBaseline.Y1 = height - 1;
            SpeedBaseline.Y2 = height - 1;
        }
        if (SpeedNowText is not null)
            SpeedNowText.Text = Task.SpeedText;
        if (SpeedPeakText is not null)
            SpeedPeakText.Text = samples.Length > 0
                ? $"peak {DownloadTask.FormatBytes((long)peak)}/s"
                : "";
    }

    private void SpeedCanvas_SizeChanged(object sender, SizeChangedEventArgs e) => DrawSpeedGraph();

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
            UpdateConnections(progress);
        });
    }

    private void UpdateBlockVisuals()
    {
        if (BlockList.Count == 0) return;

        // Aggregate the engine's per-range progress (0-100 per range, at most
        // eight ranges) onto the fixed 64-block map by file position.
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
            if (_lastChunkProgress is null or { Length: 0 })
                UpdateConnectionFallback();
            if (e.PropertyName == nameof(DownloadTask.ChunkCount))
            {
                OnPropertyChanged(nameof(ChunkCountText));
                // Block map is file-fixed (64 blocks), so thread-count changes
                // don't rebuild it: just refresh from current state.
            }
            // Only update block visuals for HTTP tasks; YouTube uses single bar via YouTubeProgress* bindings.
            if (!Task.IsYouTube)
                UpdateBlockVisuals();
            else
                ApplyYouTubeMode();

            if (Task.Status == TaskStatus.Completed)
            {
                Title = "Download Progress";
                if (!_completionHandled)
                {
                    _completionHandled = true;
                    HandleCompletionOptions();
                }
            }
            else
            {
                Title = "Download Progress";
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

            // Never auto-launch executables on completion: the user can still
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
        Title = "Download Progress";
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
        Task.ErrorDetail = null;
        Task.Eta = "";
        Task.WaitForIdle = false;
        _mainViewModel.Engine.Start(Task);
        _mainViewModel.SaveTasksSoon();
        OnPropertyChanged(nameof(CanPause));
        OnPropertyChanged(nameof(CanResume));
    }

    private void SpeedLimit_Changed(object sender, RoutedEventArgs e)
    {
        OnPropertyChanged(nameof(IsSpeedLimitEnabled));
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
        _speedTimer.Stop();
        _mainViewModel.Engine.ChunkProgressUpdated -= Engine_ChunkProgressUpdated;
        _mainViewModel.PropertyChanged -= ViewModel_PropertyChanged;
        Task.PropertyChanged -= Task_PropertyChanged;
        base.OnClosed(e);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
