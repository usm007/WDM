using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Threading;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using WDM.Models;
using WDM.Services;

namespace WDM.ViewModels;

public sealed class MainViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _saveTimer;
    private readonly Action _onEngineTaskChanged;
    private readonly Action<DownloadTask> _onEngineTaskCompleted;
    private readonly Action<DownloadTask> _onCloudflareBlocked;
    private readonly Action<DownloadTask, string> _onEmbedInteractionRequired;
    private bool _disposed;
    /// <summary>When true, persistence is disabled (screenshot generator /
    /// design-time VM must never overwrite the user's tasks.json).</summary>
    public bool PersistenceSuppressed { get; private set; }
    /// <summary>Tasks currently running the automatic Cloudflare challenge solver window;
    /// prevents opening multiple concurrent solver windows for the same task.</summary>
    private readonly HashSet<Guid> _cfSolving = new();
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
    /// <summary>Called by any dialog right before ApplyAndRestart so the main
    /// window can set _exiting and prevent the MinimizeToTray guard from
    /// cancelling the restart (BUG-038).</summary>
    public Action? PrepareForRestart;
    /// <summary>Static variant so callers that don't have a ViewModel reference
    /// (AboutDialog, OptionsControl, UpdateAvailableDialog) can still signal
    /// the main window before restart.</summary>
    public static Action? Restarting;
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
    public RelayCommand OpenFolderCommand { get; }
    public RelayCommand CleanFileNameCommand { get; }

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
        _onEngineTaskChanged = () => Dispatch(OnTasksChanged);
        _onEngineTaskCompleted = task => Dispatch(() =>
        {
            task.CompletedAt ??= DateTime.Now;
            TaskCompleted?.Invoke(task);
            HandlePostDownload(task);
            MaybeShutdownOnQueueComplete();
            SaveTasksSoon();
        });
        _onCloudflareBlocked = task => Dispatch(() => AutoSolveCloudflare(task));
        _onEmbedInteractionRequired = (task, pageUrl) => Dispatch(() => SolveEmbedInteraction(task, pageUrl));
        Engine.TaskChanged += _onEngineTaskChanged;
        Engine.TaskCompleted += _onEngineTaskCompleted;
        Engine.CloudflareBlocked += _onCloudflareBlocked;
        Engine.EmbedInteractionRequired += _onEmbedInteractionRequired;

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
        BulkResumeCommand = new RelayCommand(_ => BulkDo(t => { if (t.Status is TaskStatus.Paused or TaskStatus.Failed or TaskStatus.Queued) Engine.Start(t); }));
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
        StartQueueCommand = new RelayCommand(_ => ResumeAll());
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
        OpenFolderCommand = new RelayCommand(p => RevealTask(p as DownloadTask ?? SelectedTask));
        CleanFileNameCommand = new RelayCommand(_ => CleanSelectedFileNames(), _ => SelectedTask is not null || SelectedTasks.Count > 0);

        TasksView = CollectionViewSource.GetDefaultView(Tasks);
        TasksView.Filter = FilterTask;
        TasksView.SortDescriptions.Add(
            new SortDescription(nameof(DownloadTask.AddedAt), ListSortDirection.Descending));

        // 1. Views Section (All, Queue, Finished, Paused, Failed)
        Filters.Add(new FilterItem(FilterKind.All));
        Filters.Add(new FilterItem(FilterKind.Queue));
        Filters.Add(new FilterItem(FilterKind.Finished));
        Filters.Add(new FilterItem(FilterKind.Paused));
        Filters.Add(new FilterItem(FilterKind.Failed));

        // Divider between Views and Categories
        Filters.Add(FilterItem.Separator);

        // 2. Categories Section (Video, Music, Document, Compressed, Program)
        Filters.Add(new FilterItem(FilterKind.Video));
        Filters.Add(new FilterItem(FilterKind.Music));
        Filters.Add(new FilterItem(FilterKind.Document));
        Filters.Add(new FilterItem(FilterKind.Compressed));
        Filters.Add(new FilterItem(FilterKind.Program));

        _saveTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(1500),
        };
        _saveTimer.Tick += OnSaveTimerTick;

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
            OnPropertyChanged(nameof(ThemeSymbol));
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
    public Wpf.Ui.Controls.SymbolRegular ThemeSymbol => IsDarkTheme ? Wpf.Ui.Controls.SymbolRegular.WeatherSunny24 : Wpf.Ui.Controls.SymbolRegular.WeatherMoon24;
    public string ThemeButtonLabel => IsDarkTheme ? "Light" : "Dark";
    public string ThemeButtonToolTip => IsDarkTheme ? "Switch to light theme" : "Switch to dark theme";

    private bool _isUpdateAvailable;
    /// <summary>True when the background update check found a newer release. Drives the
    /// top-bar About button to swap to an animated ArrowDownload24 update icon.</summary>
    public bool IsUpdateAvailable
    {
        get => _isUpdateAvailable;
        set
        {
            if (_isUpdateAvailable == value)
                return;
            _isUpdateAvailable = value;
            OnPropertyChanged(nameof(IsUpdateAvailable));
            OnPropertyChanged(nameof(AboutButtonSymbol));
            OnPropertyChanged(nameof(AboutButtonToolTip));
        }
    }

    /// <summary>Pending release surfaced by the background check; opened when the update icon is clicked.</summary>
    public ReleaseInfo? PendingRelease { get; set; }
    public object? PendingVelopack { get; set; }

    public Wpf.Ui.Controls.SymbolRegular AboutButtonSymbol => IsUpdateAvailable
        ? Wpf.Ui.Controls.SymbolRegular.ArrowDownload24
        : Wpf.Ui.Controls.SymbolRegular.Info24;
    public string AboutButtonToolTip => IsUpdateAvailable
        ? "Update available - click to install"
        : "About Windows Download Manager";

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
            if (value == FilterKind.Settings)
            {
                OptionsCommand.Execute(null);
                return;
            }
            if (value == FilterKind.About)
            {
                AboutCommand.Execute(null);
                return;
            }
            if (value == FilterKind.Scheduler || value == FilterKind.SpeedLimits)
            {
                OptionsCommand.Execute(null);
                return;
            }
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

    public int SelectedCount => SelectedTasks.Count > 0 ? SelectedTasks.Count : (SelectedTask != null ? 1 : 0);
    public bool HasNoSelection => SelectedCount == 0;
    public bool HasSingleSelection => SelectedCount == 1;
    public bool HasMultipleSelection => SelectedCount > 1;

    private void UpdateSelectionProperties()
    {
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(HasNoSelection));
        OnPropertyChanged(nameof(HasSingleSelection));
        OnPropertyChanged(nameof(HasMultipleSelection));
        OnPropertyChanged(nameof(BulkCountText));
    }

    public DownloadTask? SelectedTask
    {
        get => _selectedTask;
        set
        {
            _selectedTask = value;
            OnPropertyChanged(nameof(SelectedTask));
            UpdateSelectionProperties();
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
            return count == 0 ? "" : count == 1 ? "1 selected" : $"{count} selected";
        }
    }

    public void SetBulkSelection(IEnumerable<DownloadTask> tasks)
    {
        SelectedTasks.Clear();
        foreach (var t in tasks)
            SelectedTasks.Add(t);
        UpdateSelectionProperties();
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
        FilterKind.Active => task.Status == TaskStatus.Downloading,
        FilterKind.Queue => task.Status == TaskStatus.Queued,
        FilterKind.Finished or FilterKind.History => task.Status == TaskStatus.Completed,
        FilterKind.Paused => task.Status == TaskStatus.Paused,
        FilterKind.Failed => task.Status == TaskStatus.Failed,
        FilterKind.Video => task.Category == DownloadCategory.Video,
        FilterKind.Music => task.Category == DownloadCategory.Music,
        FilterKind.Document => task.Category == DownloadCategory.Document,
        FilterKind.Compressed => task.Category == DownloadCategory.Compressed,
        FilterKind.Program => task.Category == DownloadCategory.Program,
        FilterKind.Other => task.Category == DownloadCategory.Other,
        _ => true,
    };

    private static bool IsActive(DownloadTask task) =>
        task.Status is TaskStatus.Queued or TaskStatus.Downloading;

    private void LoadPersistedTasks()
    {
        foreach (var record in TaskStore.LoadTasks())
        {
            if (string.IsNullOrWhiteSpace(record.Url))
                continue;
            var task = new DownloadTask(_dispatcher)
            {
                Url = record.Url.Trim(),
                SourcePageUrl = record.SourcePageUrl,
                Referer = record.Referer,
                Headers = record.Headers ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                Mirrors = record.Mirrors?.Where(m => !string.IsNullOrWhiteSpace(m)).ToList() ?? new(),
                Etag = record.Etag,
                LastModified = record.LastModified,
                FileName = record.FileName,
                SaveFolder = string.IsNullOrWhiteSpace(record.SaveFolder) ? DownloadTask.DefaultSaveFolder : record.SaveFolder,
                ChunkCount = Math.Clamp(record.ChunkCount, 0, 32),
                TotalBytes = record.TotalBytes,
                DownloadedBytes = Math.Max(0, record.DownloadedBytes),
                Progress = Math.Clamp(record.Progress, 0, 100),
                SpeedLimitKbps = Math.Max(0, record.SpeedLimitKbps),
                Priority = record.Priority,
                Category = record.Category,
                Checksum = record.Checksum,
                Error = record.Error,
                AddedAt = record.AddedAt == default ? DateTime.Now : record.AddedAt,
                CompletedAt = record.CompletedAt,
                IsYouTube = record.IsYouTube || MediaResolver.IsYoutubeUrl(record.Url),
                YouTubeFormatArg = record.YouTubeFormatArg,
                YouTubeExtraArgs = record.YouTubeExtraArgs,
                YouTubeVideoId = record.YouTubeVideoId,
                ThumbnailUrl = record.ThumbnailUrl,
            };
            // Embed-resolved tasks persist the player page alongside the (expiring)
            // direct CDN URL; always restart from the page so the engine resolves a
            // fresh signed link instead of reusing a stale one.
            if (!string.IsNullOrWhiteSpace(task.SourcePageUrl))
                task.Url = task.SourcePageUrl;
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

    public bool IsDuplicateFile(string fileName, string folderPath)
    {
        if (string.IsNullOrWhiteSpace(fileName))
            return false;
        string fullPath = Path.Combine(folderPath, fileName);
        if (File.Exists(fullPath))
            return true;
        return Tasks.Any(t => string.Equals(t.FileName, fileName, StringComparison.OrdinalIgnoreCase) &&
                              string.Equals(t.SaveFolder, folderPath, StringComparison.OrdinalIgnoreCase));
    }

    public string GetNumberedFileName(string fileName, string folderPath)
    {
        if (string.IsNullOrWhiteSpace(fileName))
            fileName = "download";

        string extension = Path.GetExtension(fileName);
        string nameWithoutExt = Path.GetFileNameWithoutExtension(fileName);

        if (fileName.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase))
        {
            extension = ".tar.gz";
            nameWithoutExt = fileName[..^7];
        }
        else if (fileName.EndsWith(".tar.bz2", StringComparison.OrdinalIgnoreCase))
        {
            extension = ".tar.bz2";
            nameWithoutExt = fileName[..^8];
        }

        int counter = 1;
        string candidate = $"{nameWithoutExt} ({counter}){extension}";

        while (IsDuplicateFile(candidate, folderPath))
        {
            counter++;
            candidate = $"{nameWithoutExt} ({counter}){extension}";
        }

        return candidate;
    }

    /// <summary>Automatic solver invoked when the engine reports a Cloudflare block.</summary>
    private void AutoSolveCloudflare(DownloadTask task)
    {
        if (task.Status != TaskStatus.Failed)
            return;
        RunCloudflareSolver(task);
    }

    private void RunCloudflareSolver(DownloadTask task)
    {
        if (!_cfSolving.Add(task.Id))
            return;

        try
        {
            var window = new CloudflareChallengeWindow(task)
            {
                Owner = Application.Current.MainWindow
            };
            if (window.ShowDialog() == true)
            {
                if (!string.IsNullOrWhiteSpace(window.ExtractedCookies))
                {
                    task.Headers["Cookie"] = window.ExtractedCookies;
                }
                if (!string.IsNullOrWhiteSpace(window.ExtractedUserAgent))
                {
                    task.Headers["User-Agent"] = window.ExtractedUserAgent;
                }
                if (!string.IsNullOrWhiteSpace(window.FinalRedirectUrl) && window.FinalRedirectUrl != task.Url)
                {
                    task.Url = window.FinalRedirectUrl;
                }
                task.Status = TaskStatus.Queued;
                task.Error = null;
                Engine.Start(task);
            }
        }
        finally
        {
            _cfSolving.Remove(task.Id);
        }
    }

    /// <summary>Opens the embedded browser when an embed host demands human
    /// interaction. Solved session cookies are replayed, then the task restarts
    /// so the embed resolver runs fresh against the cleared session.</summary>
    private void SolveEmbedInteraction(DownloadTask task, string pageUrl)
    {
        if (task.Status != TaskStatus.Failed)
            return;
        if (!_cfSolving.Add(task.Id))
            return;
        try
        {
            var window = new EmbedInteractionWindow(task, pageUrl)
            {
                Owner = Application.Current.MainWindow
            };
            if (window.ShowDialog() == true)
            {
                if (!string.IsNullOrWhiteSpace(window.ExtractedCookies))
                    task.Headers["Cookie"] = window.ExtractedCookies;
                task.Status = TaskStatus.Queued;
                task.Error = null;
                Engine.Start(task);
            }
        }
        finally
        {
            _cfSolving.Remove(task.Id);
        }
    }

    private void Dispatch(Action action)
    {
        if (_dispatcher.HasShutdownStarted || _dispatcher.HasShutdownFinished)
            return;
        try
        {
            if (_dispatcher.CheckAccess())
                action();
            else
                _dispatcher.BeginInvoke(action);
        }
        catch (InvalidOperationException)
        {
            // Dispatcher was shutting down
        }
    }

    public void AddTask(string url, string? fileName = null, string? referer = null, int chunkCount = 0, IEnumerable<string>? mirrors = null)
    {
        if (string.IsNullOrWhiteSpace(url))
            return;
        if (!_dispatcher.CheckAccess())
        {
            Dispatch(() => AddTask(url, fileName, referer, chunkCount, mirrors));
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
        // BUG-034: A new download is active — cancel any pending shutdown that
        // was scheduled for "queue empty" so the machine doesn't shut down
        // mid-download.
        CancelPendingShutdownIfAny();
        SelectedFilter = FilterKind.All;
        SaveTasksSoon();
        UpdateStatus();
        ShowProgressDialogRequested?.Invoke(task);
    }

    public void AddTask(DownloadTask task)
    {
        if (!_dispatcher.CheckAccess())
        {
            Dispatch(() => AddTask(task));
            return;
        }
        if (string.IsNullOrWhiteSpace(task.FileName))
        {
            task.FileName = DownloadEngine.DeriveName(task.Url);
        }
        else
        {
            task.FileName = DownloadEngine.SanitizeFileName(task.FileName, referer: task.Referer);
        }
        task.Category = DownloadTask.Categorize(task.FileName);
        Tasks.Add(task);
        ApplyCategoryRouting(task);
        if (task.Status != TaskStatus.Paused)
        {
            Engine.Start(task);
            CancelPendingShutdownIfAny();
            ShowProgressDialogRequested?.Invoke(task);
        }
        SelectedFilter = FilterKind.All;
        SaveTasksSoon();
        UpdateStatus();
    }

    /// <summary>Cancels a pending OS shutdown (shutdown /a) — but ONLY when WDM
    /// itself scheduled it (tracked by _shutdownIssuedByWdm). Previously this
    /// ran on every AddTask, aborting even user-scheduled shutdowns that had
    /// nothing to do with WDM.</summary>
    private bool _shutdownIssuedByWdm;
    private void CancelPendingShutdownIfAny()
    {
        if (!_shutdownIssuedByWdm)
            return;
        _shutdownIssuedByWdm = false;
        try { Process.Start("shutdown", "/a"); } catch { }
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
        if (SelectedTasks.Count > 1)
        {
            BulkRemoveCommand.Execute(null);
            return;
        }
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
        // BUG-035: Token caps the checksum computation so it cannot outlive
        // the application shutdown / dispatcher teardown.
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        try
        {
            if (Settings.ComputeChecksum && File.Exists(task.FullPath))
            {
                string path = task.FullPath;
                task.Checksum = await Task.Run(() => ComputeChecksum(path, cts.Token), cts.Token);
                SaveTasksSoon();
            }

            string? script = Settings.PostDownloadScript;
            if (!string.IsNullOrWhiteSpace(script) && File.Exists(task.FullPath))
            {
                string fullScript = Path.GetFullPath(script.Trim());
                if (File.Exists(fullScript) && IsAllowedPostDownloadScript(fullScript))
                {
                    using var proc = Process.Start(new ProcessStartInfo(fullScript)
                    {
                        UseShellExecute = true,
                        Arguments = $"\"{task.FullPath}\"",
                    });
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Checksum timed out — non-fatal.
        }
        catch
        {
            // Best-effort script/checksum handling.
        }
    }

    private static string ComputeChecksum(string path, CancellationToken ct = default)
    {
        using var stream = File.OpenRead(path);
        using var sha = System.Security.Cryptography.SHA256.Create();
        // Chunked so the 5-minute HandlePostDownload token can actually stop
        // the hash mid-file instead of merely abandoning the await while the
        // worker thread hashes a multi-GB file to completion.
        var buf = new byte[81920];
        int n;
        while ((n = stream.Read(buf, 0, buf.Length)) > 0)
        {
            ct.ThrowIfCancellationRequested();
            sha.TransformBlock(buf, 0, n, null, 0);
        }
        sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        return Convert.ToHexString(sha.Hash!);
    }

    private static bool IsRiskyExecutable(string path)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        return ext is ".exe" or ".msi" or ".bat" or ".cmd" or ".ps1" or ".vbs" or ".vbe"
            or ".js" or ".jse" or ".wsf" or ".wsh" or ".lnk" or ".scr" or ".com"
            or ".pif" or ".reg" or ".jar" or ".msc" or ".hta";
    }

    private static bool IsAllowedPostDownloadScript(string fullPath)
    {
        string ext = Path.GetExtension(fullPath).ToLowerInvariant();
        return ext is ".exe" or ".bat" or ".cmd" or ".ps1" or ".py" or ".pyw";
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

    public void RevealTask(DownloadTask? task)
    {
        if (task is null)
            return;
        string? path = task.FullPath;
        if (!string.IsNullOrWhiteSpace(path))
        {
            // tasks.json is hand-editable: a quote in the persisted path would
            // break out of the explorer argument string. Refuse, don't execute.
            if (!IsSafeExplorerPath(path) || !IsSafeExplorerPath(task.SaveFolder))
                return;
            try
            {
                if (File.Exists(path))
                {
                    Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
                }
                else if (Directory.Exists(task.SaveFolder))
                {
                    Process.Start(new ProcessStartInfo("explorer.exe", $"\"{task.SaveFolder}\"") { UseShellExecute = true });
                }
                else
                {
                    Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
                }
            }
            catch { }
        }
        else if (Directory.Exists(task.SaveFolder))
        {
            try
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{task.SaveFolder}\"") { UseShellExecute = true });
            }
            catch { }
        }
    }

    /// <summary>Explorer arguments are quoted with double quotes — a path
    /// containing a quote (only possible via hand-edited tasks.json, since the
    /// UI sanitizes names) must never be interpolated into the command line.</summary>
    private static bool IsSafeExplorerPath(string? path) =>
        !string.IsNullOrWhiteSpace(path) && !path.Contains('"');

    public void RevealSelected() => RevealTask(SelectedTask);

    public void OpenFile(DownloadTask? task = null)
    {
        task ??= SelectedTask;
        if (task == null || task.Status != TaskStatus.Completed)
            return;
        if (File.Exists(task.FullPath))
        {
            if (IsRiskyExecutable(task.FullPath))
            {
                var answer = MessageBox.Show(
                    $"\"{task.FileName}\" is an executable file (.exe / .bat / .cmd / .msi / .ps1).\n\n" +
                    "Running downloaded executables can be dangerous if you do not trust the source.\n\n" +
                    "Are you sure you want to run this file?",
                    "Security warning",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);
                if (answer != MessageBoxResult.Yes)
                    return;
            }
            try
            {
                Process.Start(new ProcessStartInfo(task.FullPath) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"Unable to open \"{task.FileName}\":\n{ex.Message}",
                    "Open failed",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }
        else
        {
            MessageBox.Show(
                $"\"{task.FileName}\" is marked as completed, but the file no longer exists at:\n{task.FullPath}",
                "File not found",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    public void CleanSelectedFileNames()
    {
        var targets = SelectedTasks.Count > 0
            ? SelectedTasks.ToList()
            : (SelectedTask != null ? new List<DownloadTask> { SelectedTask } : new List<DownloadTask>());

        if (targets.Count == 0) return;

        bool changedAny = false;
        int skippedActive = 0, skippedCollision = 0;
        foreach (var task in targets)
        {
            string oldName = task.FileName;
            if (string.IsNullOrWhiteSpace(oldName)) continue;
            // Never rename under a live download: the engine holds an open
            // handle on the old path and the chunk bitmap is path-keyed.
            if (task.Status == TaskStatus.Downloading || task.Status == TaskStatus.Queued)
            {
                skippedActive++;
                continue;
            }
            // An empty/relative SaveFolder would make Path.Combine resolve
            // against the process CWD — refuse instead of touching the
            // wrong directory.
            if (string.IsNullOrWhiteSpace(task.SaveFolder) || !Path.IsPathRooted(task.SaveFolder))
                continue;

            string cleaned = FileNameHelper.CleanVideoFileName(oldName);
            if (string.Equals(oldName, cleaned, StringComparison.Ordinal)) continue;

            string oldPath = Path.Combine(task.SaveFolder, oldName);
            string newPath = Path.Combine(task.SaveFolder, cleaned);
            string oldState = oldPath + ".wdmstate";
            string newState = newPath + ".wdmstate";

            if (File.Exists(oldPath) && !File.Exists(newPath))
            {
                try
                {
                    File.Move(oldPath, newPath);
                    if (File.Exists(oldState) && !File.Exists(newState))
                    {
                        try { File.Move(oldState, newState); } catch { }
                    }
                    // BUG-036: Only update FileName when the file was actually
                    // moved — otherwise FullPath would point at a non-existent
                    // file and Open/Reveal would break.
                    task.FileName = cleaned;
                    task.Category = DownloadTask.Categorize(cleaned);
                    changedAny = true;
                }
                catch
                {
                    // File may be locked by another process or stream.
                    // Leave FileName unchanged so it stays in sync with the
                    // file actually present on disk.
                }
            }
            else if (!File.Exists(oldPath) && !File.Exists(newPath))
            {
                // File already gone (task deleted externally) — update the
                // name anyway so the model stays consistent.
                if (File.Exists(oldState) && !File.Exists(newState))
                {
                    try { File.Move(oldState, newState); } catch { }
                }
                task.FileName = cleaned;
                task.Category = DownloadTask.Categorize(cleaned);
                changedAny = true;
            }
            else
            {
                // Both exist: renaming would clobber the target. Count it so
                // the user gets feedback instead of silent success.
                skippedCollision++;
            }
        }

        if (skippedActive > 0 || skippedCollision > 0)
        {
            MessageBox.Show(
                $"Renamed {targets.Count - skippedActive - skippedCollision} of {targets.Count} file(s)." +
                (skippedActive > 0 ? $"\n{skippedActive} skipped: download in progress." : "") +
                (skippedCollision > 0 ? $"\n{skippedCollision} skipped: a file with the cleaned name already exists." : ""),
                "Clean filenames",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }

        if (changedAny)
        {
            TaskStore.SaveTasks(Tasks);
        }
    }

    public void SuppressPersistence()
    {
        PersistenceSuppressed = true;
        _saveTimer.Stop();
    }

    public void SaveTasksSoon()
    {
        if (PersistenceSuppressed)
            return;
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    public void SaveTasksNow()
    {
        if (PersistenceSuppressed)
            return;
        _saveTimer.Stop();
        SaveTasks();
    }

    private void OnSaveTimerTick(object? sender, EventArgs e)
    {
        _saveTimer.Stop();
        SaveTasks();
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        try { _saveTimer.Tick -= OnSaveTimerTick; } catch { }
        try { _saveTimer.Stop(); } catch { }
        try { Engine.TaskChanged -= _onEngineTaskChanged; } catch { }
        try { Engine.TaskCompleted -= _onEngineTaskCompleted; } catch { }
        try { Engine.CloudflareBlocked -= _onCloudflareBlocked; } catch { }
        try { Engine.EmbedInteractionRequired -= _onEmbedInteractionRequired; } catch { }
    }

    private void SaveTasks()
    {
        if (PersistenceSuppressed)
            return;
        // Always on the UI thread (DispatcherTimer tick or explicit UI call):
        // TaskStore + UpdateStatus touch ObservableCollections bound to the view.
        if (!_dispatcher.CheckAccess())
        {
            _dispatcher.BeginInvoke(SaveTasks);
            return;
        }
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
                key.SetValue(appName, $"\"{exe}\" /minimized");
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

    public int TotalDownloadsCount => Tasks.Count;
    public int CompletedDownloadsCount => Tasks.Count(t => t.Status == TaskStatus.Completed);
    public bool HasActiveDownloads => Engine.ActiveCount > 0 || Engine.QueuedCount > 0;

    private bool _shutdownWhenQueueComplete;
    /// <summary>Global "shutdown when done" flag. Set from any progress dialog's
    /// "Shutdown computer after all active downloads finish" checkbox. It must be
    /// global (not per-dialog) so it survives the dialog being closed and so it
    /// fires only once every active/queued download has finished.</summary>
    public bool ShutdownWhenQueueComplete
    {
        get => _shutdownWhenQueueComplete;
        set
        {
            if (_shutdownWhenQueueComplete != value)
            {
                _shutdownWhenQueueComplete = value;
                OnPropertyChanged(nameof(ShutdownWhenQueueComplete));
                if (value)
                    MaybeShutdownOnQueueComplete();
            }
        }
    }

    /// <summary>Called whenever a task completes (and whenever the flag is turned
    /// on) to cover the "checked the box after everything already finished" case.</summary>
    public void MaybeShutdownOnQueueComplete()
    {
        if (!_shutdownWhenQueueComplete)
            return;
        if (HasActiveDownloads)
            return;
        if (Tasks.Any(t => t.Status == TaskStatus.Downloading || t.Status == TaskStatus.Queued))
            return;
        // Paused tasks are unfinished work — shutting down now would strand
        // them mid-queue. Wait until they are resumed or removed.
        if (Tasks.Any(t => t.Status == TaskStatus.Paused))
            return;
        // Failed tasks still need attention (retry) — don't shut down over them.
        if (Tasks.Any(t => t.Status == TaskStatus.Failed))
            return;
        // One-shot: consume the flag only once the shutdown command is issued,
        // so a failed shutdown.exe keeps the request armed for the next check.
        if (!TriggerSystemShutdown())
            return;
        _shutdownIssuedByWdm = true;
        _shutdownWhenQueueComplete = false;
        OnPropertyChanged(nameof(ShutdownWhenQueueComplete));
    }

    private static bool TriggerSystemShutdown()
    {
        try
        {
            var proc = Process.Start(new ProcessStartInfo("shutdown", "/s /t 60 /c \"WDM: All downloads completed. Shutting down in 60 seconds. Run 'shutdown /a' in a terminal to abort.\"")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
            });
            if (proc is null)
                throw new InvalidOperationException("Could not start the shutdown process.");
            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"WDM could not shut down the computer:\n{ex.Message}\n\nRun 'shutdown /s /t 60' manually, or 'shutdown /a' to abort a pending shutdown.",
                "Shutdown failed",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return false;
        }
    }

    public string StatusPrefixText => HasActiveDownloads
        ? $"{Engine.ActiveCount} active · "
        : $"{TotalDownloadsCount} download{(TotalDownloadsCount == 1 ? "" : "s")} · ";

    public string StatusCompletedText => HasActiveDownloads
        ? $"{DownloadTask.FormatBytes((long)Engine.TotalSpeedBps)}/s"
        : $"{CompletedDownloadsCount} completed";

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

        OnPropertyChanged(nameof(StatusPrefixText));
        OnPropertyChanged(nameof(StatusCompletedText));
        OnPropertyChanged(nameof(HasActiveDownloads));

        if (active == 0 && queued == 0)
        {
            StatusText = total == 0
                ? "0 downloads · 0 completed"
                : $"{total} downloads · {completed} completed";
            StatusRightText = "Total: 0 B/s";
            return;
        }

        string speed = DownloadTask.FormatBytes((long)totalSpeedBps);
        int downloadingCount = Tasks.Count(t => t.Status == TaskStatus.Downloading);
        string countLabel = downloadingCount == 1 ? "1 downloading" : $"{downloadingCount} downloading";
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
    None = -1,

    // Downloads / Views
    All,
    Active,
    Queue,
    Finished,
    Paused,
    Failed,

    // Categories
    Video,
    Music,
    Document,
    Program,
    Compressed,
    Other,

    // Tools
    Scheduler,
    SpeedLimits,
    History,

    // Application
    Settings,
    About,
}

public sealed class FilterItem : INotifyPropertyChanged
{
    private int _count;
    private int _activeCount;

    public FilterItem(FilterKind kind) => Kind = kind;

    public static FilterItem Separator => new(FilterKind.None) { IsSeparator = true };
    public static FilterItem Header(string title) => new(FilterKind.None) { IsHeader = true, HeaderText = title };

    public bool IsSeparator { get; private init; }
    public bool IsHeader { get; private init; }
    public string HeaderText { get; private init; } = "";
    public FilterKind Kind { get; }
    public bool IsCategory => Kind is FilterKind.Video or FilterKind.Music or FilterKind.Document or FilterKind.Compressed or FilterKind.Program or FilterKind.Other;
    public bool IsToolOrApp => Kind is FilterKind.Scheduler or FilterKind.SpeedLimits or FilterKind.Settings or FilterKind.About;

    public string Icon => (IsSeparator || IsHeader) ? "" : Kind switch
    {
        FilterKind.All => char.ConvertFromUtf32(0xF003B),
        FilterKind.Active => char.ConvertFromUtf32(0xF040A),
        FilterKind.Queue => char.ConvertFromUtf32(0xF027B),
        FilterKind.Finished => char.ConvertFromUtf32(0xF05E0),
        FilterKind.Paused => char.ConvertFromUtf32(0xF03E4),
        FilterKind.Failed => char.ConvertFromUtf32(0xF0028),
        FilterKind.Video => char.ConvertFromUtf32(0xF0381),
        FilterKind.Music => char.ConvertFromUtf32(0xF0387),
        FilterKind.Document => char.ConvertFromUtf32(0xF0219),
        FilterKind.Compressed => char.ConvertFromUtf32(0xF05C4),
        FilterKind.Program => char.ConvertFromUtf32(0xF08C6),
        FilterKind.Other => char.ConvertFromUtf32(0xF0168),
        FilterKind.Scheduler => char.ConvertFromUtf32(0xF0150),
        FilterKind.SpeedLimits => char.ConvertFromUtf32(0xF04F2),
        FilterKind.History => char.ConvertFromUtf32(0xF02DA),
        FilterKind.Settings => char.ConvertFromUtf32(0xF08BB),
        FilterKind.About => char.ConvertFromUtf32(0xF02FD),
        _ => char.ConvertFromUtf32(0xF003B),
    };

    public Wpf.Ui.Controls.SymbolRegular Symbol => (IsSeparator || IsHeader) ? Wpf.Ui.Controls.SymbolRegular.Empty : Kind switch
    {
        FilterKind.All => Wpf.Ui.Controls.SymbolRegular.Apps24,
        FilterKind.Active => Wpf.Ui.Controls.SymbolRegular.Play24,
        FilterKind.Queue => Wpf.Ui.Controls.SymbolRegular.ArrowDownload24,
        FilterKind.Finished => Wpf.Ui.Controls.SymbolRegular.CheckmarkCircle24,
        FilterKind.Paused => Wpf.Ui.Controls.SymbolRegular.PauseCircle24,
        FilterKind.Failed => Wpf.Ui.Controls.SymbolRegular.DismissCircle24,
        FilterKind.Video => Wpf.Ui.Controls.SymbolRegular.Video24,
        FilterKind.Music => Wpf.Ui.Controls.SymbolRegular.MusicNote224,
        FilterKind.Document => Wpf.Ui.Controls.SymbolRegular.Document24,
        FilterKind.Compressed => Wpf.Ui.Controls.SymbolRegular.FolderZip24,
        FilterKind.Program => Wpf.Ui.Controls.SymbolRegular.AppGeneric24,
        FilterKind.Other => Wpf.Ui.Controls.SymbolRegular.DocumentBulletList24,
        FilterKind.Scheduler => Wpf.Ui.Controls.SymbolRegular.Clock24,
        FilterKind.SpeedLimits => Wpf.Ui.Controls.SymbolRegular.Gauge24,
        FilterKind.History => Wpf.Ui.Controls.SymbolRegular.History24,
        FilterKind.Settings => Wpf.Ui.Controls.SymbolRegular.Settings24,
        FilterKind.About => Wpf.Ui.Controls.SymbolRegular.Info24,
        _ => Wpf.Ui.Controls.SymbolRegular.Folder24,
    };

    public string Name => IsSeparator ? "" : IsHeader ? HeaderText : Kind switch
    {
        FilterKind.All => "All",
        FilterKind.Active => "Active",
        FilterKind.Queue => "Queue",
        FilterKind.Finished => "Finished",
        FilterKind.Paused => "Paused",
        FilterKind.Failed => "Failed",
        FilterKind.Video => "Video",
        FilterKind.Music => "Music",
        FilterKind.Document => "Documents",
        FilterKind.Compressed => "Compressed",
        FilterKind.Program => "Programs",
        FilterKind.Other => "Other",
        FilterKind.Scheduler => "Scheduler",
        FilterKind.SpeedLimits => "Speed Limits",
        FilterKind.History => "History",
        FilterKind.Settings => "Settings",
        FilterKind.About => "About",
        _ => Kind.ToString(),
    };

    public System.Windows.Media.Brush CategoryBrush
    {
        get
        {
            // Resources[] throws on a missing key (and Application.Current can
            // be null in design/test hosts) — either would take down the whole
            // sidebar render. Fall back to a plain gray like DownloadTask does.
            try
            {
                string key = Kind switch
                {
                    FilterKind.Video => "Brush.CatVideo",
                    FilterKind.Music => "Brush.CatMusic",
                    FilterKind.Document => "Brush.CatDocument",
                    FilterKind.Compressed => "Brush.CatCompressed",
                    FilterKind.Program => "Brush.CatProgram",
                    FilterKind.Finished => "Brush.StatusComplete",
                    FilterKind.Paused => "Brush.StatusPaused",
                    FilterKind.Failed => "Brush.StatusFailed",
                    _ => "Brush.TextDim",
                };
                var found = System.Windows.Application.Current?.TryFindResource(key)
                    as System.Windows.Media.Brush;
                if (found is not null)
                    return found;
            }
            catch { }
            return System.Windows.Media.Brushes.Gray;
        }
    }

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
        IsToolOrApp || Count == 0 ? ""
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

