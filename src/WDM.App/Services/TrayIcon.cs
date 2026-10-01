namespace WDM.Services;

/// <summary>
/// Thin wrapper around the WinForms NotifyIcon so the rest of the app stays WPF-only.
/// Idle tooltip is just "WDM"; while downloads run it shows count + average percent.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private readonly System.Windows.Forms.NotifyIcon _icon;
    private readonly System.Windows.Forms.ContextMenuStrip _menu;
    private readonly System.Windows.Forms.ToolStripMenuItem _resumeAllItem;
    private Action? _balloonClickAction;
    private bool _disposed;

    public event Action? Activated;
    public event Action? NewDownloadRequested;

    public TrayIcon()
    {
        _menu = new System.Windows.Forms.ContextMenuStrip();
        _resumeAllItem = new System.Windows.Forms.ToolStripMenuItem("Resume All") { Enabled = false };
        _resumeAllItem.Click += (_, _) => ResumeAllRequested?.Invoke();
        _menu.Items.Add("Open WDM", null, (_, _) => Activated?.Invoke());
        _menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        _menu.Items.Add("New Download...", null, (_, _) => NewDownloadRequested?.Invoke());
        _menu.Items.Add("Pause All", null, (_, _) => PauseAllRequested?.Invoke());
        _menu.Items.Add(_resumeAllItem);
        _menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        _menu.Items.Add("Exit", null, (_, _) => ExitRequested?.Invoke());

        _icon = new System.Windows.Forms.NotifyIcon
        {
            Icon = AppIcon.Tray ?? RuntimeFallbackIcon(),
            Text = "WDM",
            Visible = true,
            ContextMenuStrip = _menu,
        };
        _icon.DoubleClick += (_, _) => Activated?.Invoke();
        // Single left-click restores WDM (right-click keeps opening the menu).
        // DoubleClick stays wired: it fires two clicks + double-click, and
        // restoring twice is harmless (idempotent).
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == System.Windows.Forms.MouseButtons.Left)
                Activated?.Invoke();
        };
        _icon.BalloonTipClicked += (_, _) =>
        {
            _balloonClickAction?.Invoke();
            _balloonClickAction = null;
        };
    }

    public event Action? PauseAllRequested;
    public event Action? ResumeAllRequested;
    public event Action? ExitRequested;

    /// <summary>Active downloads: "1 download – 42%" or "N downloads – 42%" (average).</summary>
    public void SetProgress(int percent, string speedText, string fileName, int queued = 0, int paused = 0, int activeCount = 1)
    {
        percent = Math.Clamp(percent, 0, 100);
        if (activeCount < 1)
            activeCount = 1;
        _icon.Text = activeCount == 1 ? $"1 download – {percent}%" : $"{activeCount} downloads – {percent}%";
        _resumeAllItem.Enabled = queued > 0 || paused > 0;
    }

    /// <summary>Idle state: just "WDM".</summary>
    public void SetActiveCount(int active, int queued, long speedBps = 0, int paused = 0)
    {
        _icon.Text = "WDM";
        _resumeAllItem.Enabled = queued > 0 || paused > 0;
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
        if (_disposed)
            return;
        _disposed = true;
        _icon.Visible = false;
        _icon.Dispose();
        _menu.Dispose();
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);

    private static System.Drawing.Icon RuntimeFallbackIcon()
    {
        // Only reached if the bundled icon asset is missing entirely.
        const int size = 32;
        using var bmp = new System.Drawing.Bitmap(size, size);
        using (var g = System.Drawing.Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using var bg = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(15, 108, 189));
            g.FillRoundedRect(bg, 2, 2, size - 4, size - 4, 7);
            using var pen = new System.Drawing.Pen(System.Drawing.Color.White, 2.4f);
            g.DrawLine(pen, size / 2f, 8, size / 2f, size - 11);
            g.DrawLine(pen, size / 2f - 6, size - 15, size / 2f, size - 9);
            g.DrawLine(pen, size / 2f + 6, size - 15, size / 2f, size - 9);
        }
        IntPtr hIcon = bmp.GetHicon();
        try
        {
            return (System.Drawing.Icon)System.Drawing.Icon.FromHandle(hIcon).Clone();
        }
        finally
        {
            DestroyIcon(hIcon);
        }
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
