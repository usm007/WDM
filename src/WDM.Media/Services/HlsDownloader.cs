using System.Security.Cryptography;
using System.Text;

namespace WDM.Services;

/// <summary>
/// Downloads an HLS (.m3u8) stream as a single media file. Supports master and media
/// playlists, VOD segment download with AES-128 decryption, and both TS and fMP4
/// (EXT-X-MAP) segment types.
/// </summary>
public static class HlsDownloader
{
    /// <summary>Raised when the playlist uses packaged sample encryption
    /// (SAMPLE-AES etc.): segment CBC decrypt can't handle it, the engine must
    /// retry via ffmpeg-direct which decrypts during mux.</summary>
    public sealed class HlsPackagedStreamException : Exception
    {
        public string Method { get; }
        public HlsPackagedStreamException(string method)
            : base($"This stream uses {method} sample encryption, which segment download can't decrypt. Retrying via the ffmpeg path.")
        {
            Method = method;
        }
    }

    /// <summary>HTTP failure no retry can fix (dead/expired token link,
    /// revoked access). Thrown instead of retrying 401/403/404/410 so a dead
    /// link fails in seconds rather than sitting in "Preparing" for an hour.</summary>
    public sealed class HlsFatalHttpException : HttpRequestException
    {
        public HlsFatalHttpException(string message, System.Net.HttpStatusCode? statusCode)
            : base(message, null, statusCode) { }
    }

    internal sealed class Segment
    {
        public string Uri = "";
        public string? KeyUri;
        public byte[]? Key;
        public byte[]? Iv;
        public long Start;
        public long Length;
    }

    private static bool IsFatalStatus(System.Net.HttpStatusCode code) =>
        code is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden
            or System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.Gone;

    private static void ThrowIfFatalStatus(HttpResponseMessage resp, string url)
    {
        if (IsFatalStatus(resp.StatusCode))
            throw new HlsFatalHttpException(
                $"Stream link rejected ({(int)resp.StatusCode} {resp.StatusCode}) - " +
                $"the link is dead, expired, or access was revoked; retrying won't help: {url}",
                resp.StatusCode);
    }

    internal sealed class Playlist
    {
        public List<Segment> Segments = new();
        public string? InitUri;
        public long TotalBytes;
        /// <summary>An EXT-X-KEY METHOD other than AES-128/NONE was seen
        /// (e.g. SAMPLE-AES): segments are packaged-encrypted and need ffmpeg.</summary>
        public bool HasUnsupportedEncryption;
        public string? UnsupportedMethod;
    }

    /// <summary>Segment-granular resume state (IDM keyframe-record equivalent,
    /// coarse): HLS segments start on keyframes, so a completed segment file
    /// is never re-downloaded — resume continues at the first missing one.
    /// Identity (manifest + count + edge URIs) rejects slid live playlists;
    /// completion is derived from .part files validated against probed sizes.
    /// Encrypted runs never resume (rotating keys would corrupt the concat).
    /// </summary>
    internal sealed class HlsResumeState
    {
        public int Version { get; set; } = 1;
        public string Manifest { get; set; } = "";
        public int SegmentCount { get; set; }
        public string FirstUri { get; set; } = "";
        public string LastUri { get; set; } = "";
    }

    internal static string ResumeDirFor(string outputFile)
    {
        string folder = Path.GetDirectoryName(outputFile) ?? Directory.GetCurrentDirectory();
        string stem = Path.GetFileNameWithoutExtension(outputFile) ?? "";
        foreach (char c in Path.GetInvalidFileNameChars()) stem = stem.Replace(c, '_');
        stem = stem.Trim().Trim('.');
        if (stem.Length == 0) stem = "media";
        if (stem.Length > 80) stem = stem[..80];
        return Path.Combine(folder, ".wdmseg_" + stem);
    }

    internal static HlsResumeState? ReadResumeState(string tempDir)
    {
        try
        {
            string path = Path.Combine(tempDir, "done.json");
            if (!File.Exists(path))
                return null;
            var state = System.Text.Json.JsonSerializer.Deserialize<HlsResumeState>(File.ReadAllText(path));
            if (state is null || state.Version != 1 || string.IsNullOrWhiteSpace(state.Manifest) || state.SegmentCount <= 0)
                return null;
            return state;
        }
        catch { return null; }
    }

    internal static void WriteResumeState(string tempDir, string manifestUrl, Playlist playlist)
    {
        try
        {
            var state = new HlsResumeState
            {
                Manifest = manifestUrl,
                SegmentCount = playlist.Segments.Count,
                FirstUri = playlist.Segments.Count > 0 ? playlist.Segments[0].Uri : "",
                LastUri = playlist.Segments.Count > 0 ? playlist.Segments[^1].Uri : "",
            };
            File.WriteAllText(Path.Combine(tempDir, "done.json"),
                System.Text.Json.JsonSerializer.Serialize(state));
        }
        catch { }
    }

    internal static bool ResumeMatches(HlsResumeState state, string manifestUrl, Playlist playlist)
    {
        try
        {
            if (!string.Equals(state.Manifest, manifestUrl, StringComparison.OrdinalIgnoreCase))
                return false;
            if (state.SegmentCount != playlist.Segments.Count || playlist.Segments.Count == 0)
                return false;
            if (!string.Equals(state.FirstUri, playlist.Segments[0].Uri, StringComparison.OrdinalIgnoreCase))
                return false;
            return string.Equals(state.LastUri, playlist.Segments[^1].Uri, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    /// <summary>One rendition of an HLS master playlist (IDM/XDM-style quality
    /// picker). Url is absolute; Label is "1080p"/"4K"/"720p"/"2.4 Mbps"/"Audio".</summary>
    public sealed class HlsVariant
    {
        public string Url = "";
        public long Bandwidth;
        public int Height;
        public string Label = "";
    }

    /// <summary>Fetches a master playlist and lists its renditions (best first).
    /// Returns a single "Best available" entry when the URL is already a media
    /// playlist, and an empty list when it isn't a playlist at all.</summary>
    public static async Task<List<HlsVariant>> ListVariantsAsync(
        HttpClient http, string masterUrl, string? referer,
        Dictionary<string, string>? headers, CancellationToken ct)
    {
        var (text, effectiveUrl) = await FetchTextAsync(http, masterUrl, referer, headers, ct);
        if (!text.Contains("#EXT-X-STREAM-INF:", StringComparison.OrdinalIgnoreCase))
        {
            if (text.Contains("#EXTM3U", StringComparison.OrdinalIgnoreCase))
                return new List<HlsVariant> { new HlsVariant { Url = effectiveUrl, Label = "Best available" } };
            return new List<HlsVariant>();
        }
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var variants = ParseMasterVariants(text, effectiveUrl)
            .Where(t => seen.Add(t.uri))
            .Select(t => new HlsVariant
            {
                Url = t.uri,
                Bandwidth = t.bandwidth,
                Height = t.height ?? 0,
                Label = VariantLabel(t.bandwidth, t.height ?? 0),
            })
            .OrderByDescending(v => v.Height)
            .ThenByDescending(v => v.Bandwidth)
            .ToList();
        return variants;
    }

    internal static string VariantLabel(long bandwidth, int height)
    {
        if (height >= 2160) return "4K";
        if (height > 0) return height + "p";
        if (bandwidth > 0) return (bandwidth / 1_000_000.0).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " Mbps";
        return "Audio";
    }

    private const int MaxConcurrentSegments = 8;
    private const int MaxRetries = 4;

    /// <summary>Diagnostics of the last TS concat (C4 merge-path evidence:
    /// segment order/count are playlist order by construction).</summary>
    public sealed class MergeInfo
    {
        public int Segments;
        public long Bytes;
        public TimeSpan Elapsed;
        public DateTime At;
    }

    public static MergeInfo? LastMerge { get; private set; }

    public static async Task DownloadAsync(
        HttpClient http,
        string manifestUrl,
        string? referer,
        string outputFile,
        CancellationToken ct,
        Action<long> addBytes,
        Action<long> setTotalBytes,
        Func<long, CancellationToken, Task> throttle,
        Dictionary<string, string>? headers = null,
        Action<string>? setPhase = null)
    {
        var (playlist, effectiveManifestUrl) = await ResolvePlaylistAsync(http, manifestUrl, referer, headers, ct);

        // Packaged encryption (SAMPLE-AES et al.): segment-level CBC decrypt
        // cannot handle it. The engine catches this and retries via ffmpeg-direct.
        if (playlist.HasUnsupportedEncryption)
            throw new HlsPackagedStreamException(playlist.UnsupportedMethod ?? "unknown");

        // Pre-download the fMP4 init segment (EXT-X-MAP) and any encryption keys.
        byte[]? initSegment = null;
        if (!string.IsNullOrEmpty(playlist.InitUri))
        {
            initSegment = await DownloadBytesAsync(http, ResolveUrl(effectiveManifestUrl, playlist.InitUri), referer, headers, ct);
            playlist.TotalBytes += initSegment.Length;
        }

        // Discover each segment's size so the engine can show real progress and ETA.
        await ProbeSegmentSizesAsync(http, playlist, referer, headers, setPhase, ct);
        setTotalBytes(playlist.TotalBytes);
        if (initSegment is not null)
            addBytes(initSegment.Length);

        try { CleanStaleTempDirs(Path.GetDirectoryName(outputFile) ?? Directory.GetCurrentDirectory(), null); } catch { }
        // Deterministic temp dir (no GUID): completed segments survive an
        // interrupted run and are picked up below instead of re-downloaded.
        string tempDir = ResumeDirFor(outputFile);
        Directory.CreateDirectory(tempDir);
        // Now that our own temp dir exists, sweep orphans but never ourselves.
        try { CleanStaleTempDirs(Path.GetDirectoryName(outputFile) ?? Directory.GetCurrentDirectory(), tempDir); } catch { }
        // Segment resume: adopt completed .part files from an interrupted run
        // when the re-resolved playlist is identical (VOD). Live/event
        // playlists slide → identity mismatch → fresh start. Encrypted runs
        // never resume (rotating keys would corrupt the concat). Unknown-size
        // segments and length mismatches re-download.
        var resumedSegments = new HashSet<int>();
        try
        {
            bool hasKeys = playlist.Segments.Any(s => !string.IsNullOrEmpty(s.KeyUri));
            var prior = ReadResumeState(tempDir);
            if (!hasKeys && prior is not null && ResumeMatches(prior, effectiveManifestUrl, playlist))
            {
                for (int i = 0; i < playlist.Segments.Count; i++)
                {
                    try
                    {
                        string tempFile = Path.Combine(tempDir, $"seg_{i:D6}.part");
                        long expected = playlist.Segments[i].Length;
                        if (expected > 0 && File.Exists(tempFile) && new FileInfo(tempFile).Length == expected)
                        {
                            resumedSegments.Add(i);
                            addBytes(expected);
                        }
                        else if (File.Exists(tempFile))
                        {
                            try { File.Delete(tempFile); } catch { }
                        }
                    }
                    catch { }
                }
                if (resumedSegments.Count > 0)
                {
                    try { setPhase?.Invoke($"Resuming {resumedSegments.Count}/{playlist.Segments.Count} segments…"); } catch { }
                }
            }
            else
            {
                // Stale identity (different stream in this folder+name, or keys
                // this run): clear leftovers so they can't poison the concat.
                try
                {
                    foreach (string stale in Directory.GetFiles(tempDir, "seg_*.part")) { try { File.Delete(stale); } catch { } }
                }
                catch { }
            }
            WriteResumeState(tempDir, effectiveManifestUrl, playlist);
        }
        catch { }
        try
        {
            // Use a linked CTS so that any segment failure cancels the remaining
            // concurrent downloads immediately rather than wasting bandwidth.
            using var failCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var segCt = failCts.Token;

            var parallelOptions = new ParallelOptions
            {
                MaxDegreeOfParallelism = MaxConcurrentSegments,
                CancellationToken = segCt
            };

            Exception? firstError = null;
            var indexedSegments = playlist.Segments.Select((seg, index) => (seg, index));
            try
            {
                await Parallel.ForEachAsync(indexedSegments, parallelOptions, async (item, token) =>
                {
                    // Resumed segments are already on disk, validated above.
                    if (resumedSegments.Contains(item.index))
                        return;
                    try
                    {
                        string tempFile = Path.Combine(tempDir, $"seg_{item.index:D6}.part");
                        long length = await DownloadSegmentAsync(http, item.seg, referer, headers, tempFile, token, throttle);
                        addBytes(length);
                    }
                    catch (Exception ex) when (!ct.IsCancellationRequested)
                    {
                        // Real segment failure (not user cancel): record the first
                        // error and stop siblings. Checked against the USER token
                        // so fail-fast cancels aren't misreported as Paused.
                        Interlocked.CompareExchange(ref firstError, ex, null);
                        try { failCts.Cancel(); } catch { }
                        throw;
                    }
                });
            }
            catch (OperationCanceledException) when (firstError is not null && !ct.IsCancellationRequested)
            {
                // Sibling iterations were cancelled by our own fail-fast, not by
                // the user — surface the real failure so the engine marks Failed.
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(firstError).Throw();
            }
            if (firstError is not null && !ct.IsCancellationRequested)
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(firstError).Throw();
            ct.ThrowIfCancellationRequested();

            // Concatenate in playlist order to a temp file, then atomically
            // rename — a crash/kill must never leave a truncated file at the
            // final path masquerading as complete.
            bool concatenationComplete = false;
            string stagingFile = outputFile + ".wdmpart";
            var concatSw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                await using var output = new FileStream(stagingFile, FileMode.Create, FileAccess.Write, FileShare.None);
                if (initSegment is not null)
                    await output.WriteAsync(initSegment, ct);
                for (int i = 0; i < playlist.Segments.Count; i++)
                {
                    string tempFile = Path.Combine(tempDir, $"seg_{i:D6}.part");
                    if (!File.Exists(tempFile))
                        throw new InvalidOperationException($"Missing HLS segment {i}.");
                    await using var input = new FileStream(tempFile, FileMode.Open, FileAccess.Read, FileShare.Read);
                    await input.CopyToAsync(output, 128 * 1024, ct);
                }
                concatenationComplete = true;
            }
            finally
            {
                concatSw.Stop();
                if (concatenationComplete)
                {
                    File.Move(stagingFile, outputFile, overwrite: true);
                }
                else
                {
                    try { File.Delete(stagingFile); } catch { }
                }
            }
            try
            {
                LastMerge = new MergeInfo
                {
                    Segments = playlist.Segments.Count,
                    Bytes = File.Exists(outputFile) ? new FileInfo(outputFile).Length : 0,
                    Elapsed = concatSw.Elapsed,
                    At = DateTime.Now,
                };
            }
            catch { }
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    /// <summary>Deletes orphaned .wdmseg_* temporary directories left by crashed or terminated downloads.
    /// Never deletes <paramref name="activeDir"/> (the caller's live temp dir):
    /// directory mtime does not reliably bump on child writes, so age alone
    /// cannot prove a concurrent long VOD download is dead. UTC avoids DST skew.</summary>
    public static void CleanStaleTempDirs(string folder, string? activeDir = null)
    {
        try
        {
            if (!Directory.Exists(folder))
                return;
            foreach (var dir in Directory.GetDirectories(folder, ".wdmseg_*"))
            {
                try
                {
                    if (!string.IsNullOrEmpty(activeDir) &&
                        string.Equals(Path.GetFullPath(dir.TrimEnd(Path.DirectorySeparatorChar)),
                            Path.GetFullPath(activeDir.TrimEnd(Path.DirectorySeparatorChar)),
                            StringComparison.OrdinalIgnoreCase))
                        continue;
                    // Resume dirs carry a fresh identity manifest: a paused task
                    // resumed days later must find its segments, not a sweep.
                    // Manifest-less orphans (pre-resume era, crashes) keep the
                    // 30-minute rule below.
                    try
                    {
                        string manifest = Path.Combine(dir, "done.json");
                        if (File.Exists(manifest) &&
                            DateTime.UtcNow - File.GetLastWriteTimeUtc(manifest) < TimeSpan.FromDays(7))
                            continue;
                    }
                    catch { }
                    var info = new DirectoryInfo(dir);
                    if (DateTime.UtcNow - info.LastWriteTimeUtc > TimeSpan.FromMinutes(30))
                        Directory.Delete(dir, true);
                }
                catch { }
            }
        }
        catch { }
    }

    private static async Task ProbeSegmentSizesAsync(
        HttpClient http, Playlist playlist, string? referer, Dictionary<string, string>? headers,
        Action<string>? setPhase, CancellationToken ct)
    {
        int total = playlist.Segments.Count;
        long probed = 0;
        void Report()
        {
            try { setPhase?.Invoke($"Probing segment sizes ({Interlocked.Read(ref probed)}/{total})…"); } catch { }
        }

        // Fast death for dead links: probe segment 0 alone first. A revoked /
        // expired token fails here in seconds instead of after all N probes.
        // (Fatal propagates; anything else records size 0 = unknown.)
        if (total > 0)
        {
            var first = playlist.Segments[0];
            if (first.Length > 0)
                Interlocked.Add(ref playlist.TotalBytes, first.Length);
            else
            {
                first.Length = await ProbeSizeAsync(http, first.Uri, referer, headers, ct);
                Interlocked.Add(ref playlist.TotalBytes, first.Length);
            }
            Interlocked.Increment(ref probed);
            Report();
        }

        // Best-effort remainder: unknown sizes only lose Range/ETA precision,
        // so cap the whole phase at 60s rather than hanging "Preparing" on a
        // tarpitting CDN. Skipped segments keep Length 0 and still download.
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = MaxConcurrentSegments,
            CancellationToken = ct
        };

        await Parallel.ForEachAsync(playlist.Segments.Skip(1), parallelOptions, async (seg, token) =>
        {
            try
            {
                if (seg.Length > 0)
                {
                    Interlocked.Add(ref playlist.TotalBytes, seg.Length);
                    return;
                }
                if (sw.Elapsed > TimeSpan.FromSeconds(60))
                    return; // budget spent: download proceeds with unknown size
                seg.Length = await ProbeSizeAsync(http, seg.Uri, referer, headers, token);
                Interlocked.Add(ref playlist.TotalBytes, seg.Length);
            }
            finally
            {
                Interlocked.Increment(ref probed);
                Report();
            }
        });
        try { setPhase?.Invoke("Downloading segments…"); } catch { }
    }

    private static void ApplyHeaders(HttpRequestMessage req, string? referer, Dictionary<string, string>? headers)
    {
        if (!string.IsNullOrWhiteSpace(referer) && Uri.TryCreate(referer, UriKind.Absolute, out var r))
            req.Headers.Referrer = r;
        bool hasOrigin = false;
        string? targetHost = null;
        try
        {
            if (req.RequestUri is not null)
                targetHost = req.RequestUri.Host;
        }
        catch { }
        if (headers != null)
        {
            foreach (var kv in headers)
            {
                if (string.IsNullOrWhiteSpace(kv.Key) || string.IsNullOrWhiteSpace(kv.Value)) continue;
                if (kv.Key.Equals("Referer", StringComparison.OrdinalIgnoreCase) || kv.Key.Equals("Referrer", StringComparison.OrdinalIgnoreCase)) continue;
                if (kv.Key.Equals("Origin", StringComparison.OrdinalIgnoreCase)) hasOrigin = true;
                // Internal routing hints must never leave the client.
                if (kv.Key.StartsWith("X-WDM-", StringComparison.OrdinalIgnoreCase)) continue;
                // Session credentials belong to the page host — never forward
                // them to a segment/key CDN on an unrelated third-party host.
                if ((kv.Key.Equals("Cookie", StringComparison.OrdinalIgnoreCase) ||
                     kv.Key.Equals("Authorization", StringComparison.OrdinalIgnoreCase) ||
                     kv.Key.Equals("Proxy-Authorization", StringComparison.OrdinalIgnoreCase)) &&
                    !string.IsNullOrEmpty(targetHost) && !IsSameHostOrSubdomain(referer, targetHost))
                    continue;
                req.Headers.TryAddWithoutValidation(kv.Key, kv.Value);
            }
        }
        // Player CDNs often gate on Origin; derive it from the referer when absent.
        if (!hasOrigin && !string.IsNullOrWhiteSpace(referer) && Uri.TryCreate(referer, UriKind.Absolute, out var ru))
            req.Headers.TryAddWithoutValidation("Origin", ru.GetLeftPart(UriPartial.Authority));
    }

    internal static bool IsSameHostOrSubdomain(string? referer, string targetHost)
    {
        try
        {
            // No referer: single-host HLS without a page context is the common
            // case and the headers belong to this very host — allow.
            if (string.IsNullOrWhiteSpace(referer))
                return true;
            // Fail closed on unparseable referers so credentials are never
            // forwarded on the basis of a string we could not understand.
            if (!Uri.TryCreate(referer, UriKind.Absolute, out var ru))
                return false;
            if (string.Equals(ru.Host, targetHost, StringComparison.OrdinalIgnoreCase))
                return true;
            // Allow subdomains of the same parent (e.g. cdn.site.com and site.com)
            if (targetHost.EndsWith("." + ru.Host, StringComparison.OrdinalIgnoreCase) ||
                ru.Host.EndsWith("." + targetHost, StringComparison.OrdinalIgnoreCase))
                return true;
            return false;
        }
        catch { return false; }
    }

    private static async Task<long> ProbeSizeAsync(HttpClient http, string url, string? referer, Dictionary<string, string>? headers, CancellationToken ct)
    {
        // Best-effort: 2 attempts, 15s per request (the engine-wide 60s client
        // timeout would otherwise let one tarpitted segment burn minutes).
        // Unknown size (0) is safe — the segment still downloads, just without
        // a Range header. Only a dead link (fatal status on the GET fallback)
        // aborts the whole download.
        const int ProbeAttempts = 2;
        for (int attempt = 0; attempt < ProbeAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            attemptCts.CancelAfter(TimeSpan.FromSeconds(15));
            var act = attemptCts.Token;
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Head, url);
                request.Headers.TryAddWithoutValidation("Accept-Encoding", "identity");
                ApplyHeaders(request, referer, headers);
                using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, act);
                long length = response.Content.Headers.ContentLength ?? 0;
                if (response.IsSuccessStatusCode && length > 0)
                    return length;

                // CDNs rejecting HEAD (e.g. 405, or 403 for HEAD-only): fall
                // back to a ranged GET — but a fatal status THERE means the
                // link itself is dead, so throw instead of returning 0.
                using var get = new HttpRequestMessage(HttpMethod.Get, url);
                get.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 0);
                ApplyHeaders(get, referer, headers);
                using var getResp = await http.SendAsync(get, HttpCompletionOption.ResponseHeadersRead, act);
                ThrowIfFatalStatus(getResp, url);
                if (getResp.Content.Headers.ContentRange?.Length is long total && total > 0)
                    return total;
                if (getResp.Content.Headers.ContentLength is long getLen && getLen > 0)
                    return getLen;
                return 0;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (HlsFatalHttpException)
            {
                throw; // dead link: abort the download immediately, never retry
            }
            catch (Exception) when (attempt + 1 < ProbeAttempts && !ct.IsCancellationRequested)
            {
                await Task.Delay(500, ct);
            }
            catch
            {
                return 0;
            }
        }
        return 0;
    }

    private static async Task<(Playlist Playlist, string EffectiveUrl)> ResolvePlaylistAsync(
        HttpClient http, string manifestUrl, string? referer, Dictionary<string, string>? headers, CancellationToken ct)
    {
        for (int depth = 0; depth < 3; depth++)
        {
            var (text, effectiveUrl) = await FetchTextAsync(http, manifestUrl, referer, headers, ct);
            // The manifest may have 302-redirected (load-balancer -> CDN):
            // relative variant/segment/key URLs belong to the FINAL host,
            // not the pre-redirect one.
            manifestUrl = effectiveUrl;

            // Master playlists reference variant media playlists via #EXT-X-STREAM-INF
            // lines; those variant URIs must never be mistaken for media segments.
            if (text.Contains("#EXT-X-STREAM-INF:", StringComparison.OrdinalIgnoreCase))
            {
                var variant = ParseMasterVariant(text);
                if (variant is null)
                    throw new InvalidOperationException("HLS master playlist has no usable variant.");
                manifestUrl = ResolveUrl(manifestUrl, variant);
                continue;
            }

            var playlist = ParsePlaylist(text, manifestUrl);
            if (playlist is null)
                throw new InvalidOperationException("Not a valid HLS playlist.");

            await PrepareKeysAsync(http, manifestUrl, referer, headers, playlist, ct);
            return (playlist, manifestUrl);
        }

        throw new InvalidOperationException("HLS playlist did not resolve to a media playlist.");
    }

    private static string? ParseMasterVariant(string text)
    {
        string? best = null;
        long bestBandwidth = -1;
        int? bestHeight = null;

        foreach (var (uri, bandwidth, height) in ParseMasterVariants(text, null))
        {
            if (bandwidth > bestBandwidth
                || (bandwidth == bestBandwidth && height is int h && (bestHeight is null || h > bestHeight)))
            {
                best = uri;
                bestBandwidth = bandwidth;
                bestHeight = height;
            }
        }
        return best;
    }

    /// <summary>All usable variants of a master playlist. When baseUrl is set,
    /// relative variant URIs are resolved to absolute URLs.</summary>
    private static List<(string uri, long bandwidth, int? height)> ParseMasterVariants(string text, string? baseUrl)
    {
        var out_ = new List<(string uri, long bandwidth, int? height)>();
        var lines = text.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i].Trim();
            if (!line.StartsWith("#EXT-X-STREAM-INF:", StringComparison.OrdinalIgnoreCase))
                continue;

            long bandwidth = -1;
            int? height = null;
            // Quote-aware split: CODECS="avc1.640028,mp4a.40.2" contains a
            // comma that a naive Split(',') would treat as an attribute
            // boundary, corrupting every attribute after it (BUG-029).
            foreach (var attr in SplitAttributes(line.Substring("#EXT-X-STREAM-INF:".Length)))
            {
                int eq = attr.IndexOf('=');
                if (eq < 0) continue;
                string key = attr.Substring(0, eq).Trim();
                string value = attr.Substring(eq + 1).Trim().Trim('"');
                if (key.Equals("BANDWIDTH", StringComparison.OrdinalIgnoreCase))
                    long.TryParse(value, out bandwidth);
                else if (key.Equals("RESOLUTION", StringComparison.OrdinalIgnoreCase))
                {
                    int x = value.IndexOf('x');
                    if (x > 0 && int.TryParse(value.Substring(x + 1).TrimEnd('"'), out int resHeight))
                        height = resHeight;
                }
            }

            // Next non-empty, non-comment line is the variant URI.
            string? uri = null;
            for (int j = i + 1; j < lines.Length; j++)
            {
                string candidate = lines[j].Trim();
                if (candidate.Length == 0)
                    continue;
                if (candidate.StartsWith("#"))
                {
                    i = j;
                    continue;
                }
                uri = candidate;
                i = j;
                break;
            }
            if (uri is null)
                continue;
            if (baseUrl is not null)
            {
                try { uri = ResolveUrl(baseUrl, uri); } catch { continue; }
            }
            out_.Add((uri, bandwidth, height));
        }
        return out_;
    }

    /// <summary>Splits an EXT-X-STREAM-INF attribute list on commas that are
    /// not inside double quotes.</summary>
    internal static IEnumerable<string> SplitAttributes(string attrList)
    {
        if (string.IsNullOrEmpty(attrList))
            yield break;
        bool inQuotes = false;
        int start = 0;
        for (int i = 0; i < attrList.Length; i++)
        {
            char c = attrList[i];
            if (c == '"')
                inQuotes = !inQuotes;
            else if (c == ',' && !inQuotes)
            {
                yield return attrList.Substring(start, i - start);
                start = i + 1;
            }
        }
        yield return attrList.Substring(start);
    }

    /// <summary>Represents one variant from an HLS master playlist.</summary>
    public sealed class HlsVariantInfo
    {
        public long Bandwidth { get; init; }
        public int? Height { get; init; }
        public string? Codecs { get; init; }
        public string VariantUrl { get; init; } = "";
        public string Label { get; init; } = "";
    }

    /// <summary>Fetches an HLS master playlist and returns all available variants
    /// (quality levels). Returns null if the URL is not a master playlist.
    /// Used by the Add dialog to offer a quality picker before download starts.</summary>
    public static async Task<List<HlsVariantInfo>> ParseMasterVariantsAsync(
        HttpClient http, string manifestUrl, string? referer, Dictionary<string, string>? headers, CancellationToken ct)
    {
        var (text, effectiveUrl) = await FetchTextAsync(http, manifestUrl, referer, headers, ct);
        return ParseMasterVariantsText(text, effectiveUrl);
    }

    /// <summary>Parses already-fetched master playlist text (browser-assisted
    /// resolution fetches in page context, preserving auth). Same result as
    /// <see cref="ParseMasterVariantsAsync"/> without the fetch.</summary>
    internal static List<HlsVariantInfo> ParseMasterVariantsText(string text, string manifestUrl)
    {
        var result = new List<HlsVariantInfo>();
        if (!text.Contains("#EXT-X-STREAM-INF:", StringComparison.OrdinalIgnoreCase))
            return result;

        var lines = text.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i].Trim();
            if (!line.StartsWith("#EXT-X-STREAM-INF:", StringComparison.OrdinalIgnoreCase))
                continue;

            long bandwidth = -1;
            int? height = null;
            string? codecs = null;
            foreach (var attr in SplitAttributes(line.Substring("#EXT-X-STREAM-INF:".Length)))
            {
                int eq = attr.IndexOf('=');
                if (eq < 0) continue;
                string key = attr.Substring(0, eq).Trim();
                string value = attr.Substring(eq + 1).Trim().Trim('"');
                if (key.Equals("BANDWIDTH", StringComparison.OrdinalIgnoreCase))
                    long.TryParse(value, out bandwidth);
                else if (key.Equals("RESOLUTION", StringComparison.OrdinalIgnoreCase))
                {
                    int x = value.IndexOf('x');
                    if (x > 0 && int.TryParse(value.Substring(x + 1).TrimEnd('"'), out int resHeight))
                        height = resHeight;
                }
                else if (key.Equals("CODECS", StringComparison.OrdinalIgnoreCase))
                    codecs = value;
            }

            string? uri = null;
            for (int j = i + 1; j < lines.Length; j++)
            {
                string candidate = lines[j].Trim();
                if (candidate.Length == 0) continue;
                if (candidate.StartsWith("#")) { i = j; continue; }
                uri = candidate;
                i = j;
                break;
            }
            if (uri is null) continue;

            string resolved = ResolveUrl(manifestUrl, uri);
            string label = height.HasValue
                ? $"{height.Value}p"
                : bandwidth > 0
                    ? $"{bandwidth / 1000} kbps"
                    : "Best";
            if (!string.IsNullOrWhiteSpace(codecs))
                label += $" ({codecs})";

            result.Add(new HlsVariantInfo
            {
                Bandwidth = bandwidth,
                Height = height,
                Codecs = codecs,
                VariantUrl = resolved,
                Label = label,
            });
        }

        result.Sort((a, b) =>
        {
            int ha = a.Height ?? 0;
            int hb = b.Height ?? 0;
            if (ha != hb) return ha.CompareTo(hb);
            return a.Bandwidth.CompareTo(b.Bandwidth);
        });

        return result;
    }

    internal static Playlist? ParsePlaylist(string text, string baseUrl)
    {
        const int maxSegments = 10000;
        var result = new Playlist();        string? keyUri = null;
        string? keyIv = null;
        long mediaSequence = 0;
        bool haveMediaSequence = false;
        long nextOffset = 0;

        string[] lines = text.Split('\n');
        int segmentOrdinal = 0;
        bool sawExtM3U = false;
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i].Trim();
            if (!sawExtM3U)
            {
                // The playlist magic must come first: without it this is not a
                // playlist (BUG: a ZIP misclassified as HLS had its binary split
                // into garbage "segment" lines → segment fetches 404). BOM and
                // leading blanks are tolerated; anything else aborts the parse.
                if (line.Length == 0)
                    continue;
                if (!line.TrimStart('\uFEFF').StartsWith("#EXTM3U", StringComparison.OrdinalIgnoreCase))
                    return null;
                sawExtM3U = true;
                continue;
            }
            if (line.StartsWith("#EXT-X-KEY:", StringComparison.OrdinalIgnoreCase))
            {
                string method = GetAttribute(line, "METHOD") ?? "NONE";
                if (method.Equals("AES-128", StringComparison.OrdinalIgnoreCase))
                {
                    keyUri = GetAttribute(line, "URI")?.Trim('"');
                    keyIv = GetAttribute(line, "IV");
                }
                else
                {
                    keyUri = null;
                    keyIv = null;
                    // SAMPLE-AES and friends encrypt packaged samples, not whole
                    // segments: CBC segment decrypt can't handle them. Flag for
                    // the ffmpeg-direct fallback instead of silently clearing.
                    if (!method.Equals("NONE", StringComparison.OrdinalIgnoreCase))
                    {
                        result.HasUnsupportedEncryption = true;
                        result.UnsupportedMethod ??= method;
                    }
                }
                continue;
            }
            if (line.StartsWith("#EXT-X-MAP:", StringComparison.OrdinalIgnoreCase))
            {
                result.InitUri = GetAttribute(line, "URI")?.Trim('"');
                continue;
            }
            if (line.StartsWith("#EXT-X-MEDIA-SEQUENCE:", StringComparison.OrdinalIgnoreCase))
            {
                string seq = line.Substring("#EXT-X-MEDIA-SEQUENCE:".Length).Trim();
                if (long.TryParse(seq, out mediaSequence))
                    haveMediaSequence = true;
                continue;
            }
            if (line.StartsWith("#"))
                continue;
            if (line.Length == 0)
                continue;

            // This is a segment URI.
            string? byterange = null;
            for (int j = i - 1; j >= 0; j--)
            {
                string prev = lines[j].Trim();
                if (prev.StartsWith("#EXT-X-BYTERANGE:", StringComparison.OrdinalIgnoreCase))
                {
                    byterange = prev.Substring("#EXT-X-BYTERANGE:".Length).Trim();
                    break;
                }
                if (prev.StartsWith("#"))
                    continue;
                break;
            }

            var seg = new Segment { Uri = ResolveUrl(baseUrl, line), Key = null, Iv = null };
            if (byterange is not null)
            {
                string[] parts = byterange.Split('@');
                if (parts.Length == 2 && long.TryParse(parts[0], out long len) && long.TryParse(parts[1], out long start))
                {
                    seg.Length = len;
                    seg.Start = start;
                    nextOffset = start + len;
                }
                else if (long.TryParse(parts[0], out long justLen))
                {
                    // No explicit offset: the range continues right after the previous
                    // byterange segment. Requesting it from byte 0 would corrupt the
                    // concatenated output.
                    seg.Length = justLen;
                    seg.Start = nextOffset;
                    nextOffset += justLen;
                }
            }
            else
            {
                nextOffset = 0;
            }

            // Attach the current key (if any); IV defaults to the media sequence number.
            if (!string.IsNullOrEmpty(keyUri))
            {
                seg.KeyUri = keyUri;
                seg.Key = new byte[0]; // placeholder: real key resolved in PrepareKeysAsync
                seg.Iv = ParseIv(keyIv, haveMediaSequence ? mediaSequence : segmentOrdinal);
            }
            result.Segments.Add(seg);
            segmentOrdinal++;
            if (result.Segments.Count > maxSegments)
                throw new InvalidOperationException($"HLS playlist has too many segments (>{maxSegments}) - refusing.");
            if (haveMediaSequence)
                mediaSequence++;
        }

        if (result.Segments.Count == 0)
            return null;
        return result;
    }

    private static byte[]? ParseIv(string? iv, long sequence)
    {
        if (!string.IsNullOrWhiteSpace(iv) && iv.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            string hex = iv.Substring(2);
            if (hex.Length == 32)
            {
                try { return Convert.FromHexString(hex); } catch (Exception) { }
            }
        }
        // Default IV: media sequence number as 16-byte big-endian integer.
        var bytes = new byte[16];
        byte[] seqBytes = BitConverter.GetBytes(sequence);
        if (BitConverter.IsLittleEndian)
            Array.Reverse(seqBytes);
        seqBytes.CopyTo(bytes, 8);
        return bytes;
    }

    private static async Task PrepareKeysAsync(
        HttpClient http, string manifestUrl, string? referer, Dictionary<string, string>? headers, Playlist playlist, CancellationToken ct)
    {
        // Extension-forwarded key URL (1DM onPotentialM3u8AesKey equivalent):
        // a hint, not trust — the playlist URI wins, the hint is only fetched
        // (with the same scoped credentials) when the playlist key fails.
        string? keyHint = null;
        if (headers is not null && headers.TryGetValue("X-WDM-KeyUrl", out var hint) && !string.IsNullOrWhiteSpace(hint))
            keyHint = hint.Trim();
        var keyCache = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var seg in playlist.Segments)
        {
            // Only playlist-declared keys: a hint must never encrypt a segment
            // the playlist marks clear (e.g. after a METHOD=NONE rotation).
            if (string.IsNullOrEmpty(seg.KeyUri))
                continue;
            string? primary = SelectKeyUrl(seg.KeyUri, null, manifestUrl);
            string? fallback = SelectKeyUrl(null, keyHint, manifestUrl);
            // Playlist without a usable URI but with a hint: hint becomes primary.
            primary ??= fallback;
            if (string.IsNullOrEmpty(primary))
                continue;
            string cacheKey = primary;
            if (!keyCache.TryGetValue(cacheKey, out var key))
            {
                key = await DownloadKeyWithFallbackAsync(http, primary, fallback, referer, headers, ct);
                keyCache[cacheKey] = key;
            }
            seg.Key = key;
        }
    }

    private static async Task<byte[]> DownloadKeyWithFallbackAsync(
        HttpClient http, string primary, string? fallback, string? referer,
        Dictionary<string, string>? headers, CancellationToken ct)
    {
        try
        {
            return await DownloadBytesAsync(http, primary, referer, headers, ct);
        }
        catch (Exception ex) when (fallback is not null && !fallback.Equals(primary, StringComparison.Ordinal) &&
                                   (ex is HttpRequestException || ex is IOException || ex is TaskCanceledException))
        {
            // Playlist key dead (rotated/geo-blocked): one retry with the
            // extension-observed URL before giving up.
            return await DownloadBytesAsync(http, fallback, referer, headers, ct);
        }
    }

    /// <summary>Selects the key URL to fetch: the playlist URI resolved against
    /// the manifest wins; otherwise an absolute http(s) hint. Pure for testing.</summary>
    internal static string? SelectKeyUrl(string? playlistKeyUri, string? hintKeyUrl, string manifestUrl)
    {
        if (!string.IsNullOrWhiteSpace(playlistKeyUri))
        {
            try { return ResolveUrl(manifestUrl, playlistKeyUri.Trim()); }
            catch { }
        }
        if (!string.IsNullOrWhiteSpace(hintKeyUrl) &&
            Uri.TryCreate(hintKeyUrl.Trim(), UriKind.Absolute, out var hint) &&
            (hint.Scheme == Uri.UriSchemeHttp || hint.Scheme == Uri.UriSchemeHttps))
            return hint.ToString();
        return null;
    }

    private static async Task<long> DownloadSegmentAsync(
        HttpClient http,
        Segment seg,
        string? referer,
        Dictionary<string, string>? headers,
        string tempFile,
        CancellationToken ct,
        Func<long, CancellationToken, Task> throttle)
    {
        // tempDir is unique per session (Guid suffix), so never reuse stale
        // partial files from crashed runs — always download fresh.
        try { if (File.Exists(tempFile)) File.Delete(tempFile); } catch { }

        int attempt = 0;
        while (true)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, seg.Uri);
                request.Headers.TryAddWithoutValidation("Accept-Encoding", "identity");
                ApplyHeaders(request, referer, headers);
                if (seg.Length > 0)
                    request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(seg.Start, seg.Start + seg.Length - 1);

                using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
                ThrowIfFatalStatus(response, seg.Uri);
                response.EnsureSuccessStatusCode();
                await using var input = await response.Content.ReadAsStreamAsync(ct);
                await using var output = new FileStream(tempFile, FileMode.Create, FileAccess.Write, FileShare.None);

                if (seg.Key is not null && seg.Key.Length > 0)
                    await DecryptAndWriteAsync(seg.Key, seg.Iv, input, output, ct, throttle);
                else
                {
                    var buffer = new byte[256 * 1024];
                    int read;
                    while ((read = await input.ReadAsync(buffer, ct)) > 0)
                    {
                        await throttle(read, ct);
                        await output.WriteAsync(buffer.AsMemory(0, read), ct);
                    }
                }
                await output.FlushAsync(ct);
                if (seg.Length > 0)
                {
                    // A ranged part must come back 206 with the exact range.
                    // Anything else is only salvageable for a part starting at
                    // 0: a 200 full-resource body then starts with the asked
                    // bytes, so an oversize file is truncated to that prefix.
                    // Past offset 0 the slice is unlocatable — fail loudly
                    // (retried, then fatal) instead of duplicating full files
                    // into every part sharing the URI.
                    var cr = response.Content.Headers.ContentRange;
                    bool exact = response.StatusCode == System.Net.HttpStatusCode.PartialContent &&
                                 cr?.From == seg.Start && cr?.To == seg.Start + seg.Length - 1;
                    if (!exact)
                    {
                        if (seg.Start != 0)
                            throw new HttpRequestException(
                                $"Server returned wrong range for HLS segment (asked {seg.Start}-{seg.Start + seg.Length - 1}).");
                        bool plainCopy = seg.Key is null || seg.Key.Length == 0;
                        if (plainCopy && output.Length > seg.Length)
                            output.SetLength(seg.Length);
                    }
                }
                return output.Length;
            }
            catch (Exception ex) when (attempt < MaxRetries && !ct.IsCancellationRequested &&
                                       ex is not HlsFatalHttpException &&
                                       (ex is HttpRequestException || ex is IOException || ex is TaskCanceledException))
            {
                if (FatalErrors.IsFatalDiskError(ex))
                    throw;
                attempt++;
                try { if (File.Exists(tempFile)) File.Delete(tempFile); } catch { }
                await Task.Delay(Math.Min(4000, 500 * attempt), ct);
            }
            catch
            {
                try { if (File.Exists(tempFile)) File.Delete(tempFile); } catch { }
                throw;
            }
        }
    }

    private static async Task DecryptAndWriteAsync(
        byte[] key, byte[]? iv, Stream input, Stream output, CancellationToken ct,
        Func<long, CancellationToken, Task> throttle)
    {
        using var aes = Aes.Create();
        aes.KeySize = 128;
        aes.BlockSize = 128;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;

        if (iv is null)
            throw new InvalidOperationException("HLS segment is AES-128 encrypted but the playlist did not supply an IV.");
        byte[] ivBlock = iv;
        using var decryptor = aes.CreateDecryptor(key, ivBlock);
        using var cryptoStream = new CryptoStream(input, decryptor, CryptoStreamMode.Read);
        var buffer = new byte[256 * 1024];
        int read;
        while ((read = await cryptoStream.ReadAsync(buffer, ct)) > 0)
        {
            await throttle(read, ct);
            await output.WriteAsync(buffer.AsMemory(0, read), ct);
        }
    }

    private static async Task<byte[]> DownloadBytesAsync(HttpClient http, string url, string? referer, Dictionary<string, string>? headers, CancellationToken ct)
    {
        int attempt = 0;
        while (true)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.TryAddWithoutValidation("Accept-Encoding", "identity");
                ApplyHeaders(request, referer, headers);
                using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
                ThrowIfFatalStatus(response, url);
                response.EnsureSuccessStatusCode();
                return await response.Content.ReadAsByteArrayAsync(ct);
            }
            // NOTE: HlsFatalHttpException intentionally still matches the
            // HttpRequestException arm below so DownloadKeyWithFallbackAsync
            // gets its one fallback shot; the fallback's own fatal propagates.
            catch (Exception ex) when (attempt < MaxRetries && !ct.IsCancellationRequested &&
                                       ex is not HlsFatalHttpException &&
                                       (ex is HttpRequestException || ex is IOException || ex is TaskCanceledException))
            {
                attempt++;
                await Task.Delay(Math.Min(4000, 500 * attempt), ct);
            }
        }
    }

    private static async Task<(string Text, string EffectiveUrl)> FetchTextAsync(HttpClient http, string url, string? referer, Dictionary<string, string>? headers, CancellationToken ct)
    {
        const int maxPlaylistBytes = 10 * 1024 * 1024;
        int attempt = 0;
        while (true)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.TryAddWithoutValidation("Accept-Encoding", "identity");
                ApplyHeaders(request, referer, headers);
                using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
                ThrowIfFatalStatus(response, url);
                response.EnsureSuccessStatusCode();
                // Effective URL after any redirects (guard pins the final
                // request on the response): relative playlist entries resolve
                // against this, not the pre-redirect URL.
                string effectiveUrl = response.RequestMessage?.RequestUri?.ToString() ?? url;
                // Bound playlist reads: a manifest URL returning a full media
                // file would otherwise OOM the process as a single string.
                await using var stream = await response.Content.ReadAsStreamAsync(ct);
                using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                var sb = new StringBuilder(8192);
                var buf = new char[8192];
                int n, total = 0;
                while ((n = await reader.ReadAsync(buf.AsMemory(0, buf.Length), ct)) > 0)
                {
                    total += n;
                    if (total > maxPlaylistBytes)
                        throw new InvalidOperationException("HLS playlist too large - refusing to parse.");
                    sb.Append(buf, 0, n);
                }
                return (sb.ToString(), effectiveUrl);
            }
            catch (Exception ex) when (attempt < MaxRetries && !ct.IsCancellationRequested &&
                                       ex is not HlsFatalHttpException &&
                                       (ex is HttpRequestException || ex is IOException || ex is TaskCanceledException))
            {
                attempt++;
                await Task.Delay(Math.Min(4000, 500 * attempt), ct);
            }
        }
    }

    private static string ResolveUrl(string baseUrl, string relative)
    {
        if (Uri.TryCreate(relative, UriKind.Absolute, out var absolute))
            return absolute.ToString();
        if (Uri.TryCreate(baseUrl, UriKind.Absolute, out var baseUri))
            return new Uri(baseUri, relative).ToString();
        return relative;
    }

    private static string? GetAttribute(string line, string name)
    {
        string needle = name + "=";
        int idx = 0;
        while ((idx = line.IndexOf(needle, idx, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            if (idx == 0 || line[idx - 1] is ',' or ':' or ' ' or '\t')
                break;
            idx += needle.Length;
        }
        if (idx < 0)
            return null;
        int start = idx + needle.Length;

        // Scan for the end of the value, respecting quoted strings that may
        // contain commas (e.g. CODECS="avc1.42c01e,mp4a.40.2" or URI query strings).
        int end = start;
        bool inQuotes = false;
        while (end < line.Length)
        {
            char c = line[end];
            if (c == '"')
                inQuotes = !inQuotes;
            else if (c == ',' && !inQuotes)
                break;
            end++;
        }

        string value = line.Substring(start, end - start).Trim();
        return value.Trim('"').Trim();
    }
}