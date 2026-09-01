using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using WDM.Services;

namespace WDM;

public partial class App : Application
{
    /// <summary>True when launched with /minimized (the Windows-startup shortcut from
    /// the installer): the window starts hidden in the system tray.</summary>
    public static bool StartMinimized { get; private set; }

    private static Mutex? _singleInstanceMutex;
    private static bool _ownsMutex;
    private const string MutexId = @"Local\WDM.SingleInstance.4F3B2C0A-8D2E-4B7A-9C1E-6A5B4D3E2F10";

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindowAsync(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetProcessDpiAwarenessContext(IntPtr dpiContext);

    private static readonly IntPtr DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = new IntPtr(-4);
    private const int SW_RESTORE = 9;

    protected override void OnStartup(StartupEventArgs e)
    {
        try
        {
            SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
        }
        catch
        {
            // Fallback on older Windows builds
        }

        AppDomain.CurrentDomain.UnhandledException += (s, args) =>
        {
            LogException(args.ExceptionObject as Exception);
        };
        DispatcherUnhandledException += (s, args) =>
        {
            LogException(args.Exception);
        };

        if (e.Args.Any(a => string.Equals(a, "--capture-screenshots", StringComparison.OrdinalIgnoreCase)))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            ScreenshotGenerator.Run();
            Shutdown();
            return;
        }

        // Single instance: if another WDM is already running, surface its window
        // instead of starting a second copy.
        try
        {
            _singleInstanceMutex = new Mutex(initiallyOwned: true, MutexId, out bool createdNew);
            _ownsMutex = createdNew;
            if (!createdNew)
            {
                BringExistingInstanceToFront();
                Shutdown();
                return;
            }
        }
        catch (DirectoryNotFoundException)
        {
            // Kernel object path resolution failed — fall back to unnamed mutex
            _singleInstanceMutex = new Mutex(initiallyOwned: true);
            _ownsMutex = true;
        }

        StartMinimized = e.Args.Any(a =>
            string.Equals(a, "/minimized", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(a, "--minimized", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(a, "/autostart", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(a, "--autostart", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(a, "/tray", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(a, "--tray", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(a, "/silent", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(a, "--silent", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(a, "/background", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(a, "-minimized", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(a, "-silent", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(a, "-tray", StringComparison.OrdinalIgnoreCase));
        BrowserIntegration.DeployExtension();
        var settings = TaskStore.LoadSettings();
        ThemeService.Apply(AppTheme.Default, settings.UseDarkTheme);

        if (!settings.HasPromptedExtensionInstall && !StartMinimized)
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var welcome = new WelcomeWindow(settings);
            welcome.ShowDialog();
            TaskStore.SaveSettings(settings);
            ShutdownMode = ShutdownMode.OnLastWindowClose;
        }

        Window mainWindow = new MainWindow();
        if (!StartMinimized)
        {
            mainWindow.Show();
        }
        else
        {
            // When started on Windows startup, stay silently in the taskbar tray.
            mainWindow.Hide();
        }
    }

    /// <summary>Finds a running WDM main window and restores + focuses it.</summary>
    private static void BringExistingInstanceToFront()
    {
        var current = System.Diagnostics.Process.GetCurrentProcess();
        foreach (var process in System.Diagnostics.Process.GetProcessesByName(current.ProcessName))
        {
            if (process.Id == current.Id || process.MainWindowHandle == IntPtr.Zero)
                continue;
            ShowWindowAsync(process.MainWindowHandle, SW_RESTORE);
            SetForegroundWindow(process.MainWindowHandle);
            break;
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_ownsMutex && _singleInstanceMutex is not null)
        {
            try { _singleInstanceMutex.ReleaseMutex(); }
            catch (Exception) { /* mutex was not owned by this thread or already released */ }
        }
        _singleInstanceMutex?.Dispose();
        base.OnExit(e);
    }

    public static void LogException(Exception? ex)
    {
        if (ex is null) return;
        try
        {
            string logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "wdm_error.log");
            string entry = $"[CRASH {DateTime.Now:O}]\n{ex}";
            if (ex.InnerException is not null)
                entry += $"\nInner:\n{ex.InnerException}";
            File.AppendAllText(logPath, entry + "\n\n");
        }
        catch
        {
            // Never let logging itself take down the crash handler.
        }
    }
}
