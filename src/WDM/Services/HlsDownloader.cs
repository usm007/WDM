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

    internal sealed class Segment
    {
        public string Uri = "";
        public string? KeyUri;
        public byte[]? Key;
        public byte[]? Iv;
        public long Start;
        public long Length;
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
        Dictionary<string, string>? headers = null)
    {
        var playlist = await ResolvePlaylistAsync(http, manifestUrl, referer, headers, ct);

        // Packaged encryption (SAMPLE-AES et al.): segment-level CBC decrypt
        // cannot handle it. The engine catches this and retries via ffmpeg-direct.
        if (playlist.HasUnsupportedEncryption)
            throw new HlsPackagedStreamException(playlist.UnsupportedMethod ?? "unknown");

        // Pre-download the fMP4 init segment (EXT-X-MAP) and any encryption keys.
        byte[]? initSegment = null;
        if (!string.IsNullOrEmpty(playlist.InitUri))
        {
            initSegment = await DownloadBytesAsync(http, ResolveUrl(manifestUrl, playlist.InitUri), referer, headers, ct);
            playlist.TotalBytes += initSegment.Length;
        }

        // Discover each segment's size so the engine can show real progress and ETA.
        await ProbeSegmentSizesAsync(http, playlist, referer, headers, ct);
        setTotalBytes(playlist.TotalBytes);
        if (initSegment is not null)
            addBytes(initSegment.Length);

        try { CleanStaleTempDirs(Path.GetDirectoryName(outputFile) ?? Directory.GetCurrentDirectory(), null); } catch { }
        string tempDir = Path.Combine(
            Path.GetDirectoryName(outputFile) ?? Directory.GetCurrentDirectory(),
            $".wdmseg_{Path.GetFileNameWithoutExtension(outputFile)}_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        // Now that our own temp dir exists, sweep orphans but never ourselves.
        try { CleanStaleTempDirs(Path.GetDirectoryName(outputFile) ?? Directory.GetCurrentDirectory(), tempDir); } catch { }
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

            // Concatenate in playlist order.
            bool concatenationComplete = false;
            var concatSw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                await using var output = new FileStream(outputFile, FileMode.Create, FileAccess.Write, FileShare.Read);
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
                if (!concatenationComplete)
                {
                    try { File.Delete(outputFile); } catch { }
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
        HttpClient http, Playlist playlist, string? referer, Dictionary<string, string>? headers, CancellationToken ct)
    {
        var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = MaxConcurrentSegments,
            CancellationToken = ct
        };

        await Parallel.ForEachAsync(playlist.Segments, parallelOptions, async (seg, token) =>
        {
            if (seg.Length > 0)
            {
                Interlocked.Add(ref playlist.TotalBytes, seg.Length);
                return;
            }
            seg.Length = await ProbeSizeAsync(http, seg.Uri, referer, headers, token);
            Interlocked.Add(ref playlist.TotalBytes, seg.Length);
        });
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
        int attempt = 0;
        while (attempt <= MaxRetries)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Head, url);
                request.Headers.TryAddWithoutValidation("Accept-Encoding", "identity");
                ApplyHeaders(request, referer, headers);
                using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
                long length = response.Content.Headers.ContentLength ?? 0;
                if (response.IsSuccessStatusCode && length > 0)
                    return length;

                // CDNs rejecting HEAD (e.g. 405/403) or returning 0 length: fall back to a ranged GET for the size.
                using var get = new HttpRequestMessage(HttpMethod.Get, url);
                get.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 0);
                ApplyHeaders(get, referer, headers);
                using var getResp = await http.SendAsync(get, HttpCompletionOption.ResponseHeadersRead, ct);
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
            catch (Exception) when (attempt < MaxRetries && !ct.IsCancellationRequested)
            {
                attempt++;
                await Task.Delay(Math.Min(4000, 500 * attempt), ct);
            }
            catch
            {
                // Probe failure after retries shouldn't fail the entire HLS download;
                // return 0 (size unknown) so the segment download loop can proceed.
                return 0;
            }
        }
        return 0;
    }

    private static async Task<Playlist> ResolvePlaylistAsync(
        HttpClient http, string manifestUrl, string? referer, Dictionary<string, string>? headers, CancellationToken ct)
    {
        for (int depth = 0; depth < 3; depth++)
        {
            string text = await FetchTextAsync(http, manifestUrl, referer, headers, ct);

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
            return playlist;
        }

        throw new InvalidOperationException("HLS playlist did not resolve to a media playlist.");
    }

    private static string? ParseMasterVariant(string text)
    {
        string? best = null;
        long bestBandwidth = -1;
        int? bestHeight = null;

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
                throw new InvalidOperationException($"HLS playlist has too many segments (>{maxSegments}) — refusing.");
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
                return output.Length;
            }
            catch (Exception ex) when (attempt < MaxRetries && !ct.IsCancellationRequested &&
                                       (ex is HttpRequestException || ex is IOException || ex is TaskCanceledException))
            {
                if (DownloadEngine.IsFatalDiskError(ex))
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
                response.EnsureSuccessStatusCode();
                return await response.Content.ReadAsByteArrayAsync(ct);
            }
            catch (Exception ex) when (attempt < MaxRetries && !ct.IsCancellationRequested &&
                                       (ex is HttpRequestException || ex is IOException || ex is TaskCanceledException))
            {
                attempt++;
                await Task.Delay(Math.Min(4000, 500 * attempt), ct);
            }
        }
    }

    private static async Task<string> FetchTextAsync(HttpClient http, string url, string? referer, Dictionary<string, string>? headers, CancellationToken ct)
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
                response.EnsureSuccessStatusCode();
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
                        throw new InvalidOperationException("HLS playlist too large — refusing to parse.");
                    sb.Append(buf, 0, n);
                }
                return sb.ToString();
            }
            catch (Exception ex) when (attempt < MaxRetries && !ct.IsCancellationRequested &&
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