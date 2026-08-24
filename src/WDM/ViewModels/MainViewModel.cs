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
    private string _statusText = "WDM — ready";
    private string _statusRightText = "";
    private FilterKind _filter = FilterKind.All;
    private string _searchText = "";
    private bool _isSidebarCollapsed = false;
    private double _sidebarWidth = 155;

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
    public event Action<List<double>>? SpeedHistoryUpdated;
    public event Action? AboutRequested;
    public event Action<DownloadTask>? ShowProgressDialogRequested;
    public event Action<DownloadTask>? RefreshLinkRequested;

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
    public RelayCommand TogglePauseCommand { get; }
    public RelayCommand RetryCommand { get; }
    public RelayCommand BulkPauseCommand { get; }
    public RelayCommand BulkResumeCommand { get; }
    public RelayCommand BulkRemoveCommand { get; }
    public RelayCommand BulkPriorityHighCommand { get; }
    public RelayCommand BulkPriorityNormalCommand { get; }
    public RelayCommand ToggleSidebarCommand { get; }
    public RelayCommand ToggleThemeCommand { get; }
    public RelayCommand ClearSearchCommand { get; }
    public RelayCommand AboutCommand { get; }
    public RelayCommand StartQueueCommand { get; }
    public RelayCommand StopQueueCommand { get; }
    public RelayCommand ShowProgressDialogCommand { get; }
    public RelayCommand RetryAllFailedCommand { get; }
    public RelayCommand DismissFailedBannerCommand { get; }
    public RelayCommand RefreshLinkCommand { get; }

    /// <summary>Raised before a destructive delete so the view can confirm with the
    /// user. The handler shows the themed DeleteConfirmDialog and, if confirmed, sets
    /// <see cref="DeletePromptRequest.DeleteFromDisk"/>. A null result = cancelled.</summary>
    public event Action<DeletePromptRequest>? DeletePromptRequested;

    public MainViewModel()
    {
        _dispatcher = Dispatcher.CurrentDispatcher;
        Settings = TaskStore.LoadSettings();
        Engine = new DownloadEngine();
        Engine.MaxConcurrent = Settings.MaxConcurrentDownloads;
        Engine.GlobalSpeedLimitKbps = Settings.GlobalSpeedLimitKbps;
        Engine.MaxRetries = Settings.MaxRetries;
        Engine.TaskChanged += () => _dispatcher.BeginInvoke(OnTasksChanged);
        Engine.TaskCompleted += task => _dispatcher.BeginInvoke(() =>
        {
            task.CompletedAt ??= DateTime.Now;
            TaskCompleted?.Invoke(task);
            HandlePostDownload(task);
            SaveTasksSoon();
        });

        OpenAddDialogCommand = new RelayCommand(_ => OpenAddDialog());
        PauseCommand = new RelayCommand(_ => PauseSelected(), _ => CanPause);
        ResumeCommand = new RelayCommand(_ => ResumeSelected(), _ => CanResume);
        StopCommand = new RelayCommand(_ => StopSelected(), _ => CanStop);
        RemoveCommand = new RelayCommand(_ => RemoveSelected(), _ => SelectedTask is not null);
        RemoveWithFileCommand = new RelayCommand(_ => RemoveSelected(deleteFiles: true), _ => SelectedTask is not null);
        ClearCompletedCommand = new RelayCommand(_ => ClearCompleted());
        RevealCommand = new RelayCommand(_ => RevealSelected(), _ => SelectedTask is not null);
        OpenFileCommand = new RelayCommand(_ => OpenFile(), _ => SelectedTask?.Status == TaskStatus.Completed);
        CopyUrlCommand = new RelayCommand(_ => CopySelectedUrl(), _ => SelectedTask is not null);
        PropertiesCommand = new RelayCommand(_ => EditTaskRequested?.Invoke(SelectedTask), _ => SelectedTask is not null);
        PauseAllCommand = new RelayCommand(_ => Engine.PauseAll());
        ResumeAllCommand = new RelayCommand(_ => ResumeAll());
        OptionsCommand = new RelayCommand(_ => OptionsRequested?.Invoke());
        SetPriorityCommand = new RelayCommand(p =>
        {
            if (SelectedTask is not null && p is string value && Enum.TryParse<PriorityLevel>(value, out var level))
                Engine.SetPriority(SelectedTask, level);
        }, _ => SelectedTask is not null);
        MoveUpCommand = new RelayCommand(_ => MoveSelected(-1), _ => CanMoveSelected(-1));
        MoveDownCommand = new RelayCommand(_ => MoveSelected(1), _ => CanMoveSelected(1));
        TogglePauseCommand = new RelayCommand(_ => TogglePause(), _ => SelectedTask is not null &&
            SelectedTask.Status is TaskStatus.Downloading or TaskStatus.Queued or TaskStatus.Paused);
        RetryCommand = new RelayCommand(_ => RetrySelected(), _ => SelectedTask?.Status == TaskStatus.Failed);
        BulkPauseCommand = new RelayCommand(_ => BulkDo(t => Engine.Pause(t)));
        BulkResumeCommand = new RelayCommand(_ => BulkDo(t => { if (t.Status == TaskStatus.Paused) Engine.Start(t); }));
        BulkRemoveCommand = new RelayCommand(_ =>
        {
            var snapshot = SelectedTasks.ToArray();
            if (snapshot.Length == 0)
                return;
            var prompt = new DeletePromptRequest
            {
                Message = $"Delete {snapshot.Length} selected download{(snapshot.Length == 1 ? "" : "s")}?",
                DiskChecked = false,
            };
            DeletePromptRequested?.Invoke(prompt);
            if (prompt.DeleteFromDisk is not bool disk)
                return;
            foreach (var t in snapshot)
            {
                Engine.Remove(t, disk);
                Tasks.Remove(t);
            }
            SaveTasksSoon();
            UpdateStatus();
        });
        BulkPriorityHighCommand = new RelayCommand(_ => BulkDo(t => Engine.SetPriority(t, PriorityLevel.High)));
        BulkPriorityNormalCommand = new RelayCommand(_ => BulkDo(t => Engine.SetPriority(t, PriorityLevel.Normal)));
        ToggleSidebarCommand = new RelayCommand(_ => IsSidebarCollapsed = !IsSidebarCollapsed);
        ToggleThemeCommand = new RelayCommand(_ => IsDarkTheme = !IsDarkTheme);
        ClearSearchCommand = new RelayCommand(_ => SearchText = "");
        AboutCommand = new RelayCommand(_ => AboutRequested?.Invoke());
        StartQueueCommand = new RelayCommand(_ => Engine.ResumeAll());
        StopQueueCommand = new RelayCommand(_ => Engine.PauseAll());
        ShowProgressDialogCommand = new RelayCommand(_ =>
        {
            if (SelectedTask is not null)
                ShowProgressDialogRequested?.Invoke(SelectedTask);
        }, _ => SelectedTask is not null);
        RetryAllFailedCommand = new RelayCommand(_ =>
        {
            var failedTasks = Tasks.Where(t => t.Status == TaskStatus.Failed).ToList();
            foreach (var task in failedTasks)
            {
                task.Error = null;
                task.Eta = "";
                Engine.Start(task);
            }
            IsFailedBannerDismissed = false;
            SaveTasksSoon();
            UpdateStatus();
        });
        DismissFailedBannerCommand = new RelayCommand(_ => IsFailedBannerDismissed = true);
        RefreshLinkCommand = new RelayCommand(_ =>
        {
            if (SelectedTask is not null)
                RefreshLinkRequested?.Invoke(SelectedTask);
        }, _ => SelectedTask is { Status: TaskStatus.Failed or TaskStatus.Paused });

        TasksView = CollectionViewSource.GetDefaultView(Tasks);
        TasksView.Filter = FilterTask;
        TasksView.SortDescriptions.Add(
            new SortDescription(nameof(DownloadTask.AddedAt), ListSortDirection.Descending));

        var orderedKinds = new[]
        {
            FilterKind.All,
            FilterKind.Video,
            FilterKind.Music,
            FilterKind.Document,
            FilterKind.Compressed,
            FilterKind.Program,
        };
        Filters.Add(FilterItem.Header("CATEGORIES"));
        foreach (var kind in orderedKinds)
            Filters.Add(new FilterItem(kind));
        Filters.Add(FilterItem.Separator);
        Filters.Add(FilterItem.Header("VIEWS"));
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

    private bool CanPause => SelectedTask is not null &&
        SelectedTask.Status is TaskStatus.Downloading or TaskStatus.Queued;
    private bool CanStop => SelectedTask is not null &&
        SelectedTask.Status is TaskStatus.Downloading or TaskStatus.Queued or TaskStatus.Paused;
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

    public double SidebarWidth => IsSidebarCollapsed ? 40 : _sidebarWidth;
    public string CollapseIcon => IsSidebarCollapsed ? char.ConvertFromUtf32(0xF0142) : char.ConvertFromUtf32(0xF0141); // Chevron Right / Left
    public string CollapseToolTip => IsSidebarCollapsed ? "Expand Sidebar" : "Collapse Sidebar";

    public bool IsDarkTheme
    {
        get => Settings.UseDarkTheme;
        set
        {
            if (Settings.UseDarkTheme == value)
                return;
            Settings.UseDarkTheme = value;
            ThemeService.Apply(Settings.Theme, value);
            TaskStore.SaveSettings(Settings);
            OnPropertyChanged(nameof(IsDarkTheme));
            OnPropertyChanged(nameof(ThemeButtonIcon));
            OnPropertyChanged(nameof(ThemeButtonLabel));
            OnPropertyChanged(nameof(ThemeButtonToolTip));
        }
    }

    public AppTheme SelectedTheme
    {
        get => Settings.Theme;
        set
        {
            if (Settings.Theme == value)
                return;
            Settings.Theme = value;
            ThemeService.Apply(value, Settings.UseDarkTheme);
            TaskStore.SaveSettings(Settings);
            OnPropertyChanged(nameof(SelectedTheme));
            OnPropertyChanged(nameof(SelectedThemeName));
        }
    }

    public string SelectedThemeName => "Default";

    public IReadOnlyList<AppTheme> AvailableThemes { get; } = new[] { AppTheme.Default };

    /// <summary>Icon of the theme the button switches to: sun for light, contrast for dark.</summary>
    public string ThemeButtonIcon => IsDarkTheme ? char.ConvertFromUtf32(0xF0599) : char.ConvertFromUtf32(0xF0594); // Sunny when dark (switch to light), Night when light
    public string ThemeButtonLabel => IsDarkTheme ? "Light" : "Dark";
    public string ThemeButtonToolTip => IsDarkTheme ? "Switch to light theme" : "Switch to dark theme";

    public void SetSidebarWidth(double width)
    {
        if (IsSidebarCollapsed)
            return;
        _sidebarWidth = Math.Clamp(width, 110, 320);
        OnPropertyChanged(nameof(SidebarWidth));
    }

    public FilterKind SelectedFilter
    {
        get => _filter;
        set
        {
            if (_filter == value)
                return;
            _filter = value;
            OnPropertyChanged(nameof(SelectedFilter));
            OnPropertyChanged(nameof(SearchContextText));
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
            OnPropertyChanged(nameof(SearchContextText));
            TasksView.Refresh();
            UpdateEmptyState();
        }
    }

    public bool HasSearchText => !string.IsNullOrEmpty(_searchText);

    public string SearchContextText => HasSearchText && SelectedFilter != FilterKind.All
        ? $"in {SelectedFilterName}"
        : "";
    private string SelectedFilterName =>
        Filters.FirstOrDefault(f => f.Kind == SelectedFilter && !f.IsHeader && !f.IsSeparator)?.Name ?? SelectedFilter.ToString();

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

    public bool HasNoVisibleTasks { get; private set; } = true;
    public string EmptyStateTitle { get; private set; } = "No downloads yet";
    public string EmptyStateSubtitle { get; private set; } = "Add a URL to start your first download.";

    private bool _isFailedBannerDismissed = false;
    public bool IsFailedBannerDismissed
    {
        get => _isFailedBannerDismissed;
        set
        {
            if (_isFailedBannerDismissed == value)
                return;
            _isFailedBannerDismissed = value;
            OnPropertyChanged(nameof(IsFailedBannerDismissed));
            OnPropertyChanged(nameof(HasFailedTasks));
        }
    }

    public int FailedCount => Tasks.Count(t => t.Status == TaskStatus.Failed);
    public bool HasFailedTasks => !IsFailedBannerDismissed && FailedCount > 0;
    public string FailedCountText => $"{FailedCount} failed download{(FailedCount > 1 ? "s" : "")}";

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
        FilterKind.Queue => task.Status is TaskStatus.Queued or TaskStatus.Downloading,
        FilterKind.Finished => task.Status == TaskStatus.Completed,
        FilterKind.Paused => task.Status == TaskStatus.Paused,
        FilterKind.Failed => task.Status == TaskStatus.Failed,
        _ => true,
    };

    private static bool IsActive(DownloadTask task) =>
        task.Status is TaskStatus.Queued or TaskStatus.Downloading;

    private void LoadPersistedTasks()
    {
        foreach (var record in TaskStore.LoadTasks())
        {
            var task = new DownloadTask(_dispatcher)
            {
                Url = record.Url,
                Referer = record.Referer,
                Headers = record.Headers,
                Mirrors = record.Mirrors?.ToList() ?? new(),
                Etag = record.Etag,
                LastModified = record.LastModified,
                FileName = record.FileName,
                SaveFolder = string.IsNullOrWhiteSpace(record.SaveFolder) ? DownloadTask.DefaultSaveFolder : record.SaveFolder,
                ChunkCount = record.ChunkCount,
                TotalBytes = record.TotalBytes,
                DownloadedBytes = record.DownloadedBytes,
                Progress = record.Progress,
                SpeedLimitKbps = record.SpeedLimitKbps,
                Priority = record.Priority,
                Category = record.Category,
                Checksum = record.Checksum,
                Error = record.Error,
                AddedAt = record.AddedAt == default ? DateTime.Now : record.AddedAt,
                CompletedAt = record.CompletedAt,
            };
            // The engine queue is not persisted, so nothing can ever start a task that
            // was still queued when the app closed. Land those as Paused instead of
            // leaving a dead "Queued" row the user cannot resume.
            task.Status = record.Status is TaskStatus.Downloading or TaskStatus.Queued
                ? TaskStatus.Paused
                : record.Status;
            if (!DownloadEngine.LooksLikeFileName(task.FileName))
                task.FileName = DownloadEngine.DeriveName(record.Url);
            Tasks.Add(task);
        }
    }

    public bool ExistingUrl(string url)
    {
        string needle = url.Trim();
        return Tasks.Any(t => string.Equals(t.Url, needle, StringComparison.OrdinalIgnoreCase));
    }

    public void AddTask(string url, string? fileName = null, string? referer = null, int chunkCount = 0, IEnumerable<string>? mirrors = null)
    {
        if (string.IsNullOrWhiteSpace(url))
            return;
        if (!_dispatcher.CheckAccess())
        {
            _dispatcher.BeginInvoke(() => AddTask(url, fileName, referer, chunkCount, mirrors));
            return;
        }
        var task = new DownloadTask(_dispatcher)
        {
            Url = url.Trim(),
            Referer = referer,
            ChunkCount = Math.Max(0, chunkCount),
            SaveFolder = Settings.DownloadFolder,
            SpeedLimitKbps = 0,
        };
        if (mirrors is not null)
            task.Mirrors = mirrors.Where(m => !string.IsNullOrWhiteSpace(m)).Select(m => m.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (!string.IsNullOrWhiteSpace(fileName))
            task.FileName = DownloadEngine.SanitizeFileName(fileName);
        task.Category = DownloadTask.Categorize(task.FileName);

        Tasks.Add(task);
        ApplyCategoryRouting(task);
        Engine.Start(task);
        SelectedFilter = FilterKind.All;
        SaveTasksSoon();
        UpdateStatus();
        ShowProgressDialogRequested?.Invoke(task);
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
        task.Category = DownloadTask.Categorize(task.FileName);
        Tasks.Add(task);
        ApplyCategoryRouting(task);
        Engine.Start(task);
        SelectedFilter = FilterKind.All;
        SaveTasksSoon();
        UpdateStatus();
        ShowProgressDialogRequested?.Invoke(task);
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

    public void ResumeAll()
    {
        // Resume paused tasks that are not in the engine queue, then resume the queue.
        var paused = Tasks.Where(t => t.Status == TaskStatus.Paused).ToList();
        foreach (var task in paused)
        {
            task.Error = null;
            task.Eta = "";
            Engine.Start(task);
        }
        Engine.ResumeAll();
        SaveTasksSoon();
        UpdateStatus();
    }

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

    /// <summary>Swaps a dead link for a fresh one and resumes the download from its
    /// current progress (see <see cref="DownloadEngine.UpdateLink"/>).</summary>
    public void ApplyLinkRefresh(DownloadTask task, string newUrl)
    {
        Engine.UpdateLink(task, newUrl);
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
        var prompt = new DeletePromptRequest
        {
            Message = deleteFiles
                ? $"Delete \"{task.FileName}\" and its file from disk? This permanently removes the downloaded file."
                : $"Remove \"{task.FileName}\" from the download list?",
            DiskChecked = deleteFiles,
        };
        DeletePromptRequested?.Invoke(prompt);
        if (prompt.DeleteFromDisk is not bool disk)
            return;
        Engine.Remove(task, disk);
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
        int position = Engine.GetQueuePosition(SelectedTask);
        if (position <= 0)
            return false;
        return direction switch
        {
            -1 => position > 1,
            1 => position < Engine.QueuedCount,
            _ => false,
        };
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
        var completed = Tasks.Where(t => t.Status == TaskStatus.Completed).ToList();
        if (completed.Count == 0)
            return;
        var prompt = new DeletePromptRequest
        {
            Message = $"Delete {completed.Count} completed download{(completed.Count == 1 ? "" : "s")}?",
            DiskChecked = false,
        };
        DeletePromptRequested?.Invoke(prompt);
        if (prompt.DeleteFromDisk is not bool disk)
            return;
        foreach (var task in completed)
        {
            Engine.Remove(task, disk);
            Tasks.Remove(task);
        }
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
        if (SelectedTask?.Status != TaskStatus.Completed)
            return;
        if (File.Exists(SelectedTask.FullPath))
        {
            Process.Start(new ProcessStartInfo(SelectedTask.FullPath) { UseShellExecute = true });
        }
        else
        {
            MessageBox.Show(
                $"\"{SelectedTask.FileName}\" is marked as completed, but the file no longer exists at:\n{SelectedTask.FullPath}",
                "File not found",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    public void SaveTasksSoon()
    {
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    public void SaveTasksNow()
    {
        _saveTimer.Stop();
        SaveTasks();
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
        Environment.ProcessPath ?? System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName ?? "";

    private void OnTasksChanged()
    {
        RefreshQueuePositions();
        RefreshFilterCounts();
        CommandManager.InvalidateRequerySuggested();
        OnPropertyChanged(nameof(HasNoTasks));
        UpdateStatus();
        SaveTasksSoon();
    }

    private void RefreshQueuePositions()
    {
        foreach (var task in Tasks)
            task.QueuePosition = Engine.GetQueuePosition(task);
    }

    private void RefreshFilterCounts()
    {
        foreach (var filter in Filters)
        {
            if (filter.IsSeparator || filter.IsHeader)
                continue;
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
        UpdateEmptyState();

        double totalSpeedBps = Engine.TotalSpeedBps;
        SpeedHistory.RemoveAt(0);
        SpeedHistory.Add(totalSpeedBps);
        SpeedHistoryUpdated?.Invoke(SpeedHistory);

        int total = Tasks.Count;
        int completed = Tasks.Count(t => t.Status == TaskStatus.Completed);

        if (active == 0 && queued == 0)
        {
            StatusText = total == 0
                ? "0 downloads · 0 completed"
                : $"{total} downloads · {completed} completed";
            StatusRightText = "Total: 0 B/s";
            return;
        }

        string speed = DownloadTask.FormatBytes((long)totalSpeedBps);
        StatusText = $"{active} active · {speed}/s";
        StatusRightText = $"Total: {speed}/s";
    }

    private void UpdateEmptyState()
    {
        bool hasVisible = false;
        foreach (var item in TasksView)
        {
            hasVisible = true;
            break;
        }
        if (hasVisible == HasNoVisibleTasks)
        {
            HasNoVisibleTasks = !hasVisible;
            OnPropertyChanged(nameof(HasNoVisibleTasks));
        }

        if (Tasks.Count == 0)
        {
            EmptyStateTitle = "No downloads yet";
            EmptyStateSubtitle = "Add a URL to start your first download.";
        }
        else if (hasVisible)
        {
            EmptyStateTitle = "";
            EmptyStateSubtitle = "";
        }
        else if (!string.IsNullOrWhiteSpace(_searchText))
        {
            EmptyStateTitle = "No matching downloads";
            EmptyStateSubtitle = $"Nothing matches \"{_searchText.Trim()}\" in this view.";
        }
        else
        {
            string filterName = Filters.FirstOrDefault(f => f.Kind == SelectedFilter && !f.IsHeader && !f.IsSeparator)?.Name ?? SelectedFilter.ToString();
            EmptyStateTitle = $"No {filterName.ToLowerInvariant()} downloads";
            EmptyStateSubtitle = "Switch to a different category or add a new download.";
        }
        OnPropertyChanged(nameof(EmptyStateTitle));
        OnPropertyChanged(nameof(EmptyStateSubtitle));
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
    public static FilterItem Header(string title) => new(FilterKind.All) { IsHeader = true, HeaderText = title };

    public bool IsSeparator { get; private init; }
    public bool IsHeader { get; private init; }
    public string HeaderText { get; private init; } = "";
    public FilterKind Kind { get; }
    public bool IsCategory => Kind is FilterKind.Video or FilterKind.Music or FilterKind.Document or FilterKind.Compressed or FilterKind.Program;

    public string Icon => (IsSeparator || IsHeader) ? "" : Kind switch
    {
        FilterKind.All => char.ConvertFromUtf32(0xF003B),
        FilterKind.Video => char.ConvertFromUtf32(0xF0381),
        FilterKind.Music => char.ConvertFromUtf32(0xF0387),
        FilterKind.Document => char.ConvertFromUtf32(0xF0219),
        FilterKind.Compressed => char.ConvertFromUtf32(0xF05C4),
        FilterKind.Program => char.ConvertFromUtf32(0xF08C6),
        FilterKind.Queue => char.ConvertFromUtf32(0xF027B),
        FilterKind.Finished => char.ConvertFromUtf32(0xF05E0),
        FilterKind.Paused => char.ConvertFromUtf32(0xF03E4),
        FilterKind.Failed => char.ConvertFromUtf32(0xF0028),
        _ => char.ConvertFromUtf32(0xF003B),
    };

    public string Name => IsSeparator ? "" : IsHeader ? HeaderText : Kind switch
    {
        FilterKind.All => "All downloads",
        FilterKind.Video => "Video",
        FilterKind.Music => "Music",
        FilterKind.Document => "Documents",
        FilterKind.Compressed => "Compressed",
        FilterKind.Program => "Programs",
        FilterKind.Queue => "Queue",
        FilterKind.Finished => "Finished",
        FilterKind.Paused => "Paused",
        FilterKind.Failed => "Failed",
        _ => Kind.ToString(),
    };

    public System.Windows.Media.Brush CategoryBrush =>
        (System.Windows.Media.Brush)System.Windows.Application.Current.Resources[
            Kind switch
            {
                FilterKind.Video => "Brush.CatVideo",
                FilterKind.Music => "Brush.CatMusic",
                FilterKind.Document => "Brush.CatDocument",
                FilterKind.Compressed => "Brush.CatCompressed",
                FilterKind.Program => "Brush.CatProgram",
                _ => "Brush.Text",
            }];

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

/// <summary>Carries a pending destructive delete to the view for confirmation.
/// The view sets <see cref="DeleteFromDisk"/> to the user's choice; null means cancelled.</summary>
public sealed class DeletePromptRequest
{
    public required string Message { get; init; }
    public required bool DiskChecked { get; init; }
    public bool? DeleteFromDisk { get; set; }
}

