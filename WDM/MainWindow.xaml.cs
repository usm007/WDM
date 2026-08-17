using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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
    private readonly ClipboardMonitor _clipboard;
    private readonly Dictionary<Guid, Window> _openDialogs = new();
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
            _dispatcher.BeginInvoke(() => ShowAddDialog(url, name, referer)));
        _captureServer.Start();

        _clipboard = new ClipboardMonitor();
        _clipboard.UrlCopied += url => _dispatcher.BeginInvoke(async () =>
        {
            if (!_viewModel.Settings.MonitorClipboard || _viewModel.ExistingUrl(url))
                return;
            await PromptForClipboardLinkAsync(url);
        });

        _tray = new TrayIcon();
        _tray.Activated += () => _dispatcher.BeginInvoke(RestoreWindow);
        _tray.NewDownloadRequested += () => _dispatcher.BeginInvoke(() => ShowAddDialog());
        _tray.PauseAllRequested += () => _dispatcher.BeginInvoke(() => _viewModel.Engine.PauseAll());
        _tray.ResumeAllRequested += () => _dispatcher.BeginInvoke(() => _viewModel.Engine.ResumeAll());
        _tray.ClipboardMonitoringChanged += enabled => _dispatcher.BeginInvoke(() =>
        {
            _viewModel.Settings.MonitorClipboard = enabled;
            _viewModel.PersistSettings();
            _clipboard.Enabled = enabled;
        });
        _tray.ExitRequested += () => _dispatcher.BeginInvoke(ExitApp);
        _tray.ClipboardMonitoring = _viewModel.Settings.MonitorClipboard;
        _clipboard.Enabled = _viewModel.Settings.MonitorClipboard;

        var trayTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1),
        };
        trayTimer.Tick += (_, _) => _tray.SetActiveCount(
            _viewModel.Engine.ActiveCount, _viewModel.Engine.QueuedCount, _viewModel.Engine.TotalSpeedBps);
        trayTimer.Start();

        ApplyRoundedClip(SidebarCard);
        ApplyRoundedClip(ListCard);

        Loaded += (_, _) =>
        {
            BrowserIntegration.DeployExtension();
            if (!_captureServer.IsConnected && !_viewModel.Settings.HasPromptedExtensionInstall)
            {
                _viewModel.Settings.HasPromptedExtensionInstall = true;
                _viewModel.PersistSettings();
                _dispatcher.BeginInvoke(ShowExtensionInstallerDialog);
            }
        };
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

    private void ShowAddDialog(string? prefillUrl = null, string? prefillFileName = null, string? prefillReferer = null)
    {
        if (!string.IsNullOrWhiteSpace(prefillUrl) && _viewModel.ExistingUrl(prefillUrl))
            return;

        RestoreWindow();

        if (_activeAddDialog is not null && _activeAddDialog.IsLoaded)
        {
            _activeAddDialog.Activate();
            return;
        }

        var dialog = new AddDownloadDialog(_viewModel, prefillUrl, prefillFileName, prefillReferer);
        _activeAddDialog = dialog;
        dialog.Closed += (_, _) => _activeAddDialog = null;
        dialog.ShowDialog();
    }

    /// <summary>
    /// Called when the clipboard captures a URL. Probes the link first and only
    /// notifies the user when it actually points at a downloadable file. The user
    /// confirms by clicking the balloon, which opens the Add dialog prefilled.
    /// </summary>
    private async Task PromptForClipboardLinkAsync(string url)
    {
        var probe = await UrlProbe.ProbeAsync(url);
        if (probe is null || !probe.IsFile)
            return;

        string size = probe.SizeBytes > 0 ? DownloadTask.FormatBytes(probe.SizeBytes) : "unknown size";
        _tray?.ShowBalloon("Download link detected", $"{probe.FileName} ({size}) — click to download.", () =>
            _dispatcher.BeginInvoke(() => ShowAddDialog(url, probe.FileName)));
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
        {
            _viewModel.PersistSettings();
            _clipboard.Enabled = _viewModel.Settings.MonitorClipboard;
        }
    }

    private void RestoreWindow()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private void ExitApp()
    {
        _exiting = true;
        Close();
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
        var dialog = new BrowserExtensionDialog(_captureServer);
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
            return;
        }
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        _captureServer.Dispose();
        _clipboard.Enabled = false;
        _tray.Dispose();
        _viewModel.SaveTasksNow();
        base.OnClosed(e);
    }
}
