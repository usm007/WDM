using System;

namespace WDM.Media;

/// <summary>External engine-binary locations (yt-dlp/ffmpeg/quickjs) for media
/// resolution and manifest downloading. The App wires these to EngineManager;
/// WDM.Media never references the App. Defaults fall back to PATH names.</summary>
public static class MediaEnvironment
{
    public static Func<string> YtDlpPath { get; set; } = () => "yt-dlp.exe";
    public static Func<string> FfmpegPath { get; set; } = () => "ffmpeg.exe";
    public static Func<string> QuickJsPath { get; set; } = () => "qjs.exe";
}
