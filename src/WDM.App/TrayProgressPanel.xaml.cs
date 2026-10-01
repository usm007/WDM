using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using WDM.Models;
using WDM.ViewModels;

namespace WDM;

/// <summary>
/// Compact pill: download icon + active-count badge + short % + short MB/s.
/// Natural number formatting (13%, 0.96 MB/s — never 05% or 00.96),
/// 2Hz updates, never blank.
/// </summary>
public partial class TrayProgressPanel : Window, INotifyPropertyChanged
{
    private readonly MainViewModel _viewModel;
    private readonly DispatcherTimer _refreshTimer;
    private DownloadTask? _task;
    private Point _dragStartCursor;
    private Point _dragStartWindow;
    private bool _dragging;
    private int _activeCount = 1;
    private double _heldSpeedBps;
    private long _heldSpeedTick;
    private long _lastRefreshTick;

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
        }
    }

    public int ActiveCount
    {
        get => _activeCount;
        private set
        {
            int v = Math.Max(1, value);
            if (_activeCount != v)
            {
                _activeCount = v;
                OnPropertyChanged(nameof(ActiveCount));
                OnPropertyChanged(nameof(ActiveCountText));
            }
        }
    }

    public string ActiveCountText => ActiveCount.ToString(System.Globalization.CultureInfo.InvariantCulture);

    public string StatusText
    {
        get
        {
            // Never blank: short "5% · 9.6 MB/s" shape, no zero-padding.
            if (_task is null)
                return "0% · 0 MB/s";
            int p = Math.Clamp(_task.Progress, 0, 100);
            string pct = p.ToString(System.Globalization.CultureInfo.InvariantCulture) + "%";
            double bps = _heldSpeedBps;
            // Prefer live speed when it is flowing.
            try
            {
                if (_task.Status == TaskStatus.Downloading && _task.SpeedBps >= 1)
                    bps = _task.SpeedBps;
            }
            catch { }
            return $"{pct} · {FormatSpeedMb(bps)}";
        }
    }

    // ponytail: MB-only, up to 2 decimals with trailing zeros trimmed
    // (0.96, 5, 112.4 — never 00.96 or 5.00). Upgrade if TB/s ever matters.
    private static string FormatSpeedMb(double bps)
    {
        double mb = Math.Max(0, bps) / 1024.0 / 1024.0;
        string num = mb.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
        return $"{num} MB/s";
    }

    /// <summary>Shows the indicator snapped to the nearest side edge (default bottom-right corner).</summary>
    public void ShowPanel(DownloadTask task)
    {
        Task = task;
        _refreshTimer.Start();
        Refresh();
        if (!IsVisible)
            Show();
        RestorePosition();
        SnapToEdge();
    }

    public void HidePanel()
    {
        _refreshTimer.Stop();
        Hide();
    }

    private void RestorePosition()
    {
        var settings = _viewModel.Settings;
        var area = SystemParameters.WorkArea;
        double width = ActualWidth > 0 ? ActualWidth : 128;
        double height = ActualHeight > 0 ? ActualHeight : 30;
        if (settings.ProgressPanelLeft is double left && settings.ProgressPanelTop is double top)
        {
            // Keep it on screen in case the display changed.
            double maxLeft = Math.Max(area.Left, area.Right - width);
            double maxTop = Math.Max(area.Top, area.Bottom - height);
            left = Math.Clamp(left, area.Left, maxLeft);
            top = Math.Clamp(top, area.Top, maxTop);
            Left = left;
            Top = top;
        }
        else
        {
            Left = Math.Max(area.Left, area.Right - width - 8);
            Top = Math.Max(area.Top, area.Bottom - height - 8);
        }
    }

    /// <summary>Sticks the indicator to the right edge on release, keeping its vertical position.</summary>
    private void SnapToEdge()
    {
        try { UpdateLayout(); } catch { }
        var area = SystemParameters.WorkArea;
        double width = ActualWidth > 0 ? ActualWidth : 128;
        double height = ActualHeight > 0 ? ActualHeight : 30;
        Left = Math.Max(area.Left, area.Right - width);
        double maxTop = Math.Max(area.Top, area.Bottom - height);
        Top = Math.Clamp(Top, area.Top, maxTop);
        try
        {
            _viewModel.Settings.ProgressPanelLeft = Left;
            _viewModel.Settings.ProgressPanelTop = Top;
            _viewModel.PersistSettings();
        }
        catch { }
    }

    /// <summary>Smooth dragging via mouse capture: the pill tracks the cursor's absolute
    /// delta from the grab point (no feedback loop, so no jitter), then sticks to the
    /// right edge on release.</summary>
    private void Panel_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            HidePanel();
            e.Handled = true;
        }
    }

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
        // Debounce bursty engine ticks to 2Hz max — rapid calc was blanking the text.
        if (!IsVisible)
            return;
        long now = Environment.TickCount64;
        if (now - _lastRefreshTick < 400)
            return;
        _lastRefreshTick = now;
        Dispatcher.BeginInvoke(Refresh);
    }

    private void Refresh()
    {
        if (!IsVisible)
            return;
        _lastRefreshTick = Environment.TickCount64;

        int count = 0;
        try { count = _viewModel.Tasks.Count(t => t.Status == TaskStatus.Downloading); } catch { }
        ActiveCount = Math.Max(1, count);

        var active = _viewModel.Tasks.FirstOrDefault(t => t.Status == TaskStatus.Downloading)
            ?? _viewModel.Tasks.FirstOrDefault(t => t.Status == TaskStatus.Queued);
        if (active is null)
        {
            Task = null;
            OnPropertyChanged(nameof(StatusText));
            OnPropertyChanged(nameof(ActiveCountText));
            return;
        }

        // Hold last flowing speed ~2s so 0-ticks don't blank/flicker the number.
        try
        {
            if (active.Status == TaskStatus.Downloading && active.SpeedBps >= 1)
            {
                _heldSpeedBps = active.SpeedBps;
                _heldSpeedTick = Environment.TickCount64;
            }
            else if (Environment.TickCount64 - _heldSpeedTick > 2000)
            {
                _heldSpeedBps = 0;
            }
        }
        catch { }

        Task = active;
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(ActiveCountText));
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
