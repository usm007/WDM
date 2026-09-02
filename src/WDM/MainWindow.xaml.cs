using System.Collections.Generic;
using System.Runtime.InteropServices;
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

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly CaptureServer _captureServer;
    private readonly TrayIcon _tray;
    private readonly Dictionary<Guid, Window> _openDialogs = new();
    private TrayProgressPanel? _progressPanel;
    private bool _exiting;
    private DownloadCompleteDialog? _completeDialog;
    private RefreshLinkDialog? _activeRefreshDialog;

    public MainWindow()
    {
        InitializeComponent();
        _viewModel = new MainViewModel();
        DataContext = _viewModel;

        _viewModel.AddTaskRequested += _ => ShowAddDialog();
        _viewModel.EditTaskRequested += task => ShowProperties(task);
        _viewModel.OptionsRequested += ShowOptions;
        _viewModel.AboutRequested += ShowAbout;
        _viewModel.ShowProgressDialogRequested += task => ShowProgressDialog(task);
        _viewModel.RefreshLinkRequested += task => ShowRefreshLink(task);
        _viewModel.DeletePromptRequested += ShowDeletePrompt;
        _viewModel.SpeedHistoryUpdated += history => _dispatcher.BeginInvoke(() => RenderSparkline(history));

        SettingsContent.CloseRequested += (_, _) => ShowDownloadsView();
        SettingsContent.OpenExtensionHelperRequested += (_, _) => ShowExtensionInstallerDialog(fromSettings: true);
        ExtensionContent.DoneRequested += (_, _) => ShowDownloadsView();
        NoticeContent.CloseRequested += (_, _) => ShowDownloadsView();

        _viewModel.TaskCompleted += task =>
        {
            if (_viewModel.Settings.NotifyOnCompletion)
                _tray?.ShowBalloon("Done", $"{task.FileName} is ready.");

            // Show a single Download Complete dialog at a time instead of stacking
            // a modal chain when several tasks finish close together.
            _dispatcher.BeginInvoke(() =>
            {
                if (_completeDialog is not null)
                    return;
                var dialog = new DownloadCompleteDialog(task);
                _completeDialog = dialog;
                dialog.Closed += (_, _) => _completeDialog = null;
                dialog.Show();
            });
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
        _captureServer.Start();

        _tray = new TrayIcon();
        _tray.Activated += () => _dispatcher.BeginInvoke(RestoreWindow);
        _tray.NewDownloadRequested += () => _dispatcher.BeginInvoke(() => ShowAddDialog());
        _tray.PauseAllRequested += () => _dispatcher.BeginInvoke(() => _viewModel.Engine.PauseAll());
        _tray.ResumeAllRequested += () => _dispatcher.BeginInvoke(() => _viewModel.ResumeAll());
        _tray.ExitRequested += () => _dispatcher.BeginInvoke(ExitApp);

        var trayTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1),
        };
        trayTimer.Tick += (_, _) =>
        {
            var active = _viewModel.Tasks.FirstOrDefault(t => t.Status == Models.TaskStatus.Downloading);
            if (active is not null)
            {
                // The floating pill (docked to the right edge) shows % + speed.
                string speed = string.IsNullOrEmpty(active.SpeedText) ? "0 B/s" : active.SpeedText;
                _tray.SetProgress(active.Progress, speed, active.FileName ?? "");
                UpdateProgressPanel(_viewModel.Settings.ShowTrayProgress ? active : null);
            }
            else
            {
                _tray.SetActiveCount(
                    _viewModel.Engine.ActiveCount, _viewModel.Engine.QueuedCount, _viewModel.Engine.TotalSpeedBps);
                UpdateProgressPanel(null);
            }
        };
        trayTimer.Start();

        Loaded += (_, _) =>
        {
            BrowserIntegration.DeployExtension();
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
                string currentVer = UpdateChecker.CurrentVersion.ToString();
                string? lastVer = _viewModel.Settings.LastRunVersion;
                if (!string.IsNullOrWhiteSpace(lastVer) && lastVer != currentVer)
                {
                    _dispatcher.BeginInvoke(() => ShowExtensionReloadNotice(lastVer, currentVer));
                }
            }

            _viewModel.Settings.LastRunVersion = UpdateChecker.CurrentVersion.ToString();
            _viewModel.PersistSettings();

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
        RemoveNativeWindowShadow();
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

    private const int GCL_STYLE = -20;
    private const int CS_DROPSHADOW = 0x00020000;

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern int GetClassLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern int SetClassLong(IntPtr hWnd, int nIndex, int dwNewLong);

    /// <summary>Clears the CS_DROPSHADOW class style so the window has no native drop shadow.</summary>
    private void RemoveNativeWindowShadow()
    {
        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        int style = GetClassLong(hwnd, GCL_STYLE);
        if ((style & CS_DROPSHADOW) != 0)
            SetClassLong(hwnd, GCL_STYLE, style & ~CS_DROPSHADOW);
    }

    private void ApplyRoundedClip(Border border)
    {
        border.SizeChanged += (_, _) =>
        {
            if (border.ActualWidth <= 0 || border.ActualHeight <= 0)
                return;
            border.Clip = new RectangleGeometry
            {
                Rect = new Rect(0, 0, border.ActualWidth, border.ActualHeight),
                RadiusX = 8,
                RadiusY = 8,
            };
        };
    }

    private System.Windows.Threading.Dispatcher _dispatcher =>
        System.Windows.Application.Current.Dispatcher;

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
            Stroke = (Brush)Application.Current.Resources["Brush.Accent"],
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

    private void ShowAddDialog(string? prefillUrl = null, string? prefillFileName = null, string? prefillReferer = null, Dictionary<string, string>? prefillHeaders = null, bool fromCapture = false, string? pageTitle = null)
    {
        string targetFolder = _viewModel.Settings.DownloadFolder;
        string rawName = prefillFileName ?? (!string.IsNullOrWhiteSpace(prefillUrl) ? DownloadEngine.DeriveName(prefillUrl) : "");
        string initialFileName = DownloadEngine.SanitizeFileName(rawName, pageTitle, prefillReferer);

        if (!string.IsNullOrWhiteSpace(prefillUrl) && (_viewModel.ExistingUrl(prefillUrl) || _viewModel.IsDuplicateFile(initialFileName, targetFolder)))
        {
            string numberedFileName = _viewModel.GetNumberedFileName(initialFileName, targetFolder);
            var dupDialog = new DuplicateDownloadDialog(prefillUrl, initialFileName, numberedFileName)
            {
                Topmost = fromCapture,
                Owner = this
            };

            bool? dupResult = dupDialog.ShowDialog();
            if (dupResult == true)
            {
                if (dupDialog.SelectedAction == DuplicateAction.RenameAndDownload)
                {
                    prefillFileName = dupDialog.NumberedFileName;
                }
                else if (dupDialog.SelectedAction == DuplicateAction.Overwrite)
                {
                    prefillFileName = dupDialog.OriginalFileName;
                }
                else
                {
                    return;
                }
            }
            else
            {
                // User cancelled or closed dialog
                return;
            }
        }

        // When a link is captured from the browser extension, show the dialog on
        // top of every window without surfacing the main WDM window.
        if (!fromCapture)
            RestoreWindow();

        if (_activeAddDialog is not null && _activeAddDialog.IsLoaded)
        {
            _activeAddDialog.Topmost = fromCapture;
            _activeAddDialog.Activate();
            return;
        }

        var dialog = new AddDownloadDialog(_viewModel, prefillUrl, prefillFileName, prefillReferer, prefillHeaders)
        {
            Topmost = fromCapture,
        };
        _activeAddDialog = dialog;
        dialog.Closed += (_, _) => _activeAddDialog = null;
        dialog.ShowDialog();
    }

    private void ShowProperties(DownloadTask? task)
    {
        if (task is null)
            return;
        var dialog = new TaskPropertiesDialog(task);
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
                string content = File.ReadAllText(path);
                string url = content.Trim().Trim('[', ']', '"', '\'', ';', ' ');
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
        if (Uri.TryCreate(text.Trim(), UriKind.Absolute, out var uri) &&
            (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeFtp))
            _viewModel.AddTask(text.Trim());
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
            System.Windows.MessageBox.Show(ex.ToString());
        }
    }

    private void RestoreWindow()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
        UpdateProgressPanel(null);
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

        _progressPanel ??= new TrayProgressPanel(_viewModel);
        _progressPanel.ShowPanel(active!);
    }

    private void ExitApp()
    {
        _exiting = true;
        Close();
    }

    /// <summary>Checks for updates: Velopack delta first (patch-only, ~2MB), then GitHub full installer as fallback.
    /// Throttled to once every 15 minutes.</summary>
    private async Task CheckForUpdatesAsync()
    {
        var settings = _viewModel.Settings;
        DateTime? lastCheck = DateTime.TryParse(settings.LastUpdateCheckUtc, null, System.Globalization.DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed
            : null;
        if (lastCheck is not null && DateTime.UtcNow - lastCheck < TimeSpan.FromMinutes(15))
            return;

        // 1) Try Velopack delta (silent patch) when installed via Velopack.
        if (VelopackUpdateService.IsVelopackInstalled)
        {
            try
            {
                var velopackUpdate = await VelopackUpdateService.CheckForUpdatesAsync();
                if (velopackUpdate is not null)
                {
                    settings.LastUpdateCheckUtc = DateTime.UtcNow.ToString("O");
                    _viewModel.PersistSettings();

                    var semVer = velopackUpdate.TargetFullRelease.Version;
                    var target = VelopackUpdateService.ToSystemVersion(semVer);
                    // Reuse ReleaseInfo shape for dialog: create synthetic ReleaseInfo from Velopack asset
                    var synthetic = new ReleaseInfo($"v{target}", target, $"WDM {target}", $"https://github.com/usm007/WDM/releases/tag/v{target}", $"Delta update to {target} (patch-only, no full installer).", DateTime.UtcNow, null);
                    // Attach velopack payload via dialog's velopack path
                    _tray.ShowBalloon(
                        "WDM update available",
                        $"WDM {target} is available — click to download delta and restart.",
                        () => _dispatcher.BeginInvoke(async () => await DownloadAndInstallVelopackAsync(velopackUpdate)));
                    _ = _dispatcher.BeginInvoke(() => ShowUpdatePromptVelopack(synthetic, velopackUpdate));
                    return;
                }
            }
            catch { /* fall through to GitHub check */ }
        }

        // 2) Fallback: GitHub full installer check
        var latest = await UpdateChecker.CheckLatestAsync();
        if (latest is null)
            return;

        settings.LastUpdateCheckUtc = DateTime.UtcNow.ToString("O");
        _viewModel.PersistSettings();

        if (latest.Version is not null && latest.Version.CompareTo(UpdateChecker.CurrentVersion) > 0)
        {
            _tray.ShowBalloon(
                "WDM update available",
                $"WDM {latest.Version} is available — click to download and install.",
                () => _dispatcher.BeginInvoke(async () => await DownloadAndInstallUpdateAsync(latest)));
            _ = _dispatcher.BeginInvoke(() => ShowUpdatePrompt(latest));
        }
    }

    private void ShowUpdatePrompt(ReleaseInfo latest)
    {
        // Always surface the dialog, even when started minimized, because Windows
        // 10/11 frequently suppress tray balloons and the user would never see the
        // notification otherwise.
        var dialog = new UpdateAvailableDialog(latest) { Owner = this };
        dialog.ShowDialog();
    }

    private void ShowUpdatePromptVelopack(ReleaseInfo synthetic, Velopack.UpdateInfo velopackInfo)
    {
        var dialog = new UpdateAvailableDialog(synthetic, velopackInfo) { Owner = this };
        dialog.ShowDialog();
    }

    private async Task DownloadAndInstallVelopackAsync(Velopack.UpdateInfo update)
    {
        try
        {
            _tray.ShowBalloon("WDM update", "Downloading delta update…");
            await VelopackUpdateService.DownloadUpdatesAsync(update, pct =>
            {
                if (pct % 25 == 0)
                    _dispatcher.BeginInvoke(() => _tray.ShowBalloon("WDM update", $"Downloading delta… {pct}%"));
            });
            VelopackUpdateService.ApplyAndRestart(update.TargetFullRelease);
        }
        catch (Exception ex)
        {
            _tray.ShowBalloon("Update failed", $"Could not download delta: {ex.Message}");
        }
    }

    /// <summary>Downloads the new installer to the temp folder and launches it. WDM
    /// closes itself so the installer can replace the running copy, then restarts.</summary>
    private async Task DownloadAndInstallUpdateAsync(ReleaseInfo? release)
    {
        if (release is null || string.IsNullOrWhiteSpace(release.InstallerUrl))
        {
            _tray.ShowBalloon("No download available", "This release has no installer attached yet — open the releases page instead.");
            return;
        }

        try
        {
            _tray.ShowBalloon("WDM update", "Downloading the new installer…");
            string installer = await UpdateChecker.DownloadInstallerAsync(release, progress =>
            {
                int pct = (int)Math.Round(progress * 100);
                if (pct % 25 == 0)
                    _dispatcher.BeginInvoke(() => _tray.ShowBalloon("WDM update", $"Downloading the new installer… {pct}%"));
            });

            // Let the installer take over; it closes and restarts WDM.
            UpdateChecker.LaunchInstaller(installer);
            await Task.Delay(500);
            _exiting = true;
            Close();
        }
        catch (Exception ex)
        {
            _tray.ShowBalloon("Update failed", $"Could not download the update: {ex.Message}");
        }
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
        var dialog = new AboutDialog();
        dialog.ShowDialog();
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
                _viewModel.OpenFile();
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

    protected override void OnClosed(EventArgs e)
    {
        _progressPanel?.Close();
        _captureServer.Dispose();
        _tray.Dispose();
        _viewModel.SaveTasksNow();
        base.OnClosed(e);
    }
}