using System.Windows;
using System.Windows.Media;
using WDM.Models;

namespace WDM.Services;

/// <summary>
/// Thin wrapper around the WinForms NotifyIcon so the rest of the app stays WPF-only.
/// </summary>
    public sealed class TrayIcon : IDisposable
{
    private readonly System.Windows.Forms.NotifyIcon _icon;
    private readonly System.Windows.Forms.ToolStripMenuItem _clipboardItem;
    private readonly System.Windows.Forms.ToolStripMenuItem _resumeAllItem;
    private Action? _balloonClickAction;

    public event Action? Activated;
    public event Action? NewDownloadRequested;

    public TrayIcon()
    {
        var menu = new System.Windows.Forms.ContextMenuStrip();
        _resumeAllItem = new System.Windows.Forms.ToolStripMenuItem("Resume All") { Enabled = false };
        _resumeAllItem.Click += (_, _) => ResumeAllRequested?.Invoke();
        menu.Items.Add("Open WDM", null, (_, _) => Activated?.Invoke());
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add("New Download...", null, (_, _) => NewDownloadRequested?.Invoke());
        menu.Items.Add("Pause All", null, (_, _) => PauseAllRequested?.Invoke());
        menu.Items.Add(_resumeAllItem);
        _clipboardItem = new System.Windows.Forms.ToolStripMenuItem("Monitor Clipboard") { CheckOnClick = true, Checked = false };
        _clipboardItem.CheckedChanged += (_, _) => ClipboardMonitoringChanged?.Invoke(_clipboardItem.Checked);
        menu.Items.Add(_clipboardItem);
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitRequested?.Invoke());

        _icon = new System.Windows.Forms.NotifyIcon
        {
            Icon = AppIcon.Tray ?? RuntimeFallbackIcon(),
            Text = "WDM — Download Manager",
            Visible = true,
            ContextMenuStrip = menu,
        };
        _icon.DoubleClick += (_, _) => Activated?.Invoke();
        _icon.BalloonTipClicked += (_, _) =>
        {
            _balloonClickAction?.Invoke();
            _balloonClickAction = null;
        };
    }

    public event Action? PauseAllRequested;
    public event Action? ResumeAllRequested;
    public event Action<bool>? ClipboardMonitoringChanged;
    public event Action? ExitRequested;

    public bool ClipboardMonitoring
    {
        get => _clipboardItem.Checked;
        set
        {
            _clipboardItem.CheckedChanged -= ClipboardChanged;
            _clipboardItem.Checked = value;
            _clipboardItem.CheckedChanged += ClipboardChanged;
        }
    }

    private void ClipboardChanged(object? sender, EventArgs e) =>
        ClipboardMonitoringChanged?.Invoke(_clipboardItem.Checked);

    public void SetActiveCount(int active, int queued, long speedBps = 0)
    {
        bool hasWork = active > 0 || queued > 0;
        string label;
        if (hasWork)
        {
            string speed = speedBps > 0 ? $"{DownloadTask.FormatBytes(speedBps)}/s" : "0 B/s";
            label = $"Downloading: {active} · Queued: {queued} · {speed}";
        }
        else
        {
            label = "WDM — Download Manager";
        }
        _icon.Text = label.Length <= 63 ? label : label[..63];
        _resumeAllItem.Enabled = queued > 0;
    }

    public void ShowBalloon(string title, string text)
    {
        _balloonClickAction = null;
        _icon.ShowBalloonTip(4000, title, text, System.Windows.Forms.ToolTipIcon.Info);
    }

    public void ShowBalloon(string title, string text, Action onClick)
    {
        _balloonClickAction = onClick;
        _icon.ShowBalloonTip(4000, title, text, System.Windows.Forms.ToolTipIcon.Info);
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }

    private static System.Drawing.Icon RuntimeFallbackIcon()
    {
        // Only reached if the bundled icon asset is missing entirely.
        const int size = 32;
        var bmp = new System.Drawing.Bitmap(size, size);
        using (var g = System.Drawing.Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            var bg = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(15, 108, 189));
            g.FillRoundedRect(bg, 2, 2, size - 4, size - 4, 7);
            using var pen = new System.Drawing.Pen(System.Drawing.Color.White, 2.4f);
            g.DrawLine(pen, size / 2f, 8, size / 2f, size - 11);
            g.DrawLine(pen, size / 2f - 6, size - 15, size / 2f, size - 9);
            g.DrawLine(pen, size / 2f + 6, size - 15, size / 2f, size - 9);
        }
        return System.Drawing.Icon.FromHandle(bmp.GetHicon());
    }
}

internal static class GraphicsExtensions
{
    public static void FillRoundedRect(this System.Drawing.Graphics g, System.Drawing.Brush brush,
        int x, int y, int w, int h, int r)
    {
        using var path = new System.Drawing.Drawing2D.GraphicsPath();
        int d = r * 2;
        path.AddArc(x, y, d, d, 180, 90);
        path.AddArc(x + w - d, y, d, d, 270, 90);
        path.AddArc(x + w - d, y + h - d, d, d, 0, 90);
        path.AddArc(x, y + h - d, d, d, 90, 90);
        path.CloseFigure();
        g.FillPath(brush, path);
    }
}
