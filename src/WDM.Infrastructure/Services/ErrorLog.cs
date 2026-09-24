using System;
using System.IO;

namespace WDM.Services;

/// <summary>Crash/error file log (%LocalAppData%/WDM-Data/wdm_error.log).
/// UI-free half of App.LogException: persistence and engine code report here;
/// the App adds its activity-log line on top. Never throws.</summary>
public static class ErrorLog
{
    public static void Write(Exception? ex)
    {
        if (ex is null)
            return;
        try
        {
            Directory.CreateDirectory(AppPaths.DataDir);
            string logPath = Path.Combine(AppPaths.DataDir, "wdm_error.log");
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
