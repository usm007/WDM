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
    public string ToolTip { get; set; } = "";
    private double _widthPercent = 0;
    public double WidthPercent
    {
        get => _widthPercent;
        set
        {
            if (_widthPercent != value)
            {
                _widthPercent = value;
                OnPropertyChanged();
            }
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public partial class DownloadProgressDialog : Window, INotifyPropertyChanged
{
    private readonly MainViewModel _mainViewModel;
    public DownloadTask Task { get; }

    public ObservableCollection<ChunkVisualItem> ChunkList { get; } = new();

    public DownloadProgressDialog(DownloadTask task, MainViewModel mainViewModel)
    {
        InitializeComponent();
        Task = task;
        _mainViewModel = mainViewModel;
        DataContext = this;

        Task.PropertyChanged += Task_PropertyChanged;
        UpdateState();
        SetupChunkVisuals();
    }

    public string ProgressTitleText => $"{Task.Progress}%";
    public string DownloadedDetailText => $"{Task.DownloadedText} ({Task.Progress}%)";
    public string ChunkCountText => $"{Task.ChunkCount} threads";
    public string PauseButtonText => Task.Status == TaskStatus.Paused ? "Resume" : "Pause";

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

    public bool CloseOnComplete { get; set; } = true;
    public bool OpenOnComplete { get; set; } = false;
    public bool ShutdownOnComplete { get; set; } = false;

    private void SetupChunkVisuals()
    {
        ChunkList.Clear();
        int count = Math.Max(1, Task.ChunkCount);
        for (int i = 0; i < count; i++)
        {
            ChunkList.Add(new ChunkVisualItem
            {
                Index = i + 1,
                ToolTip = $"Thread #{i + 1} active",
                WidthPercent = 30 // initial sample visual
            });
        }
        UpdateChunkVisuals();
    }

    private void UpdateChunkVisuals()
    {
        if (ChunkList.Count == 0) return;
        double currentPercent = Task.Progress;
        for (int i = 0; i < ChunkList.Count; i++)
        {
            // Calculate pseudo chunk fill for visual feedback based on overall progress
            double chunkFill = Math.Min(100, Math.Max(0, (currentPercent - (i * (100.0 / ChunkList.Count))) * ChunkList.Count));
            ChunkList[i].WidthPercent = chunkFill;
        }
    }

    private void Task_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        Dispatcher.BeginInvoke(() =>
        {
            OnPropertyChanged(nameof(ProgressTitleText));
            OnPropertyChanged(nameof(DownloadedDetailText));
            OnPropertyChanged(nameof(PauseButtonText));
            UpdateChunkVisuals();

            if (Task.Status == TaskStatus.Completed)
            {
                Title = $"100% - Download Finished";
                if (CloseOnComplete)
                    Close();
            }
            else
            {
                Title = $"{Task.Progress}% {Task.FileName}";
            }
        });
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

    private void PauseClick(object sender, RoutedEventArgs e)
    {
        _mainViewModel.ToggleTask(Task);
        OnPropertyChanged(nameof(PauseButtonText));
    }

    private void SpeedLimit_Changed(object sender, RoutedEventArgs e)
    {
        OnPropertyChanged(nameof(IsSpeedLimitEnabled));
    }

    private void BackgroundClick(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void CancelClick(object sender, RoutedEventArgs e)
    {
        if (Task.Status == TaskStatus.Downloading || Task.Status == TaskStatus.Queued)
            _mainViewModel.Engine.Pause(Task);
        Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        Task.PropertyChanged -= Task_PropertyChanged;
        base.OnClosed(e);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
