using System.IO;
using System.Windows;
using System.Windows.Threading;
using WDM.Services;

namespace WDM;

public partial class App : Application
{
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
        ThemeService.Apply(false);
    }

    public static void LogException(Exception? ex)
    {
        if (ex is null) return;
        string logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "wdm_error.log");
        File.WriteAllText(logPath, $"[CRASH {DateTime.Now}]\n{ex}\n\nInner:\n{ex.InnerException}");
    }
}
