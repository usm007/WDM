using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using WDM.Models;
using WDM.ViewModels;

namespace WDM;

/// <summary>
/// Small floating pill: a dark background with a green fill that grows from the left
/// to indicate download progress, with the percentage and network speed on a single
/// row. Defaults to the bottom-right corner and can be dragged anywhere; its position
/// is remembered.
/// </summary>
public partial class TrayProgressPanel : Window, INotifyPropertyChanged
{
    private const double PillWidth = 173;

    private readonly MainViewModel _viewModel;
    private readonly DispatcherTimer _refreshTimer;
    private DownloadTask? _task;
    private Point _dragStartCursor;
    private Point _dragStartWindow;
    private bool _dragging;

    public TrayProgressPanel(MainViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = this;

        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _refreshTimer.Tick += (_, _) => Refresh();
        _refreshTimer.Start();

        _viewModel.Engine.TaskChanged += Engine_TaskChanged;
    }

    public DownloadTask? Task
    {
        get => _task;
        private set
        {
            if (ReferenceEquals(_task, value))
                return;
            _task = value;
            OnPropertyChanged(nameof(Task));
            OnPropertyChanged(nameof(StatusText));
            OnPropertyChanged(nameof(FillWidth));
        }
    }

    public string StatusText
    {
        get
        {
            if (_task is null)
                return "0% · 0 B/s";
            return $"{_task.Progress}% · {_task.SpeedText ?? "0 B/s"}";
        }
    }

    /// <summary>Width of the green fill inside the pill (relative to progress).</summary>
    public double FillWidth => PillWidth * (_task?.Progress ?? 0) / 100.0;

    /// <summary>Shows the pill snapped to the nearest side edge (default bottom-right corner).</summary>
    public void ShowPanel(DownloadTask task)
    {
        Task = task;
        Refresh();
        Show();
        RestorePosition();
        SnapToEdge();
    }

    public void HidePanel()
    {
        Hide();
    }

    private void RestorePosition()
    {
        var settings = _viewModel.Settings;
        var area = SystemParameters.WorkArea;
        if (settings.ProgressPanelLeft is double left && settings.ProgressPanelTop is double top)
        {
            // Keep it on screen in case the display changed.
            left = Math.Clamp(left, area.Left, area.Right - ActualWidth);
            top = Math.Clamp(top, area.Top, area.Bottom - ActualHeight);
            Left = left;
            Top = top;
        }
        else
        {
            Left = area.Right - ActualWidth - 8;
            Top = area.Bottom - ActualHeight - 8;
        }
    }

    /// <summary>Sticks the pill to the right edge on release, keeping its vertical position.</summary>
    private void SnapToEdge()
    {
        UpdateLayout();
        var area = SystemParameters.WorkArea;
        Left = area.Right - ActualWidth;
        Top = Math.Clamp(Top, area.Top, area.Bottom - ActualHeight);
        _viewModel.Settings.ProgressPanelLeft = Left;
        _viewModel.Settings.ProgressPanelTop = Top;
        _viewModel.PersistSettings();
    }

    private void Window_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var r = new Rect(0, 0, ClipGrid.ActualWidth, ClipGrid.ActualHeight);
        var clip = new System.Windows.Media.RectangleGeometry(r, 15, 15);
        clip.Freeze();
        ClipGrid.Clip = clip;
    }

    /// <summary>Smooth dragging via mouse capture: the pill tracks the cursor's absolute
    /// delta from the grab point (no feedback loop, so no jitter), then sticks to the
    /// right edge on release.</summary>
    private void Pill_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var cursor = System.Windows.Forms.Cursor.Position;
        var toDip = PresentationSource.FromVisual(this)?.CompositionTarget.TransformFromDevice
                    ?? System.Windows.Media.Matrix.Identity;
        _dragStartCursor = toDip.Transform(new Point(cursor.X, cursor.Y));
        _dragStartWindow = new Point(Left, Top);
        _dragging = true;
        CaptureMouse();
        e.Handled = true;
    }

    private void Pill_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragging)
            return;
        var cursor = System.Windows.Forms.Cursor.Position;
        var toDip = PresentationSource.FromVisual(this)?.CompositionTarget.TransformFromDevice
                    ?? System.Windows.Media.Matrix.Identity;
        var pos = toDip.Transform(new Point(cursor.X, cursor.Y));
        Left = _dragStartWindow.X + (pos.X - _dragStartCursor.X);
        Top = _dragStartWindow.Y + (pos.Y - _dragStartCursor.Y);
        e.Handled = true;
    }

    private void Pill_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging)
            return;
        _dragging = false;
        ReleaseMouseCapture();
        SnapToEdge();
        e.Handled = true;
    }

    private void Engine_TaskChanged()
    {
        Dispatcher.BeginInvoke(Refresh);
    }

    private void Refresh()
    {
        if (!IsVisible)
            return;

        var active = _viewModel.Tasks.FirstOrDefault(t => t.Status == TaskStatus.Downloading)
            ?? _viewModel.Tasks.FirstOrDefault(t => t.Status == TaskStatus.Queued);
        if (active is null)
        {
            Task = null;
            OnPropertyChanged(nameof(StatusText));
            return;
        }

        Task = active;
        OnPropertyChanged(nameof(StatusText));
    }

    protected override void OnClosed(EventArgs e)
    {
        _refreshTimer.Stop();
        _viewModel.Engine.TaskChanged -= Engine_TaskChanged;
        base.OnClosed(e);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}