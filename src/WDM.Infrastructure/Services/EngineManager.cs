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
    /// <summary>App version for the engine-download UA. The App wires this to
    /// UpdateChecker at startup; the default (entry assembly) is identical in
    /// production. Static provider, same pattern as the Media subsystem.</summary>
    public static Func<Version> VersionProvider { get; set; } = () =>
        System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(1, 0, 0, 0);

    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromMinutes(8)
    };

    static EngineManager()
    {
        Http.DefaultRequestHeaders.UserAgent.ParseAdd($"WDM/{VersionProvider()} (+https://github.com/usm007/WDM)");
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

        // Startup re-verification: pinned binaries that changed on disk are
        // discarded and re-downloaded (tamper or bit-rot can never persist).
        foreach (string exe in new[] { YtDlpPath, QuickJsPath, FfmpegPath, FfprobePath })
        {
            try
            {
                if (File.Exists(exe) && !VerifyInstalledHash(exe))
                {
                    try { ActivityLog.Write("ENGINE", $"{Path.GetFileName(exe)}: installed hash mismatch — re-downloading"); } catch { }
                    try { File.Delete(exe); } catch { }
                    try { File.Delete(exe + ".sha256"); } catch { }
                }
                else if (File.Exists(exe))
                {
                    PinInstalledHash(exe);
                }
            }
            catch { }
        }

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
        progress?.Report(new EngineProgress($"Engine ready: yt-dlp {version} + ffmpeg", 1.0));
    }

    private static bool TrySeed(string fileName)
    {
        try
        {
            var src = Path.Combine(SeedsDir, fileName);
            if (!File.Exists(src))
                return false;
            File.Copy(src, Path.Combine(BinDir, fileName), overwrite: true);
            PinInstalledHash(Path.Combine(BinDir, fileName));
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
        try
        {
            await DownloadToFileAsync(url, tmp, progress, "Downloading yt-dlp…", start, span, ct, maxBytes: 100 * 1024 * 1024);
            VerifyDownloadedBinary(tmp, minBytes: 5 * 1024 * 1024);
            VerifySha256OrWarn(tmp, await TryFetchExpectedHashAsync("yt-dlp", url, ct), "yt-dlp");
            File.Move(tmp, YtDlpPath, overwrite: true);
            PinInstalledHash(YtDlpPath);
        }
        finally
        {
            // Failure paths (size-cap throw, verify failure, move conflict)
            // must not orphan multi-MB tmp files in BinDir.
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
        }
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
        try
        {
            await DownloadToFileAsync(url, tmp, progress, "Downloading QuickJS (JS runtime)…", start, start + span, ct, maxBytes: 50 * 1024 * 1024);

            VerifyDownloadedBinary(tmp, minBytes: 100 * 1024);
            VerifySha256OrWarn(tmp, await TryFetchExpectedHashAsync("qjs", url, ct), "QuickJS");
            File.Move(tmp, QuickJsPath, overwrite: true);
            PinInstalledHash(QuickJsPath);
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
        }
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

        string usedUrl = primaryUrl;
        try
        {
            await DownloadToFileAsync(primaryUrl, tmp, progress, "Downloading FFmpeg…", start, start + span * 0.9, ct, maxBytes: 300 * 1024 * 1024);
        }
        catch
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            usedUrl = fallbackUrl;
            await DownloadToFileAsync(fallbackUrl, tmp, progress, "Downloading FFmpeg (fallback)…", start, start + span * 0.9, ct, maxBytes: 300 * 1024 * 1024);
        }
        // Hash the archive before extraction (fail-closed on mismatch).
        VerifySha256OrWarn(tmp, await TryFetchExpectedHashAsync("ffmpeg", usedUrl, ct), "FFmpeg archive");

        progress?.Report(new EngineProgress("Extracting ffmpeg…", start + span * 0.92));
        var extractDir = Path.Combine(BinDir, $"ffmpeg-extract-{Guid.NewGuid():N}");
        Directory.CreateDirectory(extractDir);

        try
        {
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
            PinInstalledHash(FfmpegPath);
            PinInstalledHash(Path.Combine(BinDir, "ffprobe.exe"));
        }
        finally
        {
            // Every failure path (missing binaries, verify failure, move
            // conflict) must clean the up-to-300MB zip and extract dir.
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            try { if (Directory.Exists(extractDir)) Directory.Delete(extractDir, true); } catch { }
        }
        progress?.Report(new EngineProgress("Extracting ffmpeg…", start + span));
    }

    internal static void ExtractZipSafely(string zipPath, string extractDir)
    {
        string fullBase = Path.GetFullPath(extractDir) + Path.DirectorySeparatorChar;
        using var archive = ZipFile.OpenRead(zipPath);
        long declaredUncompressed = 0;
        var validated = new List<(ZipArchiveEntry Entry, string Dest)>();
        foreach (var entry in archive.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name) && entry.FullName.EndsWith('/'))
                continue;
            string dest = Path.GetFullPath(Path.Combine(extractDir, entry.FullName));
            if (!dest.StartsWith(fullBase, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Archive contains an unsafe path - refusing extraction.");
            declaredUncompressed += entry.Length;
            if (declaredUncompressed > 1024L * 1024 * 1024)
                throw new InvalidOperationException("Archive too large - refusing extraction.");
            validated.Add((entry, dest));
        }
        long actualExtracted = 0;
        foreach (var (entry, dest) in validated)
        {
            string? dir = Path.GetDirectoryName(dest);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            if (entry.Name.Length == 0)
                continue;
            using var src = entry.Open();
            using var dst = new FileStream(dest, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            // Enforce the 1GB bound on ACTUAL bytes copied, not the declared
            // central-directory size (which a malicious zip can understate).
            var buf = new byte[81920];
            long written = 0;
            int n;
            while ((n = src.Read(buf, 0, buf.Length)) > 0)
            {
                written += n;
                actualExtracted += n;
                if (actualExtracted > 1024L * 1024 * 1024)
                    throw new InvalidOperationException("Archive too large - refusing extraction.");
                dst.Write(buf, 0, n);
            }
        }
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
            throw new InvalidOperationException("Engine download too large - refusing.");
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        await using var file = new FileStream(dest, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true);

        var buffer = new byte[81920];
        long read = 0;
        int n;
        try
        {
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
        catch
        {
            try { await file.DisposeAsync(); } catch { }
            try { if (File.Exists(dest)) File.Delete(dest); } catch { }
            throw;
        }
    }

    /// <summary>Supply-chain hash pinning (fail-closed on mismatch, warn-open
    /// on unknown): yt-dlp and gyan ffmpeg publish checksums; quickjs-ng and
    /// the BtbN fallback publish none, so those keep MZ+size validation only
    /// (documented, not silent).</summary>
    internal static async Task<string?> TryFetchExpectedHashAsync(string kind, string downloadUrl, CancellationToken ct)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            var tct = timeout.Token;
            if (kind == "yt-dlp")
            {
                const string sums = "https://github.com/yt-dlp/yt-dlp/releases/latest/download/SHA2-256SUMS";
                string text = await Http.GetStringAsync(sums, tct);
                return ExtractSumsHash(text, "yt-dlp.exe");
            }
            if (kind == "ffmpeg" && downloadUrl.StartsWith("https://www.gyan.dev/ffmpeg/builds/", StringComparison.OrdinalIgnoreCase))
            {
                string text = (await Http.GetStringAsync(downloadUrl + ".sha256", tct)).Trim();
                string bare = text.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
                return IsHexHash(bare) ? bare.ToLowerInvariant() : null;
            }
            return null;
        }
        catch { return null; }
    }

    internal static string? ExtractSumsHash(string text, string fileName)
    {
        try
        {
            foreach (string rawLine in text.Split('\n'))
            {
                string line = rawLine.Trim().TrimEnd('\r');
                if (line.Length < 66)
                    continue;
                // "<hash>  <name>" and "<hash> *<name>" (binary-marker) forms.
                string hash = line[..64];
                string rest = line[64..].Trim().TrimStart('*').Trim();
                if (!IsHexHash(hash))
                    continue;
                string name = rest.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
                if (string.Equals(name, fileName, StringComparison.OrdinalIgnoreCase))
                    return hash.ToLowerInvariant();
            }
            return null;
        }
        catch { return null; }
    }

    internal static bool IsHexHash(string? s) =>
        !string.IsNullOrWhiteSpace(s) && s!.Length == 64 &&
        s.All(c => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F'));

    internal static string ComputeSha256(string path)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        using var fs = File.OpenRead(path);
        return Convert.ToHexString(sha.ComputeHash(fs)).ToLowerInvariant();
    }

    /// <summary>Enforces a published hash (mismatch = delete + hard fail).
    /// Null expectation means "no published hash": warn-open, never silent.</summary>
    internal static void VerifySha256OrWarn(string path, string? expected, string display)
    {
        if (string.IsNullOrWhiteSpace(expected))
        {
            try { ActivityLog.Write("ENGINE", $"{display}: no published checksum — MZ+size validation only"); } catch { }
            return;
        }
        string actual;
        try { actual = ComputeSha256(path); }
        catch (Exception ex) { throw new EngineMissingException($"Could not hash downloaded engine ({ex.Message})."); }
        if (!string.Equals(actual, expected.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            try { File.Delete(path); } catch { }
            throw new EngineMissingException(
                $"{display} failed SHA-256 verification (possible tampering or CDN corruption) - refusing to install.");
        }
    }

    /// <summary>Sidecar pinning: records the hash of an installed binary so
    /// later startups detect modification. Trust-on-first-use for legacy and
    /// seed binaries (pins current bytes + logs); enforced thereafter.</summary>
    internal static void PinInstalledHash(string installedPath)
    {
        try
        {
            string sidecar = installedPath + ".sha256";
            if (File.Exists(sidecar))
                return;
            if (!File.Exists(installedPath))
                return;
            File.WriteAllText(sidecar, ComputeSha256(installedPath));
            try { ActivityLog.Write("ENGINE", $"{Path.GetFileName(installedPath)}: pinned current hash (trust-on-first-use)"); } catch { }
        }
        catch { }
    }

    internal static bool VerifyInstalledHash(string installedPath)
    {
        try
        {
            string sidecar = installedPath + ".sha256";
            if (!File.Exists(installedPath) || !File.Exists(sidecar))
                return true; // nothing pinned yet — caller pins after download
            string expected = File.ReadAllText(sidecar).Trim();
            if (!IsHexHash(expected))
                return true;
            return string.Equals(ComputeSha256(installedPath), expected, StringComparison.OrdinalIgnoreCase);
        }
        catch { return true; }
    }

}
