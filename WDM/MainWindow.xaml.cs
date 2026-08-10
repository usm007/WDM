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
    private bool _exiting;

    public MainWindow()
    {
        InitializeComponent();
        _viewModel = new MainViewModel();
        DataContext = _viewModel;

        _viewModel.AddTaskRequested += _ => ShowAddDialog();
        _viewModel.EditTaskRequested += task => ShowProperties(task);
        _viewModel.OptionsRequested += ShowOptions;
        _viewModel.ScheduleRequested += task => ShowSchedule(task);
        _viewModel.AboutRequested += ShowAbout;
        _viewModel.ShowProgressDialogRequested += task => ShowProgressDialog(task);
        _viewModel.SpeedHistoryUpdated += history => _dispatcher.BeginInvoke(() => RenderSparkline(history));

        _viewModel.TaskCompleted += task =>
        {
            if (_viewModel.Settings.NotifyOnCompletion)
                _tray?.ShowBalloon("Download complete", task.FileName);
        };

        _captureServer = new CaptureServer((url, name, referer) => _viewModel.AddTask(url, name, referer));
        _captureServer.Start();

        _clipboard = new ClipboardMonitor();
        _clipboard.UrlCopied += url => _dispatcher.BeginInvoke(() =>
        {
            if (_viewModel.Settings.MonitorClipboard)
                _viewModel.AddTask(url);
        });

        _tray = new TrayIcon();
        _tray.Activated += () => _dispatcher.BeginInvoke(RestoreWindow);
        _tray.NewDownloadRequested += () => _dispatcher.BeginInvoke(ShowAddDialog);
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
        trayTimer.Tick += (_, _) => _tray.SetActiveCount(_viewModel.Engine.ActiveCount, _viewModel.Engine.QueuedCount);
        trayTimer.Start();
    }

    private System.Windows.Threading.Dispatcher _dispatcher =>
        System.Windows.Application.Current.Dispatcher;

    private void RenderSparkline(List<double> history)
    {
        if (SparklineCanvas == null)
            return;

        SparklineCanvas.Children.Clear();
        if (history == null || history.Count < 2)
            return;

        double max = history.Max();
        if (max <= 0)
            max = 1;

        double width = SparklineCanvas.ActualWidth > 0 ? SparklineCanvas.ActualWidth : 100;
        double height = SparklineCanvas.ActualHeight > 0 ? SparklineCanvas.ActualHeight : 18;
        double step = width / (history.Count - 1);

        var points = new PointCollection();
        for (int i = 0; i < history.Count; i++)
        {
            double x = i * step;
            double y = height - (history[i] / max * (height - 4)) - 2;
            points.Add(new Point(x, y));
        }

        var polyPoints = new PointCollection(points)
        {
            new Point(width, height),
            new Point(0, height)
        };

        var fillBrush = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(0, 1),
            GradientStops = new GradientStopCollection
            {
                new GradientStop(Color.FromArgb(70, 59, 130, 246), 0),
                new GradientStop(Color.FromArgb(0, 59, 130, 246), 1)
            }
        };

        var polygon = new Polygon
        {
            Points = polyPoints,
            Fill = fillBrush
        };
        SparklineCanvas.Children.Add(polygon);

        var polyline = new Polyline
        {
            Points = points,
            Stroke = (Brush)Application.Current.Resources["Brush.Accent"],
            StrokeThickness = 1.5,
            StrokeLineJoin = PenLineJoin.Round
        };
        SparklineCanvas.Children.Add(polyline);
    }

    private void ShowAddDialog()
    {
        var dialog = new AddDownloadDialog(_viewModel) { Owner = this };
        dialog.ShowDialog();
    }

    private void ShowProperties(DownloadTask? task)
    {
        if (task is null)
            return;
        var dialog = new TaskPropertiesDialog(task) { Owner = this };
        dialog.ShowDialog();
    }

    private void ShowSchedule(DownloadTask? task)
    {
        if (task is null)
            return;
        var dialog = new ScheduleDialog(task) { Owner = this };
        if (dialog.ShowDialog() == true && dialog.SelectedTime is DateTime when)
            _viewModel.ScheduleSelected(when);
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
        var dialog = new OptionsDialog(_viewModel) { Owner = this };
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
        bool showBulk = TaskGrid.SelectedItems.Count >= 2;
        if (BulkBar.Visibility != (showBulk ? Visibility.Visible : Visibility.Collapsed))
            BulkBar.Visibility = showBulk ? Visibility.Visible : Visibility.Collapsed;
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
        var dialog = new AboutDialog { Owner = this };
        dialog.ShowDialog();
    }

    private void ShowProgressDialog(DownloadTask? task)
    {
        if (task is null)
            return;
        var dialog = new DownloadProgressDialog(task, _viewModel) { Owner = this };
        dialog.Show();
    }

    private void MenuExit_Click(object sender, RoutedEventArgs e)
    {
        ExitApp();
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
        if (WindowState == WindowState.Minimized && _viewModel.Settings.MinimizeToTray && !_exiting)
        {
            Hide();
        }
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
        _viewModel.SaveTasksSoon();
        base.OnClosed(e);
    }
}
