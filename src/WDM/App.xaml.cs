using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Threading;
using WDM.Services;

namespace WDM;

public partial class App : Application
{
    /// <summary>True when launched with /minimized (the Windows-startup shortcut from
    /// the installer): the window starts hidden in the system tray.</summary>
    public static bool StartMinimized { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        AppDomain.CurrentDomain.UnhandledException += (s, args) =>
        {
            LogException(args.ExceptionObject as Exception);
        };
        DispatcherUnhandledException += (s, args) =>
        {
            LogException(args.Exception);
        };
        base.OnStartup(e);
        StartMinimized = e.Args.Any(a =>
            string.Equals(a, "/minimized", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(a, "--minimized", StringComparison.OrdinalIgnoreCase));
        var settings = TaskStore.LoadSettings();
        ThemeService.Apply(settings.Theme, settings.UseDarkTheme);

        // Create the appropriate main window for the selected theme family
        // Default = Modern Grey (WDM-2) — WdmOriginal = Vibrant WDM (imported whole UI)
        Window mainWindow = settings.Theme == AppTheme.WdmOriginal
            ? new WdmOriginalMainWindow()
            : new MainWindow();
        mainWindow.Show();
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
