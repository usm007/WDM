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
        _viewModel.SpeedHistoryUpdated += history => _dispatcher.BeginInvoke(() => RenderSparkline(history));

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

        _captureServer = new CaptureServer((url, name, referer) =>
            _dispatcher.BeginInvoke(() => ShowAddDialog(url, name, referer, fromCapture: true)));
        _captureServer.Start();

        _tray = new TrayIcon();
        _tray.Activated += () => _dispatcher.BeginInvoke(RestoreWindow);
        _tray.NewDownloadRequested += () => _dispatcher.BeginInvoke(() => ShowAddDialog());
        _tray.PauseAllRequested += () => _dispatcher.BeginInvoke(() => _viewModel.Engine.PauseAll());
        _tray.ResumeAllRequested += () => _dispatcher.BeginInvoke(() => _viewModel.Engine.ResumeAll());
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
                _tray.SetProgress(active.Progress, active.SpeedText ?? "0 B/s", active.FileName ?? "");
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

        ApplyRoundedClip(SidebarCard);
        ApplyRoundedClip(ListCard);

        Loaded += (_, _) =>
        {
            BrowserIntegration.DeployExtension();
            if (!_captureServer.IsConnected && !_viewModel.Settings.HasPromptedExtensionInstall && !App.StartMinimized)
            {
                _viewModel.Settings.HasPromptedExtensionInstall = true;
                _viewModel.PersistSettings();
                _dispatcher.BeginInvoke(ShowExtensionInstallerDialog);
            }
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

    private void ShowAddDialog(string? prefillUrl = null, string? prefillFileName = null, string? prefillReferer = null, bool fromCapture = false)
    {
        if (!string.IsNullOrWhiteSpace(prefillUrl) && _viewModel.ExistingUrl(prefillUrl))
            return;

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

        var dialog = new AddDownloadDialog(_viewModel, prefillUrl, prefillFileName, prefillReferer)
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
        if (dialog.ShowDialog() == true)
            _viewModel.ApplyLinkRefresh(task, dialog.NewUrl);
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
        var dialog = new OptionsDialog(_viewModel);
        if (dialog.ShowDialog() == true)
            _viewModel.PersistSettings();
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

    /// <summary>Checks GitHub for a newer WDM release, at most once every 15 minutes,
    /// and prompts the user to download and install it when one is found.</summary>
    private async Task CheckForUpdatesAsync()
    {
        var settings = _viewModel.Settings;
        DateTime? lastCheck = DateTime.TryParse(settings.LastUpdateCheckUtc, null, System.Globalization.DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed
            : null;
        if (lastCheck is not null && DateTime.UtcNow - lastCheck < TimeSpan.FromMinutes(15))
            return;

        var latest = await UpdateChecker.CheckLatestAsync();
        // Only stamp the last-check time on a successful answer; a transient network
        // failure must not disable checking for another 24h.
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

    public void ShowExtensionInstallerDialog()
    {
        var dialog = new BrowserExtensionDialog();
        dialog.ShowDialog();
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
