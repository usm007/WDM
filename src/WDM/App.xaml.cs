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
        ThemeService.Apply(false);
    }

    public static void LogException(Exception? ex)
    {
        if (ex is null) return;
        string logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "wdm_error.log");
        File.WriteAllText(logPath, $"[CRASH {DateTime.Now}]\n{ex}\n\nInner:\n{ex.InnerException}");
    }
}
