using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using WDM.Media;
using WDM.Services;

namespace WDM;

public partial class App : Application
{
    /// <summary>True when launched with /minimized (the Windows-startup shortcut from
    /// the installer): the window starts hidden in the system tray.</summary>
    public static bool StartMinimized { get; private set; }
    public static bool IsTestMode { get; private set; }

    /// <summary>Launch flags that mean "stay silent in the tray" (autostart entries,
    /// MinimizeToTray relaunch). Forwarded bare-flag args must never restore the window.</summary>
    private static readonly string[] SilentFlags =
    {
        "/minimized", "--minimized", "-minimized",
        "/autostart", "--autostart",
        "/tray", "--tray", "-tray",
        "/silent", "--silent", "-silent",
        "/background",
    };

    public static bool IsSilentFlag(string? arg) =>
        !string.IsNullOrWhiteSpace(arg) &&
        SilentFlags.Any(f => string.Equals(f, arg.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>True when the args carry an actionable URL (browser "download with WDM"
    /// handoff). Bare-flag launches (e.g. boot /minimized) must stay silent.</summary>
    public static bool ArgsContainUrl(string[]? args) =>
        args is not null && args.Any(a =>
            !string.IsNullOrWhiteSpace(a) &&
            (a.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
             a.StartsWith("https://", StringComparison.OrdinalIgnoreCase)));

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
        // Test and data isolation support
        for (int i = 0; i < e.Args.Length; i++)
        {
            if (string.Equals(e.Args[i], "--data-dir", StringComparison.OrdinalIgnoreCase) && i + 1 < e.Args.Length)
            {
                TaskStore.AppDir = Path.GetFullPath(e.Args[i + 1]);
                break;
            }
        }
        bool isTestMode = e.Args.Any(a => string.Equals(a, "--test-mode", StringComparison.OrdinalIgnoreCase) ||
                                          string.Equals(a, "--no-single-instance", StringComparison.OrdinalIgnoreCase)) ||
                          !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WDM_TEST_MODE"));
        IsTestMode = isTestMode;
        ActivityLog.Start(UpdateChecker.CurrentVersion.ToString(), e.Args, StartMinimized);

        // Single instance: if another WDM is already running, surface its window
        // instead of starting a second copy (unless in test mode where isolated instances are required).
        string effectiveMutexId = isTestMode ? $@"Local\WDM.TestInstance.{Environment.ProcessId}" : MutexId;
        try
        {
            _singleInstanceMutex = new Mutex(initiallyOwned: true, effectiveMutexId, out bool createdNew);
            _ownsMutex = createdNew;
            if (!createdNew && !isTestMode)
            {
                // Duplicate boot entry (HKCU + HKLM Run) or user relaunch: a bare-flag
                // handoff (e.g. /minimized, no URL) must exit quietly — forwarding it
                // would restore the main window and break silent autostart.
                // Only forward actionable args (a download URL); focus-steal is the
                // fallback for interactive relaunches, never for silent ones.
                if (ArgsContainUrl(e.Args))
                {
                    try { SingleInstancePipe.TrySendArgs(SingleInstancePipe.PipeNameForCurrentSession(), e.Args, 800); }
                    catch { }
                    BringExistingInstanceToFront();
                }
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
        // Local activity log (testing): silent file log, real launches only
        // (screenshot mode returned earlier and never reaches here).
        ActivityLog.Start(UpdateChecker.CurrentVersion.ToString(), e.Args, StartMinimized);
        ActivityLog.Write("ARGS", ArgsContainUrl(e.Args) ? "launch carries download URL(s)" : "no URL args");
        if (!isTestMode)
            _ = System.Threading.Tasks.Task.Run(() =>
            {
                try { BrowserIntegration.DeployExtension(); }
                catch { /* best-effort: on-demand paths re-deploy; never block first paint */ }
            });
        // Migrate user data out of the legacy install-root location first, so every
        // read below (settings, tasks, engines, WebView2 profile) hits the new home.
        if (!isTestMode)
            TaskStore.EnsureMigrated();
        var settings = TaskStore.LoadSettings();
        // Wire the UI-free Media subsystem to live settings/engines. Media never
        // touches TaskStore/EngineManager directly (static providers, test-settable).
        MediaSettings.Provider = TaskStore.LoadSettings;
        MediaEnvironment.YtDlpPath = () => EngineManager.YtDlpPath;
        MediaEnvironment.FfmpegPath = () => EngineManager.FfmpegPath;
        MediaEnvironment.QuickJsPath = () => EngineManager.QuickJsPath;
        EngineManager.VersionProvider = () => UpdateChecker.CurrentVersion;
        DownloadEngine.MegaSidProvider = () => CaptureServer.TryGetMegaSid(out string? sid) ? sid : null;
        ThemeService.Apply(AppTheme.Default, settings.UseDarkTheme);

        // Never show welcome after an update — only on true first-ever run.
        // In test mode, suppress modal welcome unless --welcome argument is passed.
        bool isFirstEverRun = InstallState.IsFirstEverRun(settings);
        bool shouldShowWelcome = isFirstEverRun && !StartMinimized && (!isTestMode || e.Args.Contains("--welcome"));
        if (shouldShowWelcome)
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
        MainWindow = mainWindow;
        _pipeCts = new CancellationTokenSource();
        string pipeName = isTestMode
            ? $"{SingleInstancePipe.PipeNameForCurrentSession()}.{Environment.ProcessId}"
            : SingleInstancePipe.PipeNameForCurrentSession();
        SingleInstancePipe.Start(pipeName,
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

        // A boot/autostart launch carries only silent flags (e.g. /minimized):
        // invoking the handler would RestoreWindow() and pop the big window.
        // Only forward actionable args (a download URL) to the fresh window.
        if (ArgsContainUrl(e.Args))
        {
            SecondInstanceHandler?.Invoke(e.Args);
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
        try { ActivityLog.Stop(); } catch { }
        try { _pipeCts?.Cancel(); } catch { }
        _pipeCts?.Dispose();
        _pipeCts = null;
        if (_ownsMutex && _singleInstanceMutex is not null)
        {
            try { _singleInstanceMutex.ReleaseMutex(); }
            catch (Exception) { /* mutex was not owned by this thread or already released */ }
        }
        _singleInstanceMutex?.Dispose();
        ActivityLog.Stop();
        base.OnExit(e);
    }

    public static void LogException(Exception? ex)
    {
        if (ex is null) return;
        try { ActivityLog.Write("ERROR", ex.GetType().Name + ": " + ex.Message); } catch { }
        ErrorLog.Write(ex);
    }
}
