using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using WDM.Models;
using WDM.Services;
using WDM.ViewModels;

namespace WDM;

public partial class MainWindow : Wpf.Ui.Controls.FluentWindow
{
    private readonly MainViewModel _viewModel;
    private readonly CaptureServer _captureServer;
    private readonly TrayIcon _tray;
    private readonly System.Windows.Threading.DispatcherTimer _trayTimer;
    private readonly Dictionary<Guid, Window> _openDialogs = new();
    private TrayProgressPanel? _progressPanel;
    private bool _exiting;
    private DownloadCompleteDialog? _completeDialog;
    private readonly Queue<DownloadTask> _completedTasksQueue = new();
    private RefreshLinkDialog? _activeRefreshDialog;

    public MainWindow()
    {
        InitializeComponent();
        _viewModel = new MainViewModel();
        DataContext = _viewModel;

        _viewModel.AddTaskRequested += _ => _dispatcher.BeginInvoke(() => ShowAddDialog());
        _viewModel.EditTaskRequested += task => _dispatcher.BeginInvoke(() => ShowProperties(task));
        _viewModel.OptionsRequested += () => _dispatcher.BeginInvoke(ShowOptions);
        _viewModel.AboutRequested += () => _dispatcher.BeginInvoke(ShowAbout);
        _viewModel.ShowProgressDialogRequested += task => _dispatcher.BeginInvoke(() => ShowProgressDialog(task));
        _viewModel.RefreshLinkRequested += task => _dispatcher.BeginInvoke(() => ShowRefreshLink(task));
        // Delete confirmation is request/response: the command reads the result
        // synchronously right after invoking. BeginInvoke (async) always left
        // the result null, so every prompted delete silently aborted ("delete
        // is not working"). Invoke blocks the caller until the dialog closes.
        _viewModel.DeletePromptRequested += req => _dispatcher.Invoke(() => ShowDeletePrompt(req));
        _viewModel.SpeedHistoryUpdated += history => _dispatcher.BeginInvoke(() => RenderSparkline(history));
        // BUG-038: dialogs call ApplyAndRestart without access to _exiting;
        // the delegate lets them signal the window to disable MinimizeToTray.
        _viewModel.PrepareForRestart = OnRestarting;
        MainViewModel.Restarting += OnRestarting;

        SettingsContent.CloseRequested += (_, _) => ShowDownloadsView();
        ExtensionContent.DoneRequested += (_, _) => ShowDownloadsView();
        NoticeContent.CloseRequested += (_, _) => ShowDownloadsView();

        _viewModel.TaskCompleted += task =>
        {
            var s = _viewModel.Settings;
            if (task.RemuxSkippedNoFfmpeg)
            {
                // Conversion wanted but ffmpeg isn't installed: the file stays
                // .ts. Say so plainly (balloon + log) and point at the download
                // spot — never silently skip the conversion the user asked for.
                const string how = "Options > YouTube & Media";
                ActivityLog.Write("REMUX-SKIP", $"{task.FileName} kept as .TS — FFmpeg is not installed ({how}).");
                if (s.NotifyOnCompletion)
                    _tray?.ShowBalloon(task.FileName, $"Kept as .TS: FFmpeg is missing. Get MP4/MKV conversion in {how}.");
            }
            else if (s.NotifyOnCompletion)
            {
                if (s.DetailedNotifications)
                    _tray?.ShowBalloon(task.FileName, "Download complete.");
                else
                    _tray?.ShowBalloon("Done", $"{task.FileName} is ready.");
            }
            if (s.NotificationSound)
                MainViewModel.PlayNotificationSound(isError: false);

            // Show completion dialog, queueing subsequent completions if one is already open.
            _dispatcher.BeginInvoke(() =>
            {
                if (_completeDialog is not null)
                {
                    _completedTasksQueue.Enqueue(task);
                    return;
                }
                ShowNextCompleteDialog(task);
            });
        };
        _viewModel.NotificationRequested += (task, kind) =>
        {
            var s = _viewModel.Settings;
            switch (kind)
            {
                case ViewModels.NotifyKind.Added:
                    _tray?.ShowBalloon("Added", $"{task.FileName} queued for download.");
                    break;
                case ViewModels.NotifyKind.Started:
                    _tray?.ShowBalloon("Started", $"{task.FileName} started downloading.");
                    break;
                case ViewModels.NotifyKind.Failed:
                    if (s.DetailedNotifications)
                        _tray?.ShowBalloon(task.FileName, $"Failed: {task.Error ?? "unknown error"}");
                    else
                        _tray?.ShowBalloon("Failed", $"{task.FileName}: {task.Error ?? "unknown error"}");
                    if (s.NotificationSound)
                        MainViewModel.PlayNotificationSound(isError: true);
                    break;
            }
        };

        _captureServer = new CaptureServer((url, name, referer, headers, pageTitle) =>
            _dispatcher.BeginInvoke(() =>
            {
                if (_activeRefreshDialog != null && _activeRefreshDialog.IsLoaded)
                {
                    _activeRefreshDialog.OnLinkCaptured(url, headers);
                    return;
                }
                ShowAddDialog(url, name, referer, headers, fromCapture: true, pageTitle: pageTitle);
            }));
        // Structured capture (POST bodies + proxy descriptors) rides alongside
        // the legacy tuple delegate (kept for compat); the dialog prefers it.
        _captureServer.OnCaptureItem = item => _dispatcher.BeginInvoke(() =>
        {
            if (_activeRefreshDialog != null && _activeRefreshDialog.IsLoaded)
            {
                _activeRefreshDialog.OnLinkCaptured(item.Url, item.Headers);
                return;
            }
            ShowAddDialog(item.Url, item.FileName, item.Referer, item.Headers,
                fromCapture: true, pageTitle: item.PageTitle,
                postData: item.PostData, postContentType: item.PostContentType,
                proxyHost: item.ProxyHost, proxyPort: item.ProxyPort, proxyType: item.ProxyType,
                fullSession: item.FullSession);
        });
        // Loopback listener bind happens off the UI thread so slow/busy boot
        // networking can never delay first paint. Callers marshal via dispatcher.
        _ = Task.Run(() => _captureServer.Start());
        _captureServer.OnBatchCapture = items => _dispatcher.BeginInvoke(() => ShowBatchAddDialog(items));
        _captureServer.OnBlobCaptured = result => _dispatcher.Invoke(() => _viewModel.AddCompletedFile(result));
        _captureServer.MinCatchBytesProvider = () => _viewModel.Settings.MinCatchSizeBytes;
        // Third-party automation (gap 8): Invoke (not BeginInvoke) — the
        // loopback thread needs results. Safe: the UI thread never blocks on
        // the server, so this can't deadlock.
        _captureServer.OnApiListTasks = () => _dispatcher.Invoke(() => _viewModel.ApiListTasks());
        _captureServer.OnApiAddTask = req => _dispatcher.Invoke(() => _viewModel.ApiAddTask(req));
        _captureServer.OnApiCommand = (id, cmd) => _dispatcher.Invoke(() => _viewModel.ApiCommand(id, cmd));
        _viewModel.RunStartupMaintenance();

        _tray = new TrayIcon();
        _tray.Activated += () => _dispatcher.BeginInvoke(RestoreWindow);
        _tray.NewDownloadRequested += () => _dispatcher.BeginInvoke(() => ShowAddDialog());
        _tray.PauseAllRequested += () => _dispatcher.BeginInvoke(() => _viewModel.Engine.PauseAll());
        _tray.ResumeAllRequested += () => _dispatcher.BeginInvoke(() => _viewModel.ResumeAll());
        _tray.ExitRequested += () => _dispatcher.BeginInvoke(ExitApp);

        App.SecondInstanceHandler = args => _dispatcher.BeginInvoke(() => HandleSecondInstance(args));

        // List shortcut keys (Delete/Enter/Space/F5) fall back to the window
        // when focused outside the DataGrid, without interfering with text controls.
        KeyDown += OnWindowKeyDown;

        // Adaptive layout: at/above the wide threshold the download list gains
        // Status/Speed/ETA columns and the side inspector appears. Uses the
        // live DataContext (not the ctor VM) so screenshot VMs work too.
        SizeChanged += (_, _) =>
        {
            if (DataContext is MainViewModel vm)
                vm.IsWideLayout = ActualWidth >= MainViewModel.WideLayoutThreshold;
        };

        _trayTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1),
        };
        _trayTimer.Tick += (_, _) =>
        {
            int pausedCount = _viewModel.Tasks.Count(t => t.Status == Models.TaskStatus.Paused);
            int queuedCount = _viewModel.Engine.QueuedCount;
            var downloading = _viewModel.Tasks.Where(t => t.Status == Models.TaskStatus.Downloading).ToList();
            if (downloading.Count > 0)
            {
                var active = downloading[0];
                int avg = (int)Math.Round(downloading.Average(t => t.Progress));
                _tray.SetProgress(avg, "", active.FileName ?? "", queuedCount, pausedCount, downloading.Count);
                UpdateProgressPanel(_viewModel.Settings.ShowTrayProgress ? active : null);
            }
            else
            {
                _tray.SetActiveCount(
                    _viewModel.Engine.ActiveCount, queuedCount, _viewModel.Engine.TotalSpeedBps, pausedCount);
                UpdateProgressPanel(null);
            }
        };
        _trayTimer.Start();

        Loaded += (_, _) =>
        {
            if (App.IsTestMode)
            {
                return;
            }
            // NOTE: extension deploy runs once in App.OnStartup on a background
            // thread — never re-deploy synchronously here; it blocked first paint.
            if (!_captureServer.IsConnected && !_viewModel.Settings.HasPromptedExtensionInstall && !App.StartMinimized)
            {
                _viewModel.Settings.HasPromptedExtensionInstall = true;
                _viewModel.PersistSettings();
                _dispatcher.BeginInvoke(() => ShowExtensionInstallerDialog());
            }
            else if (!App.StartMinimized)
            {
                // Check if application was updated to a newer version.
                // Prompt user to reload Chromium browser extensions so latest version loads.
                // InstallState also covers the wiped-data case: install evidence alone
                // still counts as an update, so updaters get this notice — never Welcome.
                string currentVer = UpdateChecker.CurrentVersion.ToString();
                string? lastVer = _viewModel.Settings.LastRunVersion;
                bool isUpdate = InstallState.IsUpdate(_viewModel.Settings, currentVer);
                // Stale deployed copy (corrupt/rolled-back deploy dir) needs the
                // same reload even when the app version didn't change. A missing
                // copy just means the background deploy hasn't finished — not stale.
                bool staleDeploy = false;
                try
                {
                    string manifest = System.IO.Path.Combine(BrowserIntegration.DeployDir, "manifest.json");
                    staleDeploy = System.IO.File.Exists(manifest) && !BrowserIntegration.IsDeployedCurrent();
                }
                catch { }
                if (isUpdate || staleDeploy)
                {
                    string displayOld = string.IsNullOrWhiteSpace(lastVer) ? "previous" : lastVer;
                    _dispatcher.BeginInvoke(() => ShowExtensionReloadNotice(displayOld, currentVer));
                }
            }

            // Version stamp + settings flush off the UI thread: disk + registry
            // writes must not sit between Show() and first render (white window).
            string currentStamp = UpdateChecker.CurrentVersion.ToString();
            _ = Task.Run(() =>
            {
                try
                {
                    _viewModel.Settings.LastRunVersion = currentStamp;
                    _viewModel.PersistSettings();
                }
                catch { }
            });

            // Started via the Windows-startup shortcut: run in the background and only
            // surface the window when the user clicks the tray icon.
            if (App.StartMinimized)
            {
                Hide();
                var active = _viewModel.Tasks.FirstOrDefault(t => t.Status == Models.TaskStatus.Downloading);
                if (active is not null)
                    UpdateProgressPanel(_viewModel.Settings.ShowTrayProgress ? active : null);
            }

            // Background update check (once a day, tray balloon when a release exists).
            if (_viewModel.Settings.CheckForUpdates)
                _ = CheckForUpdatesAsync();
        };
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        ThemeService.ApplyTitleBar(this);
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);

        // Escape when on Settings / Extension / Notice view: return to Downloads
        if (e.Key == Key.Escape && (SettingsView.Visibility == Visibility.Visible || ExtensionView.Visibility == Visibility.Visible || NoticeView.Visibility == Visibility.Visible))
        {
            ShowDownloadsView();
            e.Handled = true;
            return;
        }

        // Ctrl+F: Fast jump & focus into Search Box for 100+ downloads power users
        if (e.Key == Key.F && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
        {
            if (SettingsView.Visibility == Visibility.Visible || ExtensionView.Visibility == Visibility.Visible || NoticeView.Visibility == Visibility.Visible)
                ShowDownloadsView();
            SearchBox.Focus();
            SearchBox.SelectAll();
            e.Handled = true;
            return;
        }

        // Escape while searching: clear filter and return focus to TaskGrid
        if (e.Key == Key.Escape && SearchBox.IsFocused)
        {
            if (!string.IsNullOrEmpty(SearchBox.Text))
            {
                SearchBox.Text = "";
            }
            TaskGrid.Focus();
            e.Handled = true;
        }
    }

    private System.Windows.Threading.Dispatcher _dispatcher =>
        this.Dispatcher;

    private static void RenderCanvasSparkline(Canvas canvas, List<double> history)
    {
        canvas.Children.Clear();
        if (history.Count < 2) return;

        double width = canvas.ActualWidth > 0 ? canvas.ActualWidth : 80;
        double height = canvas.ActualHeight > 0 ? canvas.ActualHeight : 14;
        double max = history.Max();
        if (max <= 0) max = 1;

        double step = width / (history.Count - 1);
        var points = new PointCollection();
        for (int i = 0; i < history.Count; i++)
        {
            double x = i * step;
            double y = height - (history[i] / max * (height - 4)) - 2;
            points.Add(new Point(x, y));
        }

        var polyline = new Polyline
        {
            Points = points,
            Stroke = (Brush?)Application.Current?.Resources["Brush.Accent"] ?? Brushes.DodgerBlue,
            StrokeThickness = 1.5,
            StrokeLineJoin = PenLineJoin.Round
        };
        canvas.Children.Add(polyline);
    }

    private void RenderSparkline(List<double> history)
    {
        RenderCanvasSparkline(SparklineCanvas, history);
    }

    private AddDownloadDialog? _activeAddDialog;

    private void ShowAddDialog(string? prefillUrl = null, string? prefillFileName = null, string? prefillReferer = null, Dictionary<string, string>? prefillHeaders = null, bool fromCapture = false, string? pageTitle = null,
        string? postData = null, string? postContentType = null, string? proxyHost = null, int proxyPort = 0, string? proxyType = null, bool fullSession = false)
    {
        string targetFolder = _viewModel.Settings.DownloadFolder;
        string rawName = prefillFileName ?? (!string.IsNullOrWhiteSpace(prefillUrl) ? DownloadEngine.DeriveName(prefillUrl) : "");
        string initialFileName = DownloadEngine.SanitizeFileName(rawName, pageTitle, prefillReferer);

        // When a link is captured from the browser extension, show the dialog on
        // top of every window without surfacing the main WDM window.
        if (!fromCapture)
            RestoreWindow();

        if (_activeAddDialog is not null && _activeAddDialog.IsLoaded)
        {
            if (_activeAddDialog.IsEmpty)
            {
                _activeAddDialog.UpdatePrefill(prefillUrl, initialFileName, prefillReferer, prefillHeaders, postData, postContentType, proxyHost, proxyPort, proxyType, fullSession);
                _activeAddDialog.Topmost = fromCapture;
                _activeAddDialog.Activate();
                return;
            }
        }

        var dialog = new AddDownloadDialog(_viewModel, prefillUrl, initialFileName, prefillReferer, prefillHeaders, postData, postContentType, proxyHost, proxyPort, proxyType, fullSession)
        {
            Topmost = fromCapture,
        };
        if (_activeAddDialog is null)
        {
            _activeAddDialog = dialog;
            dialog.Closed += (_, _) =>
            {
                if (ReferenceEquals(_activeAddDialog, dialog))
                    _activeAddDialog = null;
            };
        }
        dialog.Show();
    }

    /// <summary>Batch capture checklist (1DM multi-post dialog equivalent):
    /// checked links become downloads via a single batched add.</summary>
    private void ShowBatchAddDialog(List<Services.CaptureServer.BatchCaptureItem> items)
    {
        if (items is null || items.Count == 0)
            return;
        var dialog = new BatchAddDialog(items) { Owner = this };
        if (dialog.ShowDialog() == true)
        {
            var selected = dialog.GetSelected();
            if (selected.Count > 0)
                _viewModel.AddTasks(selected);
        }
    }

    private void ShowNextCompleteDialog(DownloadTask task)
    {
        var dialog = new DownloadCompleteDialog(task);
        _completeDialog = dialog;
        dialog.Closed += (_, _) =>
        {
            _completeDialog = null;
            if (_completedTasksQueue.Count > 0)
            {
                var next = _completedTasksQueue.Dequeue();
                ShowNextCompleteDialog(next);
            }
        };
        dialog.Show();
    }

    private void ShowProperties(DownloadTask? task)
    {
        if (task is null)
            return;
        var dialog = new TaskPropertiesDialog(task, _viewModel);
        dialog.ShowDialog();
    }

    private void ShowRefreshLink(DownloadTask task)
    {
        var dialog = new RefreshLinkDialog(task) { Owner = this };
        _activeRefreshDialog = dialog;
        try
        {
            if (dialog.ShowDialog() == true)
            {
                if (dialog.CapturedHeaders is not null && dialog.CapturedHeaders.Count > 0)
                {
                    foreach (var kv in dialog.CapturedHeaders)
                        task.Headers[kv.Key] = kv.Value;
                }
                _viewModel.ApplyLinkRefresh(task, dialog.NewUrl);
            }
        }
        finally
        {
            _activeRefreshDialog = null;
        }
    }

    private void ShowDeletePrompt(DeletePromptRequest prompt)
    {
        var dialog = new DeleteConfirmDialog(prompt.Message, prompt.DiskChecked) { Owner = this };
        if (dialog.ShowDialog() == true)
            prompt.DeleteFromDisk = dialog.DeleteFromDisk;
    }

    private void Root_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) || e.Data.GetDataPresent(DataFormats.Text)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void Root_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop) && e.Data.GetData(DataFormats.FileDrop) is string[] files)
        {
            foreach (string file in files)
                AddUrlFromFile(file);
        }
        else if (e.Data.GetDataPresent(DataFormats.Text) && e.Data.GetData(DataFormats.Text) is string text)
        {
            TryAddUrl(text);
        }
        e.Handled = true;
    }

    private void AddUrlFromFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                // Cap the read: a dropped multi-GB file must not be buffered
                // into memory just to extract a URL.
                const long maxDropBytes = 1 * 1024 * 1024;
                if (new FileInfo(path).Length > maxDropBytes)
                    return;
                string content = File.ReadAllText(path);
                if (content.Length > maxDropBytes)
                    content = content[..(int)maxDropBytes];
                // .url shortcut files are INI ([InternetShortcut] URL=...),
                // not raw URLs — parse the URL= line instead of failing silently.
                string? shortcutUrl = null;
                if (path.EndsWith(".url", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var line in content.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
                    {
                        string t = line.Trim();
                        if (t.StartsWith("URL=", StringComparison.OrdinalIgnoreCase))
                        {
                            shortcutUrl = t[4..].Trim();
                            break;
                        }
                    }
                }
                string url = (shortcutUrl ?? content).Trim().Trim('[', ']', '"', '\'', ';', ' ');
                TryAddUrl(url);
            }
        }
        catch
        {
            // Ignore unreadable drops.
        }
    }

    private void TryAddUrl(string text)
    {
        string trimmed = text.Trim();
        if (DownloadEngine.IsHttpUrl(trimmed))
        {
            // Drag-drop path bypasses the Add dialog's duplicate check — apply it here.
            if (_viewModel.ExistingUrl(trimmed))
                return;
            _viewModel.AddTask(trimmed);
        }
    }

    private void ShowOptions()
    {
        try
        {
            if (SettingsView.Visibility == Visibility.Visible)
            {
                ShowDownloadsView();
                return;
            }

            DownloadsView.Visibility = Visibility.Collapsed;
            ExtensionView.Visibility = Visibility.Collapsed;
            NoticeView.Visibility = Visibility.Collapsed;
            SettingsView.Visibility = Visibility.Visible;
            SettingsContent.Initialize(_viewModel);
        }
        catch (Exception ex)
        {
            App.LogException(ex);
            ErrorDialogs.ShowError(this, "Settings", "The Settings page couldn't be opened. Please try again.");
        }
    }

    /// <summary>Tray relaunch / second-instance restore: the main (downloads)
    /// window always appears, no matter which view was open when the window
    /// was closed to the tray. An already-visible window is only focused —
    /// its current view is left alone.</summary>
    private void RestoreWindow()
    {
        if (Visibility != Visibility.Visible)
        {
            Show();
            ShowDownloadsView();
        }
        WindowState = WindowState.Normal;
        Activate();
        UpdateProgressPanel(null);
    }

    /// <summary>Second-instance handoff (see <see cref="Services.SingleInstancePipe"/>):
    /// restores a possibly tray-hidden window and opens any forwarded URL.
    /// Bare-flag handoffs (boot duplicate /minimized, no URL) are ignored so a
    /// silent autostart can never pop the main window.</summary>
    public void HandleSecondInstance(string[] args)
    {
        if (args is { Length: > 0 } && !App.ArgsContainUrl(args))
            return;
        RestoreWindow();
        try
        {
            string? url = args?.Select(a => App.FirstDownloadLink(a)).FirstOrDefault(u => !string.IsNullOrWhiteSpace(u));
            if (!string.IsNullOrWhiteSpace(url))
                ShowAddDialog(prefillUrl: url.Trim());
        }
        catch { }
    }

    /// <summary>Shows the always-on-top tray progress panel only while the main
    /// window is hidden to the tray, a download is running, and the option is on.</summary>
    private void UpdateProgressPanel(DownloadTask? active)
    {
        bool show = _viewModel.Settings.ShowTrayProgress &&
                    Visibility != Visibility.Visible &&
                    active is not null;

        if (!show)
        {
            _progressPanel?.HidePanel();
            return;
        }

        // A user-closed (Alt+F4) panel can't be re-Shown — recreate it.
        // The panel unsubscribes itself in OnClosed, so dropping the
        // reference is leak-free.
        if (_progressPanel is not null && !_progressPanel.IsLoaded)
            _progressPanel = null;
        if (_progressPanel is null)
        {
            var panel = new TrayProgressPanel(_viewModel);
            panel.Closed += (_, _) =>
            {
                if (ReferenceEquals(_progressPanel, panel))
                    _progressPanel = null;
            };
            _progressPanel = panel;
        }
        _progressPanel.ShowPanel(active!);
    }

    private void ExitApp()
    {
        _exiting = true;
        Close();
    }

    /// <summary>Checks for updates: Velopack delta first (patch-only, ~2MB), then GitHub full installer as fallback.
    /// Icon-only: never pops a dialog — sets the animated update icon on the top-bar About button instead.
    /// Clicking that icon opens the update dialog (AboutDialog inline panel).</summary>
    private async Task CheckForUpdatesAsync()
    {
        var settings = _viewModel.Settings;
        DateTime? lastCheck = DateTime.TryParse(settings.LastUpdateCheckUtc, null, System.Globalization.DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed
            : null;
        if (lastCheck is not null && DateTime.UtcNow - lastCheck < TimeSpan.FromMinutes(15))
            return;

        // 1) Velopack → nupkg only (delta if 1 behind, self-contained full if 2+ behind).
        // Never runs Setup.exe (new users only) which shows the "already installed" dialog.
        if (VelopackUpdateService.IsVelopackInstalled)
        {
            try
            {
                var velopackUpdate = await VelopackUpdateService.CheckForUpdatesAsync();
                if (velopackUpdate == null)
                    velopackUpdate = await VelopackUpdateService.CheckForUpdatesAnyAsync();
                if (velopackUpdate is not null)
                {
                    settings.LastUpdateCheckUtc = DateTime.UtcNow.ToString("O");
                    _viewModel.PersistSettings();
                    // Automatic install option: download + restart without asking (Velopack only).
                    if (settings.AutoDownloadUpdates)
                    {
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                await VelopackUpdateService.DownloadUpdatesAsync(velopackUpdate, null);
                                _ = _dispatcher.BeginInvoke(() => VelopackUpdateService.ApplyAndRestart(velopackUpdate.TargetFullRelease));
                            }
                            catch { }
                        });
                        return;
                    }
                    var semVer = velopackUpdate.TargetFullRelease.Version;
                    var target = VelopackUpdateService.ToSystemVersion(semVer);
                    bool isDelta = VelopackUpdateService.IsDeltaUpdate(velopackUpdate);
                    string desc = VelopackUpdateService.DescribeUpdate(velopackUpdate);
                    var synthetic = new ReleaseInfo($"v{target}", target, $"WDM {target}", $"https://github.com/usm007/WDM/releases/tag/v{target}", $"{desc} update to {target} ({(isDelta ? "1 behind, patch-only" : "2+ behind, .NET included")}).", DateTime.UtcNow, null);
                    _ = _dispatcher.BeginInvoke(() =>
                    {
                        try
                        {
                            _viewModel.PendingRelease = synthetic;
                            _viewModel.PendingVelopack = velopackUpdate;
                            _viewModel.IsUpdateAvailable = true;
                        }
                        catch { }
                    });
                    return;
                }
                // No delta — fall back to GitHub full installer so the About icon still offers Download & Install
                try
                {
                    var fallback = await UpdateChecker.CheckLatestAsync();
                    settings.LastUpdateCheckUtc = DateTime.UtcNow.ToString("O");
                    _viewModel.PersistSettings();
                    if (fallback?.Version is not null && fallback.Version.CompareTo(UpdateChecker.CurrentVersion) > 0)
                    {
                        // Nupkg-only release (no Setup.exe yet): attach Velopack info now so the About
                        // dialog offers Download & Install (same as Settings). Setup.exe is new-users only.
                        object? pendingVelo = null;
                        if (string.IsNullOrWhiteSpace(fallback.InstallerUrl) && !string.IsNullOrWhiteSpace(fallback.UpdatePackageUrl))
                        {
                            try
                            {
                                var any = await VelopackUpdateService.CheckForUpdatesAsync()
                                    ?? await VelopackUpdateService.CheckForUpdatesAnyAsync();
                                if (any is not null)
                                {
                                    if (settings.AutoDownloadUpdates)
                                    {
                                        var auto = any;
                                        _ = Task.Run(async () =>
                                        {
                                            try
                                            {
                                                await VelopackUpdateService.DownloadUpdatesAsync(auto, null);
                                                _ = _dispatcher.BeginInvoke(() => VelopackUpdateService.ApplyAndRestart(auto.TargetFullRelease));
                                            }
                                            catch { }
                                        });
                                        return;
                                    }
                                    pendingVelo = any;
                                }
                            }
                            catch { }
                        }
                        var captured = pendingVelo;
                        _ = _dispatcher.BeginInvoke(() =>
                        {
                            try
                            {
                                _viewModel.PendingRelease = fallback;
                                _viewModel.PendingVelopack = captured;
                                _viewModel.IsUpdateAvailable = true;
                            }
                            catch { }
                        });
                    }
                    return;
                }
                catch
                {
                    settings.LastUpdateCheckUtc = DateTime.UtcNow.ToString("O");
                    _viewModel.PersistSettings();
                    return;
                }
            }
            catch
            {
                settings.LastUpdateCheckUtc = DateTime.UtcNow.ToString("O");
                _viewModel.PersistSettings();
                return;
            }
        }

        // 2) Non-Velopack (portable/dev) → GitHub full installer — show in-app dialog, not a balloon
        var latest = await UpdateChecker.CheckLatestAsync();
        if (latest is null)
            return;

        settings.LastUpdateCheckUtc = DateTime.UtcNow.ToString("O");
        _viewModel.PersistSettings();

        if (latest.Version is not null && latest.Version.CompareTo(UpdateChecker.CurrentVersion) > 0)
        {
            _ = _dispatcher.BeginInvoke(() =>
            {
                try
                {
                    _viewModel.PendingRelease = latest;
                    _viewModel.PendingVelopack = null;
                    _viewModel.IsUpdateAvailable = true;
                }
                catch { }
            });
        }
    }

    private async Task DownloadAndInstallVelopackAsync(Velopack.UpdateInfo update)
    {
        try
        {
            await VelopackUpdateService.DownloadUpdatesAsync(update, null);
            VelopackUpdateService.ApplyAndRestart(update.TargetFullRelease);
        }
        catch
        {
            // Failure is surfaced in the in-app update dialog (AboutDialog), not via Windows balloon
        }
    }

    /// <summary>Downloads the new installer to the temp folder and launches it. WDM
    /// closes itself so the installer can replace the running copy, then restarts.</summary>
    private async Task DownloadAndInstallUpdateAsync(ReleaseInfo? release)
    {
        if (release is null)
        {
            UpdateChecker.OpenReleasesPage(release?.Url);
            return;
        }
        // If only delta package is available (no .exe), fallback to Velopack or open page
        if (string.IsNullOrWhiteSpace(release.InstallerUrl) && !string.IsNullOrWhiteSpace(release.UpdatePackageUrl))
        {
            try
            {
                var anyUpdate = await VelopackUpdateService.CheckForUpdatesAnyAsync();
                if (anyUpdate != null)
                {
                    await DownloadAndInstallVelopackAsync(anyUpdate);
                    return;
                }
            }
            catch { }
            UpdateChecker.OpenReleasesPage(release.Url);
            return;
        }
        if (string.IsNullOrWhiteSpace(release.InstallerUrl))
        {
            UpdateChecker.OpenReleasesPage(release.Url);
            return;
        }

        try
        {
            string installer = await UpdateChecker.DownloadInstallerAsync(release, null);

            // Silent install — LaunchInstaller passes --silent alone (Velopack
            // bundle uses clap-style parsing; /VERYSILENT would drop it back
            // to the interactive "already installed" dialog) and signals
            // _exiting via MainViewModel.Restarting so Close() isn't trapped
            // by the MinimizeToTray guard.
            UpdateChecker.LaunchInstaller(installer, silent: true);
            await Task.Delay(500);
            _exiting = true;
            Close();
        }
        catch
        {
            UpdateChecker.OpenReleasesPage(release.Url);
        }
    }

    private void TaskGrid_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 1)
            return;

        if (Keyboard.Modifiers != ModifierKeys.None)
            return;

        if (e.OriginalSource is not DependencyObject source)
            return;

        // Do not intercept clicks on buttons (like Open Containing Folder) or scrollbars
        if (FindVisualParent<System.Windows.Controls.Primitives.ButtonBase>(source) is not null ||
            FindVisualParent<System.Windows.Controls.Primitives.TextBoxBase>(source) is not null ||
            FindVisualParent<System.Windows.Controls.Primitives.ScrollBar>(source) is not null)
        {
            return;
        }

        var row = ItemsControl.ContainerFromElement(TaskGrid, source) as DataGridRow
                  ?? FindVisualParent<DataGridRow>(source);

        if (row?.Item is DownloadTask clickedTask)
        {
            // If this task is already the sole selected task, clicking it again deselects it (details panel disappears)
            if (_viewModel.SelectedTask == clickedTask && TaskGrid.SelectedItems.Count <= 1)
            {
                TaskGrid.UnselectAll();
                TaskGrid.SelectedItem = null;
                _viewModel.SelectedTask = null;
                e.Handled = true;
            }
        }
        else
        {
            // Clicked empty area of the list outside any row
            if (_viewModel.SelectedTask is not null || TaskGrid.SelectedItems.Count > 0)
            {
                TaskGrid.UnselectAll();
                TaskGrid.SelectedItem = null;
                _viewModel.SelectedTask = null;
            }
        }
    }

    private static T? FindVisualParent<T>(DependencyObject? child) where T : DependencyObject
    {
        while (child is not null)
        {
            if (child is T parent)
                return parent;

            if (child is Visual or System.Windows.Media.Media3D.Visual3D)
                child = VisualTreeHelper.GetParent(child);
            else if (child is FrameworkContentElement fce)
                child = fce.Parent;
            else
                break;
        }
        return null;
    }

    private static T? FindVisualChild<T>(DependencyObject? parent) where T : DependencyObject
    {
        if (parent is null)
            return null;
        int count = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match)
                return match;
            T? nested = FindVisualChild<T>(child);
            if (nested is not null)
                return nested;
        }
        return null;
    }

    private void TaskGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _viewModel.SetBulkSelection(TaskGrid.SelectedItems.OfType<DownloadTask>());
    }

    private void SidebarSplitter_DragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e)
    {
        _viewModel.SetSidebarWidth(SidebarColumn.ActualWidth);
        // The splitter sets a local width; clear it so the {Binding SidebarWidth}
        // keeps driving collapse/expand and future resize drags stay consistent.
        SidebarColumn.ClearValue(System.Windows.Controls.ColumnDefinition.WidthProperty);
    }

    private void ActionPauseClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: DownloadTask task })
            _viewModel.ToggleTask(task);
    }

    private void ActionRevealClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: DownloadTask task })
        {
            _viewModel.SelectedTask = task;
            _viewModel.RevealSelected();
        }
    }

    private void ActionRemoveClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: DownloadTask task })
            _viewModel.RemoveTask(task);
    }

    private void ShowAbout()
    {
        _dispatcher.BeginInvoke(() =>
        {
            if (_viewModel.IsUpdateAvailable && _viewModel.PendingRelease is not null)
            {
                var dialog = new AboutDialog();
                dialog.ShowAvailableUpdate(_viewModel.PendingRelease, _viewModel.PendingVelopack as Velopack.UpdateInfo);
                dialog.Show();
                return;
            }
            var about = new AboutDialog();
            about.Show();
        });
    }


    private void ShowProgressDialog(DownloadTask? task)
    {
        if (task is null)
            return;

        // Reuse an already-open dialog for this task instead of stacking duplicates.
        if (_openDialogs.TryGetValue(task.Id, out var existing))
        {
            existing.Activate();
            return;
        }

        var dialog = new DownloadProgressDialog(task, _viewModel);
        _openDialogs[task.Id] = dialog;
        dialog.Closed += (_, _) => _openDialogs.Remove(task.Id);
        dialog.Show();
    }

    private void MenuExit_Click(object sender, RoutedEventArgs e)
    {
        ExitApp();
    }

    private void MenuExtension_Click(object sender, RoutedEventArgs e)
    {
        ShowExtensionInstallerDialog();
    }

    private void MoreButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.ContextMenu is not null)
        {
            btn.ContextMenu.PlacementTarget = btn;
            btn.ContextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            btn.ContextMenu.IsOpen = true;
        }
    }

    public void ShowExtensionInstallerDialog()
    {
        ShowExtensionInstallerDialog(false);
    }

    public void ShowExtensionInstallerDialog(bool fromSettings)
    {
        DownloadsView.Visibility = Visibility.Collapsed;
        SettingsView.Visibility = Visibility.Collapsed;
        NoticeView.Visibility = Visibility.Collapsed;
        ExtensionView.Visibility = Visibility.Visible;
    }

    public void ShowExtensionReloadNotice(string oldVersion, string newVersion)
    {
        DownloadsView.Visibility = Visibility.Collapsed;
        SettingsView.Visibility = Visibility.Collapsed;
        ExtensionView.Visibility = Visibility.Collapsed;
        NoticeView.Visibility = Visibility.Visible;
        NoticeContent.Initialize(oldVersion, newVersion);
        // Best-effort: fetch short release notes for the new version
        _ = Task.Run(async () =>
        {
            try
            {
                var rel = await Services.UpdateChecker.CheckLatestAsync();
                if (rel?.Body != null && rel.Version?.ToString() == newVersion)
                    _ = Dispatcher.BeginInvoke(() => NoticeContent.Initialize(oldVersion, newVersion, rel.Body));
            }
            catch { }
        });
    }

    public void ShowDownloadsView()
    {
        SettingsView.Visibility = Visibility.Collapsed;
        ExtensionView.Visibility = Visibility.Collapsed;
        NoticeView.Visibility = Visibility.Collapsed;
        DownloadsView.Visibility = Visibility.Visible;
        _viewModel.PersistSettings();
        TaskGrid.Focus();
    }

    private void SearchClear_Click(object sender, RoutedEventArgs e)
    {
        SearchBox.Text = "";
        SearchBox.Focus();
    }

    private bool _snappingHeight;
    private bool _snapRetryHooked;

    private void Window_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        SnapWindowHeightToWholeRows();
    }

    private void Window_ContentRendered(object? sender, EventArgs e)
    {
        // Layout transients (zero viewports, unrealized rows) make early
        // passes no-ops; retry once content is rendered and realized.
        Dispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.Loaded,
            new Action(SnapWindowHeightToWholeRows));
        if (!_snapRetryHooked && TaskGrid is not null)
        {
            _snapRetryHooked = true;
            TaskGrid.ItemContainerGenerator.StatusChanged += (_, _) =>
            {
                if (TaskGrid.ItemContainerGenerator.Status == System.Windows.Controls.Primitives.GeneratorStatus.ContainersGenerated)
                    Dispatcher.BeginInvoke(
                        System.Windows.Threading.DispatcherPriority.Background,
                        new Action(SnapWindowHeightToWholeRows));
            };
        }
    }

    /// <summary>Whole rows by window size: moves the window's bottom edge onto
    /// a row boundary (shorter or longer, whichever is closer) so no row is
    /// ever half-shown. Measures the actual row at the cut point, so taller
    /// error rows are handled exactly. Also nudges a drifted scroll offset
    /// back onto a boundary so the top row is whole too. Converges in one
    /// step; maximized windows and MinHeight always win.</summary>
    private void SnapWindowHeightToWholeRows()
    {
        if (_snappingHeight || WindowState != WindowState.Normal || TaskGrid is null)
            return;
        if (TaskGrid.Items.Count == 0)
            return;
        ScrollViewer? scroller = FindVisualChild<ScrollViewer>(TaskGrid);
        if (scroller is null || scroller.ExtentHeight <= 0 || scroller.ViewportHeight <= 0)
            return;
        if (double.IsNaN(Height) || Height <= 0)
            return;
        double offset = scroller.VerticalOffset;
        double edge = offset + scroller.ViewportHeight;
        // Locate the row straddling the bottom edge (if any) and the bottom
        // of the last fully-visible row.
        DataGridRow? cutRow = null;
        double cutTop = 0, cutBottom = 0;
        double lastWholeBottom = double.NaN;
        foreach (object? item in TaskGrid.Items)
        {
            if (TaskGrid.ItemContainerGenerator.ContainerFromItem(item) is not DataGridRow row)
                continue;
            double top, bottom;
            try
            {
                top = row.TranslatePoint(new Point(0, 0), scroller).Y + offset;
                bottom = top + row.ActualHeight;
            }
            catch (InvalidOperationException) { continue; }
            if (top <= edge - 2 && bottom >= edge + 2)
            {
                if (cutRow is null || top < cutTop)
                {
                    cutRow = row;
                    cutTop = top;
                    cutBottom = bottom;
                }
            }
            else if (bottom <= edge + 2 && top >= offset - 2)
            {
                if (double.IsNaN(lastWholeBottom) || bottom > lastWholeBottom)
                    lastWholeBottom = bottom;
            }
        }
        double heightAdjust = 0;
        if (cutRow is not null)
        {
            double shown = edge - cutTop;
            double hidden = cutBottom - edge;
            double rowH = Math.Max(1, cutBottom - cutTop);
            heightAdjust = shown >= rowH / 2 ? hidden : -shown;
        }
        else if (!double.IsNaN(lastWholeBottom))
        {
            double slack = edge - lastWholeBottom;
            if (slack >= 2)
                heightAdjust = -slack; // empty space below the last row: pull up
        }
        // Nudge a drifted scroll offset back onto a row boundary.
        double offAdjust = 0;
        if (cutRow is null && !double.IsNaN(lastWholeBottom))
        {
            double topEdge = offset;
            foreach (object? item in TaskGrid.Items)
            {
                if (TaskGrid.ItemContainerGenerator.ContainerFromItem(item) is not DataGridRow row)
                    continue;
                double top;
                try { top = row.TranslatePoint(new Point(0, 0), scroller).Y + offset; }
                catch (InvalidOperationException) { continue; }
                if (top <= topEdge + 2 && topEdge <= top + row.ActualHeight + 2 && topEdge - top >= 2)
                {
                    offAdjust = top - topEdge; // negative: scroll slightly up
                    break;
                }
            }
        }
        if (Math.Abs(heightAdjust) < 2 && Math.Abs(offAdjust) < 2)
            return;
        _snappingHeight = true;
        try
        {
            if (Math.Abs(heightAdjust) >= 2)
            {
                double newHeight = Height + heightAdjust;
                if (newHeight >= MinHeight)
                    Height = newHeight;
            }
            if (Math.Abs(offAdjust) >= 2)
            {
                double target = Math.Max(0, offset + offAdjust);
                if (Math.Abs(target - offset) >= 1)
                    scroller.ScrollToVerticalOffset(target);
            }
        }
        finally { _snappingHeight = false; }
    }

    private void LocationText_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left && _viewModel.SelectedTask is not null)
        {
            _viewModel.RevealSelected();
            e.Handled = true;
        }
    }

    private void SourceText_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left && _viewModel.SelectedTask is not null)
        {
            _viewModel.CopySelectedUrl();
            e.Handled = true;
        }
    }

    private void TaskGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        var row = ItemsControl.ContainerFromElement(TaskGrid, e.OriginalSource as DependencyObject) as DataGridRow;
        if (row?.Item is DownloadTask task)
        {
            if (task.Status == TaskStatus.Completed)
                _viewModel.OpenFile(task);
            else if (task.Status == TaskStatus.Downloading || task.Status == TaskStatus.Queued || task.Status == TaskStatus.Paused)
                ShowProgressDialog(task);
            else
                ShowProperties(task);
        }
    }

    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);
        // Minimize sends WDM window to the main Windows taskbar
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!_exiting && _viewModel.Settings.MinimizeToTray)
        {
            e.Cancel = true;
            Hide();
            var active = _viewModel.Tasks.FirstOrDefault(t => t.Status == Models.TaskStatus.Downloading);
            if (active is not null)
                UpdateProgressPanel(_viewModel.Settings.ShowTrayProgress ? active : null);
            return;
        }
        base.OnClosing(e);
    }

    private void OnRestarting() => _exiting = true;

    /// <summary>Window-level fallback for list shortcut keys (Delete/Enter/Space/F5)
    /// when the user clicked outside the DataGrid (e.g. sidebar, background).
    /// Does nothing when the focused element is a text input control so Space/Delete/Enter
    /// edit text normally (BUG-084).</summary>
    private void OnWindowKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Handled)
            return;
        if (e.KeyboardDevice.Modifiers != System.Windows.Input.ModifierKeys.None)
            return;
        var focused = System.Windows.Input.Keyboard.FocusedElement as DependencyObject;
        if (focused is System.Windows.Controls.Primitives.TextBoxBase
            || focused is System.Windows.Controls.ComboBox cb && cb.IsEditable)
        {
            return; // let text controls process their own keystrokes freely
        }

        if (e.Key is System.Windows.Input.Key.Delete && _viewModel.BulkRemoveCommand.CanExecute(null))
        {
            _viewModel.BulkRemoveCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key is (System.Windows.Input.Key.Enter or System.Windows.Input.Key.Return) && _viewModel.BulkResumeCommand.CanExecute(null))
        {
            _viewModel.BulkResumeCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key is System.Windows.Input.Key.Space && _viewModel.TogglePauseCommand.CanExecute(null))
        {
            _viewModel.TogglePauseCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key is System.Windows.Input.Key.F5 && _viewModel.RetryCommand.CanExecute(null))
        {
            _viewModel.RetryCommand.Execute(null);
            e.Handled = true;
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        try { _trayTimer.Stop(); } catch { }
        try { MainViewModel.Restarting -= OnRestarting; } catch { }
        _progressPanel?.Close();
        _captureServer.Dispose();
        _tray.Dispose();
        _viewModel.SaveTasksNow();
        _viewModel.Dispose();
        base.OnClosed(e);
    }
}