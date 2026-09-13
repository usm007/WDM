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

    // Downloaded engines live with the rest of the user data (TaskStore.AppDir),
    // outside the install root so updates/uninstalls never wipe them.
    public static string DataFolder => TaskStore.AppDir;

    public static string BinDir => Path.Combine(DataFolder, "bin");
    public static string SeedsDir => Path.Combine(AppContext.BaseDirectory, "engines");

    public static string YtDlpPath => FindEngineBinary("yt-dlp.exe");
    public static string FfmpegPath => FindEngineBinary("ffmpeg.exe");
    public static string FfprobePath => FindEngineBinary("ffprobe.exe");
    public static string QuickJsPath => FindEngineBinary("qjs.exe");

    /// <summary>Prefers our own BinDir/seeds over PATH to avoid binary hijack.
    /// PATH is only a last resort and must pass <see cref="LooksLikeValidBinary"/>.</summary>
    public static string FindEngineBinary(string exeName)
    {
        var localBin = Path.Combine(BinDir, exeName);
        if (File.Exists(localBin))
            return localBin;

        var seedBin = Path.Combine(SeedsDir, exeName);
        if (File.Exists(seedBin))
            return seedBin;

        var fromPath = FindInPath(exeName);
        if (fromPath is not null && LooksLikeValidBinary(fromPath))
            return fromPath;

        return localBin;
    }

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

            var stdoutTask = proc.StandardOutput.ReadToEndAsync();
            var stderrTask = proc.StandardError.ReadToEndAsync();
            var completed = await Task.WhenAny(Task.WhenAll(stdoutTask, stderrTask), Task.Delay(TimeSpan.FromSeconds(15), ct));
            if (completed is not Task<string[]> allTask)
            {
                try { if (!proc.HasExited) proc.Kill(true); } catch { }
                try { await proc.WaitForExitAsync(ct); } catch { }
                return "not installed";
            }
            string output = (await allTask)[0];
            try { await proc.WaitForExitAsync(ct); } catch { }
            return (output ?? "").Trim().Split('\n')[0].Trim();
        }
        catch
        {
            return "not installed";
        }
    }

    private static bool LooksLikeValidBinary(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (info.Length < 100 * 1024)
                return false;
            using var fs = File.OpenRead(path);
            var header = new byte[2];
            if (fs.Read(header, 0, 2) != 2 || header[0] != 'M' || header[1] != 'Z')
                return false;
            return true;
        }
        catch { return false; }
    }

    private static async Task DownloadYtDlpAsync(
        IProgress<EngineProgress>? progress,
        double start,
        double span,
        CancellationToken ct)
    {
        const string url = "https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe";
        var tmp = Path.Combine(BinDir, $"yt-dlp-{Guid.NewGuid():N}.tmp");
        await DownloadToFileAsync(url, tmp, progress, "Downloading yt-dlp…", start, span, ct, maxBytes: 100 * 1024 * 1024);
        VerifyDownloadedBinary(tmp, minBytes: 5 * 1024 * 1024);
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
        var tmp = Path.Combine(BinDir, $"qjs-{Guid.NewGuid():N}.tmp");

        progress?.Report(new EngineProgress("Downloading QuickJS (lightweight JS runtime)…", start));
        await DownloadToFileAsync(url, tmp, progress, "Downloading QuickJS (JS runtime)…", start, start + span, ct, maxBytes: 50 * 1024 * 1024);

        VerifyDownloadedBinary(tmp, minBytes: 100 * 1024);
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
        var tmp = Path.Combine(BinDir, $"ffmpeg-{Guid.NewGuid():N}.zip");

        progress?.Report(new EngineProgress("Downloading FFmpeg (essentials build)…", start));

        try
        {
            await DownloadToFileAsync(primaryUrl, tmp, progress, "Downloading FFmpeg…", start, start + span * 0.9, ct, maxBytes: 300 * 1024 * 1024);
        }
        catch
        {
            await DownloadToFileAsync(fallbackUrl, tmp, progress, "Downloading FFmpeg (fallback)…", start, start + span * 0.9, ct, maxBytes: 300 * 1024 * 1024);
        }

        progress?.Report(new EngineProgress("Extracting ffmpeg…", start + span * 0.92));
        var extractDir = Path.Combine(BinDir, $"ffmpeg-extract-{Guid.NewGuid():N}");
        Directory.CreateDirectory(extractDir);

        try
        {
            await Task.Run(() => ExtractZipSafely(tmp, extractDir), ct);
        }
        catch
        {
            try { Directory.Delete(extractDir, true); } catch { }
            throw;
        }

        var ffmpeg = Directory
            .GetFiles(extractDir, "ffmpeg.exe", SearchOption.AllDirectories)
            .FirstOrDefault();
        var ffprobe = Directory
            .GetFiles(extractDir, "ffprobe.exe", SearchOption.AllDirectories)
            .FirstOrDefault();

        if (ffmpeg is null || ffprobe is null)
            throw new EngineMissingException("Could not find ffmpeg in the downloaded archive.");

        VerifyDownloadedBinary(ffmpeg, minBytes: 5 * 1024 * 1024);
        File.Move(ffmpeg, FfmpegPath, overwrite: true);
        File.Move(ffprobe, Path.Combine(BinDir, "ffprobe.exe"), overwrite: true);

        try { File.Delete(tmp); Directory.Delete(extractDir, true); } catch { }
        progress?.Report(new EngineProgress("Extracting ffmpeg…", start + span));
    }

    private static void ExtractZipSafely(string zipPath, string extractDir)
    {
        string fullBase = Path.GetFullPath(extractDir) + Path.DirectorySeparatorChar;
        using var archive = ZipFile.OpenRead(zipPath);
        long totalUncompressed = 0;
        foreach (var entry in archive.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name) && entry.FullName.EndsWith('/'))
                continue;
            string dest = Path.GetFullPath(Path.Combine(extractDir, entry.FullName));
            if (!dest.StartsWith(fullBase, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Archive contains an unsafe path — refusing extraction.");
            totalUncompressed += entry.Length;
            if (totalUncompressed > 1024L * 1024 * 1024)
                throw new InvalidOperationException("Archive too large — refusing extraction.");
        }
        ZipFile.ExtractToDirectory(zipPath, extractDir);
    }

    private static void VerifyDownloadedBinary(string path, long minBytes)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length < minBytes)
        {
            try { File.Delete(path); } catch { }
            throw new EngineMissingException($"Downloaded engine failed validation ({info.Length} bytes).");
        }
        using var fs = File.OpenRead(path);
        var header = new byte[2];
        if (fs.Read(header, 0, 2) != 2 || header[0] != 'M' || header[1] != 'Z')
        {
            try { File.Delete(path); } catch { }
            throw new EngineMissingException("Downloaded engine is not a valid Windows executable.");
        }
    }

    private static bool IsTrustedEngineUrl(string url)
    {
        try
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
                return false;
            string host = uri.Host.ToLowerInvariant();
            return host == "github.com" || host.EndsWith(".github.com", StringComparison.Ordinal) ||
                   host == "objects.githubusercontent.com" || host == "release-assets.githubusercontent.com" ||
                   host == "www.gyan.dev" || host == "gyan.dev";
        }
        catch { return false; }
    }

    private static async Task DownloadToFileAsync(
        string url,
        string dest,
        IProgress<EngineProgress>? progress,
        string stage,
        double start,
        double span,
        CancellationToken ct,
        long maxBytes = 300 * 1024 * 1024)
    {
        if (!IsTrustedEngineUrl(url))
            throw new InvalidOperationException("Refusing to download engine from untrusted host.");
        Directory.CreateDirectory(Path.GetDirectoryName(dest) ?? BinDir);
        using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        var total = response.Content.Headers.ContentLength ?? 0;
        if (total > maxBytes)
            throw new InvalidOperationException("Engine download too large — refusing.");
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        await using var file = new FileStream(dest, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true);

        var buffer = new byte[81920];
        long read = 0;
        int n;
        while ((n = await stream.ReadAsync(buffer, ct)) > 0)
        {
            await file.WriteAsync(buffer.AsMemory(0, n), ct);
            read += n;
            if (read > maxBytes)
                throw new InvalidOperationException("Engine download exceeded size limit.");
            if (total > 0)
            {
                var pct = (double)read / total;
                progress?.Report(new EngineProgress(stage, start + span * pct));
            }
        }
    }
}
