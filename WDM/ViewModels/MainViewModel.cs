using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using WDM.Models;
using WDM.Services;

namespace WDM.ViewModels;

public sealed class MainViewModel : INotifyPropertyChanged
{
    private readonly Dispatcher _dispatcher;
    private readonly System.Timers.Timer _saveTimer;
    private DownloadTask? _selectedTask;
    private string _statusText = "WDM ready";
    private string _statusRightText = "";
    private FilterKind _filter = FilterKind.All;
    private string _searchText = "";
    private bool _isSidebarCollapsed = false;

    public ObservableCollection<DownloadTask> Tasks { get; } = new();
    public ObservableCollection<DownloadTask> SelectedTasks { get; } = new();
    public DownloadEngine Engine { get; }
    public ICollectionView TasksView { get; }
    public ObservableCollection<FilterItem> Filters { get; } = new();
    public AppSettings Settings { get; }
    public List<double> SpeedHistory { get; } = new(new double[30]);

    public event Action<DownloadTask?>? AddTaskRequested;
    public event Action<DownloadTask?>? EditTaskRequested;
    public event Action<DownloadTask>? TaskCompleted;
    public event Action<DownloadTask>? ScheduleRequested;
    public event Action<List<double>>? SpeedHistoryUpdated;
    public event Action? AboutRequested;
    public event Action<DownloadTask>? ShowProgressDialogRequested;

    public RelayCommand OpenAddDialogCommand { get; }
    public RelayCommand PauseCommand { get; }
    public RelayCommand ResumeCommand { get; }
    public RelayCommand StopCommand { get; }
    public RelayCommand RemoveCommand { get; }
    public RelayCommand RemoveWithFileCommand { get; }
    public RelayCommand ClearCompletedCommand { get; }
    public RelayCommand RevealCommand { get; }
    public RelayCommand OpenFileCommand { get; }
    public RelayCommand CopyUrlCommand { get; }
    public RelayCommand PropertiesCommand { get; }
    public RelayCommand PauseAllCommand { get; }
    public RelayCommand ResumeAllCommand { get; }
    public RelayCommand OptionsCommand { get; }
    public RelayCommand SetPriorityCommand { get; }
    public RelayCommand MoveUpCommand { get; }
    public RelayCommand MoveDownCommand { get; }
    public RelayCommand ScheduleCommand { get; }
    public RelayCommand TogglePauseCommand { get; }
    public RelayCommand RetryCommand { get; }
    public RelayCommand BulkPauseCommand { get; }
    public RelayCommand BulkResumeCommand { get; }
    public RelayCommand BulkRemoveCommand { get; }
    public RelayCommand BulkPriorityHighCommand { get; }
    public RelayCommand BulkPriorityNormalCommand { get; }
    public RelayCommand ToggleSidebarCommand { get; }
    public RelayCommand ClearSearchCommand { get; }
    public RelayCommand AboutCommand { get; }
    public RelayCommand StartQueueCommand { get; }
    public RelayCommand StopQueueCommand { get; }
    public RelayCommand GrabberCommand { get; }
    public RelayCommand ShowProgressDialogCommand { get; }

    public MainViewModel()
    {
        _dispatcher = Dispatcher.CurrentDispatcher;
        Settings = TaskStore.LoadSettings();
        Engine = new DownloadEngine();
        Engine.MaxConcurrent = Settings.MaxConcurrentDownloads;
        Engine.GlobalSpeedLimitKbps = Settings.GlobalSpeedLimitKbps;
        Engine.MaxRetries = Settings.MaxRetries;
        Engine.ThrottleEnabled = Settings.ThrottleScheduleEnabled;
        Engine.ThrottleStart = Settings.ThrottleStart;
        Engine.ThrottleEnd = Settings.ThrottleEnd;
        Engine.ThrottleLimitKbps = Settings.ThrottleLimitKbps;
        Engine.DownloadWindowEnabled = Settings.DownloadWindowEnabled;
        Engine.WindowStart = Settings.WindowStart;
        Engine.WindowEnd = Settings.WindowEnd;
        Engine.TaskChanged += () => _dispatcher.BeginInvoke(OnTasksChanged);
        Engine.TaskCompleted += task => _dispatcher.BeginInvoke(() =>
        {
            task.CompletedAt ??= DateTime.Now;
            TaskCompleted?.Invoke(task);
            HandlePostDownload(task);
            SaveTasksSoon();
        });

        OpenAddDialogCommand = new RelayCommand(_ => OpenAddDialog());
        PauseCommand = new RelayCommand(_ => PauseSelected(), _ => CanControl);
        ResumeCommand = new RelayCommand(_ => ResumeSelected(), _ => CanResume);
        StopCommand = new RelayCommand(_ => StopSelected(), _ => CanControl);
        RemoveCommand = new RelayCommand(_ => RemoveSelected(), _ => SelectedTask is not null);
        RemoveWithFileCommand = new RelayCommand(_ => RemoveSelected(deleteFiles: true), _ => SelectedTask is not null);
        ClearCompletedCommand = new RelayCommand(_ => ClearCompleted());
        RevealCommand = new RelayCommand(_ => RevealSelected(), _ => SelectedTask is not null);
        OpenFileCommand = new RelayCommand(_ => OpenFile(), _ => SelectedTask?.Status == TaskStatus.Completed);
        CopyUrlCommand = new RelayCommand(_ => CopySelectedUrl(), _ => SelectedTask is not null);
        PropertiesCommand = new RelayCommand(_ => EditTaskRequested?.Invoke(SelectedTask), _ => SelectedTask is not null);
        PauseAllCommand = new RelayCommand(_ => Engine.PauseAll());
        ResumeAllCommand = new RelayCommand(_ => Engine.ResumeAll());
        OptionsCommand = new RelayCommand(_ => OptionsRequested?.Invoke());
        SetPriorityCommand = new RelayCommand(p =>
        {
            if (SelectedTask is not null && p is string value && Enum.TryParse<PriorityLevel>(value, out var level))
                Engine.SetPriority(SelectedTask, level);
        }, _ => SelectedTask is not null);
        MoveUpCommand = new RelayCommand(_ => MoveSelected(-1), _ => CanMoveSelected(-1));
        MoveDownCommand = new RelayCommand(_ => MoveSelected(1), _ => CanMoveSelected(1));
        ScheduleCommand = new RelayCommand(_ => ScheduleRequested?.Invoke(SelectedTask!), _ => SelectedTask is not null);
        TogglePauseCommand = new RelayCommand(_ => TogglePause(), _ => SelectedTask is not null &&
            SelectedTask.Status is TaskStatus.Downloading or TaskStatus.Queued or TaskStatus.Paused);
        RetryCommand = new RelayCommand(_ => RetrySelected(), _ => SelectedTask?.Status == TaskStatus.Failed);
        BulkPauseCommand = new RelayCommand(_ => BulkDo(t => Engine.Pause(t)));
        BulkResumeCommand = new RelayCommand(_ => BulkDo(t => { if (t.Status == TaskStatus.Paused) Engine.Start(t); }));
        BulkRemoveCommand = new RelayCommand(_ =>
        {
            var snapshot = SelectedTasks.ToArray();
            foreach (var t in snapshot)
            {
                Engine.Remove(t);
                Tasks.Remove(t);
            }
            SaveTasksSoon();
            UpdateStatus();
        });
        BulkPriorityHighCommand = new RelayCommand(_ => BulkDo(t => Engine.SetPriority(t, PriorityLevel.High)));
        BulkPriorityNormalCommand = new RelayCommand(_ => BulkDo(t => Engine.SetPriority(t, PriorityLevel.Normal)));
        ToggleSidebarCommand = new RelayCommand(_ => IsSidebarCollapsed = !IsSidebarCollapsed);
        ClearSearchCommand = new RelayCommand(_ => SearchText = "");
        AboutCommand = new RelayCommand(_ => AboutRequested?.Invoke());
        StartQueueCommand = new RelayCommand(_ => Engine.ResumeAll());
        StopQueueCommand = new RelayCommand(_ => Engine.PauseAll());
        GrabberCommand = new RelayCommand(_ => OpenAddDialog());
        ShowProgressDialogCommand = new RelayCommand(_ =>
        {
            if (SelectedTask is not null)
                ShowProgressDialogRequested?.Invoke(SelectedTask);
        }, _ => SelectedTask is not null);

        TasksView = CollectionViewSource.GetDefaultView(Tasks);
        TasksView.Filter = FilterTask;

        var orderedKinds = new[]
        {
            FilterKind.All,
            FilterKind.Video,
            FilterKind.Music,
            FilterKind.Document,
            FilterKind.Compressed,
            FilterKind.Program,
        };
        foreach (var kind in orderedKinds)
            Filters.Add(new FilterItem(kind));
        Filters.Add(FilterItem.Separator);
        foreach (var kind in new[] { FilterKind.Queue, FilterKind.Finished, FilterKind.Paused, FilterKind.Failed })
            Filters.Add(new FilterItem(kind));

        _saveTimer = new System.Timers.Timer(1500)
        {
            AutoReset = false,
        };
        _saveTimer.Elapsed += (_, _) => SaveTasks();

        LoadPersistedTasks();
        UpdateStatus();
    }

    public event Action? OptionsRequested;

    private bool CanControl => SelectedTask is not null &&
        SelectedTask.Status is TaskStatus.Downloading or TaskStatus.Queued;
    private bool CanResume => SelectedTask is not null && SelectedTask.Status == TaskStatus.Paused;

    public bool IsSidebarCollapsed
    {
        get => _isSidebarCollapsed;
        set
        {
            if (_isSidebarCollapsed == value)
                return;
            _isSidebarCollapsed = value;
            OnPropertyChanged(nameof(IsSidebarCollapsed));
            OnPropertyChanged(nameof(SidebarWidth));
            OnPropertyChanged(nameof(CollapseIcon));
            OnPropertyChanged(nameof(CollapseToolTip));
        }
    }

    public double SidebarWidth => IsSidebarCollapsed ? 48 : 180;
    public string CollapseIcon => IsSidebarCollapsed ? "\uE76C" : "\uE76B"; // Chevron Right / Left
    public string CollapseToolTip => IsSidebarCollapsed ? "Expand Sidebar" : "Collapse Sidebar";

    public FilterKind SelectedFilter
    {
        get => _filter;
        set
        {
            if (_filter == value)
                return;
            _filter = value;
            OnPropertyChanged(nameof(SelectedFilter));
            TasksView.Refresh();
            UpdateStatus();
        }
    }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (_searchText == value)
                return;
            _searchText = value;
            OnPropertyChanged(nameof(SearchText));
            OnPropertyChanged(nameof(HasSearchText));
            TasksView.Refresh();
        }
    }

    public bool HasSearchText => !string.IsNullOrEmpty(_searchText);

    public DownloadTask? SelectedTask
    {
        get => _selectedTask;
        set
        {
            _selectedTask = value;
            OnPropertyChanged(nameof(SelectedTask));
            CommandManager.InvalidateRequerySuggested();
        }
    }

    public string StatusText
    {
        get => _statusText;
        private set
        {
            if (_statusText == value)
                return;
            _statusText = value;
            OnPropertyChanged(nameof(StatusText));
        }
    }

    public string StatusRightText
    {
        get => _statusRightText;
        private set
        {
            if (_statusRightText == value)
                return;
            _statusRightText = value;
            OnPropertyChanged(nameof(StatusRightText));
        }
    }

    public bool HasNoTasks => Tasks.Count == 0;

    public string BulkCountText
    {
        get
        {
            int count = SelectedTasks.Count;
            return count == 0 ? "" : count == 1 ? "1 item selected" : $"{count} items selected";
        }
    }

    public void SetBulkSelection(IEnumerable<DownloadTask> tasks)
    {
        SelectedTasks.Clear();
        foreach (var t in tasks)
            SelectedTasks.Add(t);
        OnPropertyChanged(nameof(BulkCountText));
    }

    private void BulkDo(Action<DownloadTask> action)
    {
        var snapshot = SelectedTasks.ToArray();
        foreach (var t in snapshot)
            action(t);
        SaveTasksSoon();
        UpdateStatus();
    }

    private bool FilterTask(object item)
    {
        if (item is not DownloadTask task)
            return false;
        if (!MatchesFilter(task))
            return false;
        if (!string.IsNullOrWhiteSpace(_searchText))
        {
            string needle = _searchText.Trim();
            if (task.FileName?.Contains(needle, StringComparison.OrdinalIgnoreCase) != true &&
                task.Url.Contains(needle, StringComparison.OrdinalIgnoreCase) != true)
                return false;
        }
        return true;
    }

    private bool MatchesFilter(DownloadTask task) => FilterTaskFor(SelectedFilter, task);

    private static bool FilterTaskFor(FilterKind kind, DownloadTask task) => kind switch
    {
        FilterKind.All => true,
        FilterKind.Video => task.Category == DownloadCategory.Video,
        FilterKind.Music => task.Category == DownloadCategory.Music,
        FilterKind.Document => task.Category == DownloadCategory.Document,
        FilterKind.Compressed => task.Category == DownloadCategory.Compressed,
        FilterKind.Program => task.Category == DownloadCategory.Program,
        FilterKind.Queue => task.Status is TaskStatus.Queued or TaskStatus.Downloading or TaskStatus.Scheduled,
        FilterKind.Finished => task.Status == TaskStatus.Completed,
        FilterKind.Paused => task.Status == TaskStatus.Paused,
        FilterKind.Failed => task.Status == TaskStatus.Failed,
        _ => true,
    };

    private static bool IsActive(DownloadTask task) =>
        task.Status is TaskStatus.Queued or TaskStatus.Downloading or TaskStatus.Scheduled;

    private void LoadPersistedTasks()
    {
        foreach (var record in TaskStore.LoadTasks())
        {
            var task = new DownloadTask(_dispatcher)
            {
                Url = record.Url,
                Referer = record.Referer,
                FileName = record.FileName,
                SaveFolder = string.IsNullOrWhiteSpace(record.SaveFolder) ? DownloadTask.DefaultSaveFolder : record.SaveFolder,
                ChunkCount = record.ChunkCount,
                TotalBytes = record.TotalBytes,
                SpeedLimitKbps = record.SpeedLimitKbps,
                Priority = record.Priority,
                Category = record.Category,
                ScheduledStart = record.ScheduledStart,
                Checksum = record.Checksum,
                CompletedAt = record.CompletedAt,
            };
            task.Status = record.Status == TaskStatus.Downloading ? TaskStatus.Paused : record.Status;
            if (record.Status is TaskStatus.Queued or TaskStatus.Scheduled or TaskStatus.Paused)
            {
                task.Status = TaskStatus.Paused;
                if (record.ScheduledStart is DateTime s && s > DateTime.Now)
                    Engine.Start(task);
            }
            Tasks.Add(task);
        }
    }

    public bool ExistingUrl(string url)
    {
        string needle = url.Trim();
        return Tasks.Any(t => string.Equals(t.Url, needle, StringComparison.OrdinalIgnoreCase));
    }

    public void AddTask(string url, string? fileName = null, string? referer = null, int chunkCount = 4)
    {
        if (string.IsNullOrWhiteSpace(url))
            return;
        if (!_dispatcher.CheckAccess())
        {
            _dispatcher.BeginInvoke(() => AddTask(url, fileName, referer, chunkCount));
            return;
        }
        var task = new DownloadTask(_dispatcher)
        {
            Url = url.Trim(),
            Referer = referer,
            ChunkCount = Math.Max(1, chunkCount),
            SaveFolder = Settings.DownloadFolder,
            SpeedLimitKbps = 0,
        };
        if (!string.IsNullOrWhiteSpace(fileName))
            task.FileName = DownloadEngine.SanitizeFileName(fileName);
        task.Category = DownloadTask.Categorize(task.FileName);

        Tasks.Add(task);
        ApplyCategoryRouting(task);
        Engine.Start(task);
        SelectedFilter = FilterKind.All;
        SaveTasksSoon();
        UpdateStatus();
    }

    public void AddTask(DownloadTask task)
    {
        if (!_dispatcher.CheckAccess())
        {
            _dispatcher.BeginInvoke(() => AddTask(task));
            return;
        }
        if (string.IsNullOrWhiteSpace(task.FileName))
        {
            task.FileName = DownloadEngine.DeriveName(task.Url);
        }
        Tasks.Add(task);
        ApplyCategoryRouting(task);
        Engine.Start(task);
        SelectedFilter = FilterKind.All;
        SaveTasksSoon();
        UpdateStatus();
    }

    private void ApplyCategoryRouting(DownloadTask task)
    {
        if (!Settings.RouteByCategory || task.Category == DownloadCategory.Other)
            return;
        if (Settings.CategoryFolders.TryGetValue(task.Category.ToString(), out string? folder) &&
            !string.IsNullOrWhiteSpace(folder))
        {
            task.SaveFolder = folder;
        }
    }

    public void OpenAddDialog() => AddTaskRequested?.Invoke(null);

    public void PauseSelected()
    {
        if (SelectedTask is not null)
            Engine.Pause(SelectedTask);
    }

    public void ResumeSelected()
    {
        if (SelectedTask is not null && SelectedTask.Status == TaskStatus.Paused)
            Engine.Start(SelectedTask);
    }

    public void StopSelected()
    {
        if (SelectedTask is not null)
            Engine.Stop(SelectedTask);
    }

    public void TogglePause()
    {
        if (SelectedTask is null)
            return;
        if (SelectedTask.Status is TaskStatus.Downloading or TaskStatus.Queued)
            Engine.Pause(SelectedTask);
        else if (SelectedTask.Status == TaskStatus.Paused)
            Engine.Start(SelectedTask);
    }

    public void ToggleTask(DownloadTask task)
    {
        if (task.Status is TaskStatus.Downloading or TaskStatus.Queued)
            Engine.Pause(task);
        else if (task.Status == TaskStatus.Paused)
            Engine.Start(task);
        SaveTasksSoon();
        UpdateStatus();
    }

    public void RemoveTask(DownloadTask task)
    {
        Engine.Remove(task);
        Tasks.Remove(task);
        SaveTasksSoon();
        UpdateStatus();
    }

    public void RetrySelected()
    {
        if (SelectedTask is not { Status: TaskStatus.Failed } task)
            return;
        task.Error = null;
        task.Eta = "";
        Engine.Start(task);
        SaveTasksSoon();
        UpdateStatus();
    }

    public void CopySelectedUrl()
    {
        if (SelectedTask is null)
            return;
        try
        {
            Clipboard.SetText(SelectedTask.Url);
        }
        catch
        {
            // Clipboard lock fallback
        }
    }

    public void RemoveSelected(bool deleteFiles = false)
    {
        if (SelectedTask is null)
            return;
        var task = SelectedTask;
        Engine.Remove(task, deleteFiles);
        Tasks.Remove(task);
        SaveTasksSoon();
        UpdateStatus();
    }

    public void MoveSelected(int direction)
    {
        if (SelectedTask is null)
            return;
        Engine.MoveQueued(SelectedTask, direction);
        SaveTasksSoon();
    }

    private bool CanMoveSelected(int direction)
    {
        if (SelectedTask is null)
            return false;
        if (SelectedTask.Status != TaskStatus.Queued)
            return false;
        int index = Tasks.IndexOf(SelectedTask);
        if (index < 0)
            return false;
        return direction switch
        {
            -1 => index > 0,
            1 => index < Tasks.Count - 1,
            _ => false,
        };
    }

    public void ScheduleSelected(DateTime when)
    {
        if (SelectedTask is null)
            return;
        Engine.Schedule(SelectedTask, when);
        SaveTasksSoon();
        UpdateStatus();
    }

    private async void HandlePostDownload(DownloadTask task)
    {
        try
        {
            if (Settings.ComputeChecksum && File.Exists(task.FullPath))
            {
                task.Checksum = await Task.Run(() => ComputeChecksum(task.FullPath));
                SaveTasksSoon();
            }

            string? script = Settings.PostDownloadScript;
            if (!string.IsNullOrWhiteSpace(script) && File.Exists(script) && File.Exists(task.FullPath))
            {
                Process.Start(new ProcessStartInfo(script)
                {
                    UseShellExecute = true,
                    Arguments = $"\"{task.FullPath}\"",
                });
            }
        }
        catch
        {
            // Best-effort script/checksum handling.
        }
    }

    private static string ComputeChecksum(string path)
    {
        using var stream = File.OpenRead(path);
        using var sha = System.Security.Cryptography.SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(stream));
    }

    public void ClearCompleted()
    {
        foreach (var task in Tasks.Where(t => t.Status == TaskStatus.Completed).ToList())
            Tasks.Remove(task);
        SaveTasksSoon();
        UpdateStatus();
    }

    public void RevealSelected()
    {
        if (SelectedTask is null)
            return;
        string? path = SelectedTask.FullPath;
        if (!string.IsNullOrWhiteSpace(path))
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
    }

    public void OpenFile()
    {
        if (SelectedTask?.Status == TaskStatus.Completed && File.Exists(SelectedTask.FullPath))
            Process.Start(new ProcessStartInfo(SelectedTask.FullPath) { UseShellExecute = true });
    }

    public void SaveTasksSoon()
    {
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void SaveTasks()
    {
        TaskStore.SaveTasks(Tasks);
        UpdateStatus();
    }

    public void PersistSettings()
    {
        TaskStore.SaveSettings(Settings);
        Engine.MaxConcurrent = Settings.MaxConcurrentDownloads;
        Engine.GlobalSpeedLimitKbps = Settings.GlobalSpeedLimitKbps;
        Engine.MaxRetries = Settings.MaxRetries;
        Engine.ThrottleEnabled = Settings.ThrottleScheduleEnabled;
        Engine.ThrottleStart = Settings.ThrottleStart;
        Engine.ThrottleEnd = Settings.ThrottleEnd;
        Engine.ThrottleLimitKbps = Settings.ThrottleLimitKbps;
        Engine.DownloadWindowEnabled = Settings.DownloadWindowEnabled;
        Engine.WindowStart = Settings.WindowStart;
        Engine.WindowEnd = Settings.WindowEnd;
        ApplyRunAtStartup();
    }

    private void ApplyRunAtStartup()
    {
        const string runKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string appName = "WDM";
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(runKey, writable: true);
            if (key is null)
                return;
            if (Settings.RunAtStartup)
            {
                string exe = Environment.ProcessPath ?? ApplicationPath;
                key.SetValue(appName, $"\"{exe}\"");
            }
            else
            {
                key.DeleteValue(appName, throwOnMissingValue: false);
            }
        }
        catch
        {
            // Registry write failed.
        }
    }

    private static string ApplicationPath =>
        System.Reflection.Assembly.GetExecutingAssembly().Location;

    private void OnTasksChanged()
    {
        RefreshFilterCounts();
        CommandManager.InvalidateRequerySuggested();
        OnPropertyChanged(nameof(HasNoTasks));
        UpdateStatus();
    }

    private void RefreshFilterCounts()
    {
        foreach (var filter in Filters)
        {
            int count = Tasks.Count(t => FilterTaskFor(filter.Kind, t));
            int active = filter.IsCategory ? Tasks.Count(t => FilterTaskFor(filter.Kind, t) && IsActive(t)) : 0;
            filter.Count = count;
            filter.ActiveCount = active;
        }
    }

    private void UpdateStatus()
    {
        int active = Engine.ActiveCount;
        int queued = Engine.QueuedCount;
        RefreshFilterCounts();

        double totalSpeedBps = Engine.TotalSpeedBps;
        SpeedHistory.RemoveAt(0);
        SpeedHistory.Add(totalSpeedBps);
        SpeedHistoryUpdated?.Invoke(SpeedHistory);

        if (active == 0 && queued == 0)
        {
            int total = Tasks.Count;
            int completed = Tasks.Count(t => t.Status == TaskStatus.Completed);
            StatusText = total == 0
                ? "WDM — ready. Copy a link to download, or use the browser extension."
                : $"WDM — {total} task(s), {completed} completed.";
            StatusRightText = "Total: 0 B/s";
            return;
        }

        string speed = DownloadTask.FormatBytes((long)totalSpeedBps);
        string parts = new List<string>
        {
            active > 0 ? $"{active} downloading" : null!,
            queued > 0 ? $"{queued} queued" : null!,
        }.Where(s => !string.IsNullOrEmpty(s)).Aggregate((a, b) => $"{a} · {b}");
        StatusText = $"{parts} · {speed}/s";
        StatusRightText = $"Total: {speed}/s";
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public enum FilterKind
{
    All,
    Video,
    Music,
    Document,
    Compressed,
    Program,
    Queue,
    Finished,
    Paused,
    Failed,
}

public sealed class FilterItem : INotifyPropertyChanged
{
    private int _count;
    private int _activeCount;

    public FilterItem(FilterKind kind) => Kind = kind;

    public static FilterItem Separator => new(FilterKind.All) { IsSeparator = true };

    public bool IsSeparator { get; private init; }
    public FilterKind Kind { get; }
    public bool IsCategory => Kind is FilterKind.Video or FilterKind.Music or FilterKind.Document or FilterKind.Compressed or FilterKind.Program;

    public string Icon => Kind switch
    {
        FilterKind.All => "\uE774",
        FilterKind.Video => "\uE714",
        FilterKind.Music => "\uE8D6",
        FilterKind.Document => "\uE8A5",
        FilterKind.Compressed => "\uF133",
        FilterKind.Program => "\uE756",
        FilterKind.Queue => "\uE806",
        FilterKind.Finished => "\uE73E",
        FilterKind.Paused => "\uE769",
        FilterKind.Failed => "\uEA39",
        _ => "\uE774",
    };

    public string Name => Kind switch
    {
        FilterKind.All => "All Downloads",
        FilterKind.Video => "Video",
        FilterKind.Music => "Music",
        FilterKind.Document => "Documents",
        FilterKind.Compressed => "Archives",
        FilterKind.Program => "Programs",
        FilterKind.Queue => "Queue",
        FilterKind.Finished => "Finished",
        FilterKind.Paused => "Paused",
        FilterKind.Failed => "Failed",
        _ => Kind.ToString(),
    };

    public int Count
    {
        get => _count;
        set
        {
            if (_count == value)
                return;
            _count = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Count)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CountText)));
        }
    }

    public int ActiveCount
    {
        get => _activeCount;
        set
        {
            if (_activeCount == value)
                return;
            _activeCount = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ActiveCount)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CountText)));
        }
    }

    public string CountText =>
        Count == 0 ? ""
        : IsCategory && ActiveCount > 0 ? $"{ActiveCount}/{Count}"
        : Count.ToString();

    public event PropertyChangedEventHandler? PropertyChanged;
}

