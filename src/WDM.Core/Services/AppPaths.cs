using System;
using System.IO;

namespace WDM.Services;

/// <summary>User data home (tasks.json, settings.json, cookies, engines).
/// Deliberately OUTSIDE the install root. Plain static (not readonly) so
/// tests/tools can redirect it; app code must treat it as read-only after
/// startup. Honors WDM_DATA_DIR / --data-dir redirection.</summary>
public static class AppPaths
{
    public static string DataDir = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("WDM_DATA_DIR"))
        ? Path.GetFullPath(Environment.GetEnvironmentVariable("WDM_DATA_DIR")!)
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WDM-Data");
}
