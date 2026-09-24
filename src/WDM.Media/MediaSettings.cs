using System;
using WDM.Services;

namespace WDM.Media;

/// <summary>Settings snapshot for media resolution. The App wires the provider
/// to TaskStore at startup; tests assign lambdas directly. Keeps WDM.Media
/// free of persistence and UI dependencies (static-provider pattern, same as
/// YtDlpRunner.RunnerOverride).</summary>
public static class MediaSettings
{
    public static Func<AppSettings> Provider { get; set; } = () => new AppSettings();

    public static AppSettings Current => Provider();
}
