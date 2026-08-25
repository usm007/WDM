using System.IO;
using System.IO.Compression;
using System.Net.Http;

namespace WDM.Services;

public sealed record EngineProgress(string StatusText, double ProgressFraction);

public sealed class EngineMissingException : Exception
{
    public EngineMissingException(string message) : base(message) { }
}

public static class EngineManager
{
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromMinutes(8)
    };

    static EngineManager()
    {
        Http.DefaultRequestHeaders.UserAgent.ParseAdd("WDM/2.2 (+https://github.com/usm007/WDM)");
    }

    public static string DataFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WDM");

    public static string BinDir => Path.Combine(DataFolder, "bin");
    public static string SeedsDir => Path.Combine(AppContext.BaseDirectory, "engines");

    public static string YtDlpPath => FindInPath("yt-dlp.exe") ?? Path.Combine(BinDir, "yt-dlp.exe");
    public static string FfmpegPath => FindInPath("ffmpeg.exe") ?? Path.Combine(BinDir, "ffmpeg.exe");
    public static string FfprobePath => FindInPath("ffprobe.exe") ?? Path.Combine(BinDir, "ffprobe.exe");
    public static string QuickJsPath => FindInPath("qjs.exe") ?? Path.Combine(BinDir, "qjs.exe");

    public static string? FindInPath(string exeName)
    {
        var localBin = Path.Combine(BinDir, exeName);
        if (File.Exists(localBin))
            return localBin;

        var seedBin = Path.Combine(SeedsDir, exeName);
        if (File.Exists(seedBin))
            return seedBin;

        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(pathEnv))
            return null;

        foreach (var dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var full = Path.Combine(dir.Trim(), exeName);
                if (File.Exists(full))
                    return full;
            }
            catch
            {
                // ignore path errors
            }
        }
        return null;
    }

    public static bool IsReady => File.Exists(YtDlpPath) && File.Exists(FfmpegPath);

    public static async Task EnsureAsync(IProgress<EngineProgress>? progress, CancellationToken ct = default)
    {
        Directory.CreateDirectory(BinDir);

        if (!File.Exists(YtDlpPath) && !TrySeed("yt-dlp.exe"))
            await DownloadYtDlpAsync(progress, 0, 0.20, ct);

        if (!File.Exists(QuickJsPath) && !TrySeed("qjs.exe"))
            await DownloadQuickJsAsync(progress, 0.20, 0.10, ct);

        if (!File.Exists(FfmpegPath))
        {
            if (!TrySeed("ffmpeg.exe") || !TrySeed("ffprobe.exe"))
                await DownloadFfmpegAsync(progress, 0.30, 0.70, ct);
        }
        else if (!File.Exists(FfprobePath) && !TrySeed("ffprobe.exe"))
        {
            await DownloadFfmpegAsync(progress, 0.30, 0.70, ct);
        }

        var version = await GetVersionAsync(ct);
        progress?.Report(new EngineProgress($"Engine ready — yt-dlp {version} · ffmpeg", 1.0));
    }

    private static bool TrySeed(string fileName)
    {
        try
        {
            var src = Path.Combine(SeedsDir, fileName);
            if (!File.Exists(src))
                return false;
            File.Copy(src, Path.Combine(BinDir, fileName), overwrite: true);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static async Task<string> GetVersionAsync(CancellationToken ct = default)
    {
        if (!File.Exists(YtDlpPath))
            return "not installed";

        try
        {
            using var proc = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = YtDlpPath,
                Arguments = "--version",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });

            if (proc is null)
                return "not installed";

            var readTask = proc.StandardOutput.ReadToEndAsync();
            var completed = await Task.WhenAny(readTask, Task.Delay(TimeSpan.FromSeconds(15), ct));
            var output = completed == readTask ? await readTask : null;
            try { proc.Kill(true); } catch { }
            return (output ?? "").Trim().Split('\n')[0].Trim();
        }
        catch
        {
            return "not installed";
        }
    }

    private static async Task DownloadYtDlpAsync(
        IProgress<EngineProgress>? progress,
        double start,
        double span,
        CancellationToken ct)
    {
        const string url = "https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe";
        var tmp = YtDlpPath + ".tmp";
        await DownloadToFileAsync(url, tmp, progress, "Downloading yt-dlp…", start, span, ct);
        File.Move(tmp, YtDlpPath, overwrite: true);
    }

    private static async Task DownloadQuickJsAsync(
        IProgress<EngineProgress>? progress,
        double start,
        double span,
        CancellationToken ct)
    {
        var arch = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture
            == System.Runtime.InteropServices.Architecture.X86
                ? "x86"
                : "x86_64";
        var url = $"https://github.com/quickjs-ng/quickjs/releases/latest/download/qjs-windows-{arch}.exe";
        var tmp = QuickJsPath + ".tmp";

        progress?.Report(new EngineProgress("Downloading QuickJS (lightweight JS runtime)…", start));
        await DownloadToFileAsync(url, tmp, progress, "Downloading QuickJS (JS runtime)…", start, start + span, ct);

        File.Move(tmp, QuickJsPath, overwrite: true);
        progress?.Report(new EngineProgress("QuickJS engine ready", start + span));
    }

    private static async Task DownloadFfmpegAsync(
        IProgress<EngineProgress>? progress,
        double start,
        double span,
        CancellationToken ct)
    {
        const string primaryUrl = "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip";
        const string fallbackUrl = "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-win64-gpl.zip";
        var tmp = Path.Combine(BinDir, "ffmpeg.zip");

        progress?.Report(new EngineProgress("Downloading FFmpeg (essentials build)…", start));

        try
        {
            await DownloadToFileAsync(primaryUrl, tmp, progress, "Downloading FFmpeg…", start, start + span * 0.9, ct);
        }
        catch
        {
            await DownloadToFileAsync(fallbackUrl, tmp, progress, "Downloading FFmpeg (fallback)…", start, start + span * 0.9, ct);
        }

        progress?.Report(new EngineProgress("Extracting ffmpeg…", start + span * 0.92));
        var extractDir = Path.Combine(BinDir, "ffmpeg-extract");
        if (Directory.Exists(extractDir))
            Directory.Delete(extractDir, true);
        Directory.CreateDirectory(extractDir);

        await Task.Run(() => ZipFile.ExtractToDirectory(tmp, extractDir), ct);

        var ffmpeg = Directory
            .GetFiles(extractDir, "ffmpeg.exe", SearchOption.AllDirectories)
            .FirstOrDefault();
        var ffprobe = Directory
            .GetFiles(extractDir, "ffprobe.exe", SearchOption.AllDirectories)
            .FirstOrDefault();

        if (ffmpeg is null || ffprobe is null)
            throw new EngineMissingException("Could not find ffmpeg in the downloaded archive.");

        File.Move(ffmpeg, FfmpegPath, overwrite: true);
        File.Move(ffprobe, Path.Combine(BinDir, "ffprobe.exe"), overwrite: true);

        try { File.Delete(tmp); Directory.Delete(extractDir, true); } catch { }
        progress?.Report(new EngineProgress("Extracting ffmpeg…", start + span));
    }

    private static async Task DownloadToFileAsync(
        string url,
        string dest,
        IProgress<EngineProgress>? progress,
        string stage,
        double start,
        double span,
        CancellationToken ct)
    {
        using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        var total = response.Content.Headers.ContentLength ?? 0;
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        await using var file = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);

        var buffer = new byte[81920];
        long read = 0;
        int n;
        while ((n = await stream.ReadAsync(buffer, ct)) > 0)
        {
            await file.WriteAsync(buffer.AsMemory(0, n), ct);
            read += n;
            if (total > 0)
            {
                var pct = (double)read / total;
                progress?.Report(new EngineProgress(stage, start + span * pct));
            }
        }
    }
}
