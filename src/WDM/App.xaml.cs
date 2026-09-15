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

    /// <summary>Set by <see cref="MainWindow"/> so a second instance can restore
    /// this one (and forward URL arguments) over <see cref="Services.SingleInstancePipe"/>.</summary>
    public static Action<string[]>? SecondInstanceHandler { get; set; }
    private static CancellationTokenSource? _pipeCts;

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
        if (e.Args.Any(a => string.Equals(a, "--capture-screenshots", StringComparison.OrdinalIgnoreCase) || string.Equals(a, "--screenshots", StringComparison.OrdinalIgnoreCase)))
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
                // Forward our command line to the running instance (restores it
                // even when it is hidden to the tray with no HWND to find),
                // then exit. HWND focus-steal stays as a fallback.
                try { SingleInstancePipe.TrySendArgs(SingleInstancePipe.PipeNameForCurrentSession(), e.Args, 800); }
                catch { }
                BringExistingInstanceToFront();
                Shutdown();
                return;
            }
        }
        catch (AbandonedMutexException)
        {
            // Previous instance crashed while holding the mutex — we now own it.
            _ownsMutex = true;
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
        // Migrate user data out of the legacy install-root location first, so every
        // read below (settings, tasks, engines, WebView2 profile) hits the new home.
        TaskStore.EnsureMigrated();
        var settings = TaskStore.LoadSettings();
        ThemeService.Apply(AppTheme.Default, settings.UseDarkTheme);

        // Never show welcome after an update — only on true first-ever run.
        // InstallState looks beyond data files: an updater whose data was wiped
        // still leaves install evidence (Velopack state, app bits, Inno key),
        // so they get the reload notice instead of onboarding.
        bool isFirstEverRun = InstallState.IsFirstEverRun(settings);
        if (isFirstEverRun && !StartMinimized)
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var welcome = new WelcomeWindow(settings);
            welcome.ShowDialog();
            TaskStore.SaveSettings(settings);
            ShutdownMode = ShutdownMode.OnLastWindowClose;
        }
        else if (!settings.HasPromptedExtensionInstall && !isFirstEverRun)
        {
            // Returning user that never set the flag: upgraded from an older
            // version, or version stamp wiped but data/install evidence exists.
            // Suppress future welcome.
            settings.HasPromptedExtensionInstall = true;
            TaskStore.SaveSettings(settings);
        }

        Window mainWindow = new MainWindow();
        _pipeCts = new CancellationTokenSource();
        SingleInstancePipe.Start(SingleInstancePipe.PipeNameForCurrentSession(),
            args => SecondInstanceHandler?.Invoke(args), _pipeCts.Token);
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
        string exePath;
        try { exePath = current.MainModule?.FileName ?? ""; }
        catch { exePath = ""; }
        foreach (var process in System.Diagnostics.Process.GetProcessesByName(current.ProcessName))
        {
            if (process.Id == current.Id || process.MainWindowHandle == IntPtr.Zero)
                continue;
            // Same exe name isn't enough (unrelated same-named exe): require the
            // same binary path and a WDM main window before stealing focus.
            try
            {
                string other = process.MainModule?.FileName ?? "";
                if (!string.IsNullOrEmpty(exePath) && !string.IsNullOrEmpty(other) &&
                    !string.Equals(exePath, other, StringComparison.OrdinalIgnoreCase))
                    continue;
                string title = process.MainWindowTitle ?? "";
                if (!title.Contains("WDM", StringComparison.OrdinalIgnoreCase) &&
                    !title.Contains("Download Manager", StringComparison.OrdinalIgnoreCase))
                    continue;
            }
            catch { continue; }
            ShowWindowAsync(process.MainWindowHandle, SW_RESTORE);
            SetForegroundWindow(process.MainWindowHandle);
            break;
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { _pipeCts?.Cancel(); } catch { }
        _pipeCts?.Dispose();
        _pipeCts = null;
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
            Directory.CreateDirectory(TaskStore.AppDir);
            string logPath = Path.Combine(TaskStore.AppDir, "wdm_error.log");
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
