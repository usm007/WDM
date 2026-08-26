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
    private sealed class Segment
    {
        public string Uri = "";
        public byte[]? Key;
        public byte[]? Iv;
        public long Start;
        public long Length;
    }

    private sealed class Playlist
    {
        public List<Segment> Segments = new();
        public string? InitUri;
        public long TotalBytes;
    }

    private const int MaxConcurrentSegments = 8;
    private const int MaxRetries = 4;

    public static async Task DownloadAsync(
        HttpClient http,
        string manifestUrl,
        string? referer,
        string outputFile,
        CancellationToken ct,
        Action<long> addBytes,
        Action<long> setTotalBytes,
        Func<long, CancellationToken, Task> throttle)
    {
        var playlist = await ResolvePlaylistAsync(http, manifestUrl, referer, ct);

        // Pre-download the fMP4 init segment (EXT-X-MAP) and any encryption keys.
        byte[]? initSegment = null;
        if (!string.IsNullOrEmpty(playlist.InitUri))
        {
            initSegment = await DownloadBytesAsync(http, ResolveUrl(manifestUrl, playlist.InitUri), referer, ct);
            playlist.TotalBytes += initSegment.Length;
        }

        // Discover each segment's size so the engine can show real progress and ETA.
        await ProbeSegmentSizesAsync(http, playlist, referer, ct);
        setTotalBytes(playlist.TotalBytes);

        string tempDir = Path.Combine(
            Path.GetDirectoryName(outputFile) ?? Directory.GetCurrentDirectory(),
            $".wdmseg_{Path.GetFileNameWithoutExtension(outputFile)}_{Environment.ProcessId}");
        Directory.CreateDirectory(tempDir);
        try
        {
            // Use a linked CTS so that any segment failure cancels the remaining
            // concurrent downloads immediately rather than wasting bandwidth.
            using var failCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var segCt = failCts.Token;

            using var semaphore = new SemaphoreSlim(MaxConcurrentSegments);
            var tasks = new List<Task>();
            for (int i = 0; i < playlist.Segments.Count; i++)
            {
                int index = i;
                tasks.Add(Task.Run(async () =>
                {
                    await semaphore.WaitAsync(segCt);
                    try
                    {
                        var seg = playlist.Segments[index];
                        string tempFile = Path.Combine(tempDir, $"seg_{index:D6}.part");
                        long length = await DownloadSegmentAsync(http, seg, referer, tempFile, segCt, throttle);
                        addBytes(length);
                    }
                    catch when (!segCt.IsCancellationRequested)
                    {
                        // Signal all sibling segments to stop on first error.
                        failCts.Cancel();
                        throw;
                    }
                    finally
                    {
                        semaphore.Release();
                    }
                }, segCt));
            }
            await Task.WhenAll(tasks);
            ct.ThrowIfCancellationRequested();

            // Concatenate in playlist order.
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
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    private static async Task ProbeSegmentSizesAsync(
        HttpClient http, Playlist playlist, string? referer, CancellationToken ct)
    {
        using var semaphore = new SemaphoreSlim(MaxConcurrentSegments);
        var tasks = new List<Task>();
        for (int i = 0; i < playlist.Segments.Count; i++)
        {
            var seg = playlist.Segments[i];
            if (seg.Length > 0)
            {
                Interlocked.Add(ref playlist.TotalBytes, seg.Length);
                continue;
            }
            tasks.Add(Task.Run(async () =>
            {
                await semaphore.WaitAsync(ct);
                try
                {
                    seg.Length = await ProbeSizeAsync(http, seg.Uri, referer, ct);
                    Interlocked.Add(ref playlist.TotalBytes, seg.Length);
                }
                finally
                {
                    semaphore.Release();
                }
            }, ct));
        }
        await Task.WhenAll(tasks);
    }

    private static async Task<long> ProbeSizeAsync(HttpClient http, string url, string? referer, CancellationToken ct)
    {
        int attempt = 0;
        while (true)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Head, url);
                request.Headers.TryAddWithoutValidation("Accept-Encoding", "identity");
                if (!string.IsNullOrWhiteSpace(referer) && Uri.TryCreate(referer, UriKind.Absolute, out var r))
                    request.Headers.Referrer = r;
                using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
                long length = response.Content.Headers.ContentLength ?? 0;
                if (length > 0)
                    return length;
                // Some CDNs reject HEAD; fall back to a ranged GET for the size.
                if (response.StatusCode != System.Net.HttpStatusCode.OK)
                    return 0;
                using var get = new HttpRequestMessage(HttpMethod.Get, url);
                get.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 0);
                using var getResp = await http.SendAsync(get, HttpCompletionOption.ResponseHeadersRead, ct);
                if (getResp.Content.Headers.ContentRange?.Length is long total && total > 0)
                    return total;
                return getResp.Content.Headers.ContentLength ?? 0;
            }
            catch (Exception) when (attempt < MaxRetries && !ct.IsCancellationRequested)
            {
                attempt++;
                await Task.Delay(Math.Min(4000, 500 * attempt), ct);
            }
        }
    }

    private static async Task<Playlist> ResolvePlaylistAsync(
        HttpClient http, string manifestUrl, string? referer, CancellationToken ct)
    {
        for (int depth = 0; depth < 3; depth++)
        {
            string text = await FetchTextAsync(http, manifestUrl, referer, ct);

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

            await PrepareKeysAsync(http, manifestUrl, referer, playlist, ct);
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
            foreach (var attr in line.Substring("#EXT-X-STREAM-INF:".Length).Split(','))
            {
                int eq = attr.IndexOf('=');
                if (eq < 0) continue;
                string key = attr.Substring(0, eq).Trim();
                string value = attr.Substring(eq + 1).Trim();
                if (key.Equals("BANDWIDTH", StringComparison.OrdinalIgnoreCase))
                    long.TryParse(value, out bandwidth);
                else if (key.Equals("RESOLUTION", StringComparison.OrdinalIgnoreCase))
                {
                    int x = value.IndexOf('x');
                    if (x > 0 && int.TryParse(value.Substring(x + 1), out int resHeight))
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

    private static Playlist? ParsePlaylist(string text, string baseUrl)
    {
        var result = new Playlist();
        string? keyUri = null;
        string? keyIv = null;
        long mediaSequence = 0;
        bool haveMediaSequence = false;
        long nextOffset = 0;

        string[] lines = text.Split('\n');
        int segmentOrdinal = 0;
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i].Trim();
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
                seg.Key = new byte[0]; // placeholder: real key resolved in PrepareKeysAsync
                seg.Iv = ParseIv(keyIv, haveMediaSequence ? mediaSequence : segmentOrdinal);
            }
            result.Segments.Add(seg);
            segmentOrdinal++;
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
        HttpClient http, string manifestUrl, string? referer, Playlist playlist, CancellationToken ct)
    {
        var keyCache = new Dictionary<string, byte[]>(StringComparer.Ordinal);

        // Walk the playlist text once more to map each segment index to its key URI.
        string text = await FetchTextAsync(http, manifestUrl, referer, ct);
        string[] lines = text.Split('\n');
        string? currentKeyUri = null;
        int segIndex = 0;
        foreach (var rawLine in lines)
        {
            string line = rawLine.Trim();
            if (line.StartsWith("#EXT-X-KEY:", StringComparison.OrdinalIgnoreCase))
            {
                string method = GetAttribute(line, "METHOD") ?? "NONE";
                currentKeyUri = method.Equals("AES-128", StringComparison.OrdinalIgnoreCase)
                    ? GetAttribute(line, "URI")?.Trim('"')
                    : null;
                continue;
            }
            if (line.Length == 0 || line.StartsWith("#"))
                continue;
            if (segIndex >= playlist.Segments.Count)
                break;

            var seg = playlist.Segments[segIndex];
            if (!string.IsNullOrEmpty(currentKeyUri))
            {
                if (!keyCache.TryGetValue(currentKeyUri, out var key))
                {
                    key = await DownloadBytesAsync(http, ResolveUrl(manifestUrl, currentKeyUri), referer, ct);
                    keyCache[currentKeyUri] = key;
                }
                seg.Key = key;
            }
            segIndex++;
        }
    }

    private static async Task<long> DownloadSegmentAsync(
        HttpClient http,
        Segment seg,
        string? referer,
        string tempFile,
        CancellationToken ct,
        Func<long, CancellationToken, Task> throttle)
    {
        int attempt = 0;
        while (true)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, seg.Uri);
                request.Headers.TryAddWithoutValidation("Accept-Encoding", "identity");
                if (!string.IsNullOrWhiteSpace(referer) && Uri.TryCreate(referer, UriKind.Absolute, out var r))
                    request.Headers.Referrer = r;
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
            catch (Exception) when (attempt < MaxRetries && !ct.IsCancellationRequested)
            {
                attempt++;
                await Task.Delay(Math.Min(4000, 500 * attempt), ct);
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

        byte[] ivBlock = iv ?? new byte[16];
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

    private static async Task<byte[]> DownloadBytesAsync(HttpClient http, string url, string? referer, CancellationToken ct)
    {
        int attempt = 0;
        while (true)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.TryAddWithoutValidation("Accept-Encoding", "identity");
                if (!string.IsNullOrWhiteSpace(referer) && Uri.TryCreate(referer, UriKind.Absolute, out var r))
                    request.Headers.Referrer = r;
                using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
                response.EnsureSuccessStatusCode();
                return await response.Content.ReadAsByteArrayAsync(ct);
            }
            catch (Exception) when (attempt < MaxRetries && !ct.IsCancellationRequested)
            {
                attempt++;
                await Task.Delay(Math.Min(4000, 500 * attempt), ct);
            }
        }
    }

    private static async Task<string> FetchTextAsync(HttpClient http, string url, string? referer, CancellationToken ct)
    {
        int attempt = 0;
        while (true)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.TryAddWithoutValidation("Accept-Encoding", "identity");
                if (!string.IsNullOrWhiteSpace(referer) && Uri.TryCreate(referer, UriKind.Absolute, out var r))
                    request.Headers.Referrer = r;
                using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
                response.EnsureSuccessStatusCode();
                return await response.Content.ReadAsStringAsync(ct);
            }
            catch (Exception) when (attempt < MaxRetries && !ct.IsCancellationRequested)
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
        int idx = line.IndexOf(name + "=", StringComparison.OrdinalIgnoreCase);
        if (idx < 0)
            return null;
        int start = idx + name.Length + 1;

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