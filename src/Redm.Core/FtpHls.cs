namespace Redm.Core;

public static class FtpEngine
{
    // Note: FtpWebRequest is obsolete (SYSLIB0014) but retained deliberately:
    // it is the only built-in FTP client on net8.0-windows and it works. A
    // future port may move to a maintained third-party FTP client.
    public static async Task DownloadAsync(string url, string dest, IProgress<double>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(dest) ?? ".");
        string part = dest + ".part";
        long resumeFrom = File.Exists(part) ? new FileInfo(part).Length : 0;
        var req = (System.Net.FtpWebRequest)System.Net.WebRequest.Create(url);
        req.Method = System.Net.WebRequestMethods.Ftp.DownloadFile;
        req.UseBinary = true;
        req.UsePassive = true;
        if (resumeFrom > 0) req.ContentOffset = resumeFrom; // REST: resume, not rewrite
        using var res = (System.Net.FtpWebResponse)await req.GetResponseAsync();
        using var src = res.GetResponseStream();
        Directory.CreateDirectory(Path.GetDirectoryName(dest) ?? ".");
        using var outF = new FileStream(dest + ".part", FileMode.Append, FileAccess.Write, FileShare.None);
        var buf = new byte[64 * 1024];
        int n;
        while ((n = await src.ReadAsync(buf, ct)) > 0) await outF.WriteAsync(buf.AsMemory(0, n), ct);
        outF.Close();
        File.Move(dest + ".part", dest, true);
        progress?.Report(1);
    }
}

public static class HlsEngine
{
    /// <summary>Parses EXT-X-BYTERANGE ("n[@o]") following a segment URI.</summary>
    public static (long Length, long? Offset)? ParseByteRange(string line)
    {
        var m = System.Text.RegularExpressions.Regex.Match(line.Trim(),
            @"^#EXT-X-BYTERANGE:\s*(\d+)(?:@(\d+))?");
        if (!m.Success) return null;
        long n = long.Parse(m.Groups[1].Value);
        long? o = m.Groups[2].Success ? long.Parse(m.Groups[2].Value) : null;
        return (n, o);
    }

    /// <summary>Locates ffmpeg for formats the built-in engine cannot handle.</summary>
    public static string? FindFfmpeg()
    {
        string? path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in path.Split(';'))
        {
            try
            {
                string c = Path.Combine(dir.Trim(), "ffmpeg.exe");
                if (File.Exists(c)) return c;
            }
            catch { }
        }
        return null;
    }

    /// <summary>Parses EXT-X-KEY for AES-128. Returns null when clear.</summary>
    public static (string Method, string? KeyUri, byte[]? Iv)? ParseKey(string playlist)
    {
        foreach (var raw in playlist.Split('\n'))
        {
            string line = raw.Trim();
            if (!line.StartsWith("#EXT-X-KEY:", StringComparison.OrdinalIgnoreCase)) continue;
            var attrs = System.Text.RegularExpressions.Regex.Matches(line[11..], "([A-Z-]+)=(\"[^\"]*\"|[^,]*)")
                .ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value.Trim('"'));
            attrs.TryGetValue("METHOD", out string? method);
            attrs.TryGetValue("URI", out string? uri);
            attrs.TryGetValue("IV", out string? iv);
            byte[]? ivBytes = null;
            if (iv is not null)
            {
                string hex = iv.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? iv[2..] : iv;
                ivBytes = Convert.FromHexString(hex.PadLeft(32, '0')[^32..]);
            }
            return (method ?? "NONE", uri, ivBytes);
        }
        return null;
    }

    public static byte[] DecryptAes128(byte[] cipher, byte[] key, byte[] iv)
    {
        using var aes = System.Security.Cryptography.Aes.Create();
        aes.Key = key;
        aes.IV = iv;
        aes.Mode = System.Security.Cryptography.CipherMode.CBC;
        aes.Padding = System.Security.Cryptography.PaddingMode.PKCS7;
        return aes.DecryptCbc(cipher, iv);
    }

    public record SegmentRef(string Url, long? RangeStart, long? RangeEnd);

    /// <summary>Splits a playlist into segment refs, honoring EXT-X-BYTERANGE
    /// (a bare length continues right after the previous range).</summary>
    public static List<SegmentRef> ParseSegments(Uri baseUri, string text)
    {
        var segs = new List<SegmentRef>();
        long nextOffset = 0;
        string? pendingRange = null;
        foreach (var raw in text.Split('\n'))
        {
            string line = raw.Trim();
            if (line.StartsWith("#EXT-X-BYTERANGE:", StringComparison.OrdinalIgnoreCase))
            {
                pendingRange = line;
                continue;
            }
            if (line == "" || line.StartsWith("#")) continue;
            string url = new Uri(baseUri, line).ToString();
            long? rs = null, re = null;
            if (pendingRange is not null)
            {
                var br = ParseByteRange(pendingRange);
                pendingRange = null;
                if (br is not null)
                {
                    long off = br.Value.Offset ?? nextOffset;
                    rs = off;
                    re = off + br.Value.Length - 1;
                    nextOffset = off + br.Value.Length;
                }
            }
            segs.Add(new(url, rs, re));
        }
        return segs;
    }

    public static async Task DownloadAsync(HttpClient http, string playlistUrl, string dest, IProgress<double>? progress, CancellationToken ct, string? ffmpegPath = null, int maxLiveRefreshes = 8)
    {
        string text = await GetWithRetryAsync(http, playlistUrl, ct);
        var baseUri = new Uri(playlistUrl);
        var key = ParseKey(text);
        if (key?.Method.Equals("SAMPLE-AES", StringComparison.OrdinalIgnoreCase) == true)
        {
            string ff = ffmpegPath ?? FindFfmpeg() ?? throw new NotSupportedException(
                "SAMPLE-AES playlists need ffmpeg, which was not found on PATH. Install ffmpeg to enable them.");
            await DownloadViaFfmpegAsync(ff, playlistUrl, dest, ct);
            progress?.Report(1);
            return;
        }
        byte[]? aesKey = await LoadAesKeyAsync(http, baseUri, key, ct);
        Directory.CreateDirectory(Path.GetDirectoryName(dest) ?? ".");
        string statePath = dest + ".hlsstate";
        var doneUrls = ReadStateUrls(statePath, playlistUrl, dest);
        using var outF = new FileStream(dest, doneUrls.Count > 0 ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None);
        // Live windows: re-fetch the playlist and pick up unseen segments.
        for (int round = 0; ; round++)
        {
            var segs = ParseSegments(baseUri, text);
            if (segs.Count == 0 && round == 0) throw new InvalidOperationException("No segments in playlist.");
            int fresh = 0, seen = 0;
            foreach (var seg in segs)
            {
                ct.ThrowIfCancellationRequested();
                seen++;
                if (doneUrls.Contains(seg.Url)) continue;
                byte[] bytes = await GetSegmentAsync(http, seg, ct);
                if (aesKey is not null)
                {
                    // RFC 8216 §4.4.2.4: default IV is the segment index as a
                    // 128-bit big-endian integer (EXT-X-MEDIA-SEQUENCE offset ignored).
                    byte[] iv = key?.Iv ?? SequenceIv(seen - 1);
                    bytes = DecryptAes128(bytes, aesKey, iv);
                }
                await outF.WriteAsync(bytes, ct);
                await outF.FlushAsync(ct);
                doneUrls.Add(seg.Url);
                WriteState(statePath, playlistUrl, doneUrls);
                fresh++;
                progress?.Report(segs.Count == 0 ? 1 : (double)seen / segs.Count);
            }
            if (!IsLive(text) || round >= maxLiveRefreshes || fresh == 0) break;
            await Task.Delay(TimeSpan.FromSeconds(5), ct);
            text = await GetWithRetryAsync(http, playlistUrl, ct);
        }
        outF.Close();
        try { File.Delete(statePath); } catch { }
    }

    private static List<string> ReadStateUrls(string statePath, string playlistUrl, string dest)
    {
        try
        {
            if (!File.Exists(statePath) || !File.Exists(dest)) return new();
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(statePath));
            if (doc.RootElement.TryGetProperty("Url", out var u) && u.GetString() == playlistUrl
                && doc.RootElement.TryGetProperty("DoneUrls", out var d))
                return d.EnumerateArray().Select(e => e.GetString() ?? "").Where(s => s != "").ToList();
            // Legacy integer-count state from earlier versions: cannot map to
            // URLs, so restart clean rather than risk a corrupt file.
        }
        catch { }
        return new();
    }

    private static void WriteState(string statePath, string playlistUrl, List<string> doneUrls)
    {
        try { File.WriteAllText(statePath, System.Text.Json.JsonSerializer.Serialize(new { Url = playlistUrl, DoneUrls = doneUrls })); }
        catch { }
    }

    private static bool IsLive(string text) =>
        text.Contains("#EXTINF") && !text.Contains("#EXT-X-ENDLIST");

    private static byte[] SequenceIv(int i)
    {
        byte[] iv = new byte[16];
        iv[12] = (byte)(i >> 24); iv[13] = (byte)(i >> 16);
        iv[14] = (byte)(i >> 8); iv[15] = (byte)i;
        return iv;
    }

    private static async Task<byte[]?> LoadAesKeyAsync(HttpClient http, Uri baseUri,
        (string Method, string? KeyUri, byte[]? Iv)? key, CancellationToken ct)
    {
        if (key?.Method.Equals("AES-128", StringComparison.OrdinalIgnoreCase) != true) return null;
        if (key?.KeyUri is null) throw new InvalidDataException("AES-128 playlist has no key URI.");
        byte[] aesKey = await GetBytesWithRetryAsync(http, new Uri(baseUri, key?.KeyUri).ToString(), ct);
        if (aesKey.Length != 16) throw new InvalidDataException($"AES-128 key must be 16 bytes, got {aesKey.Length}.");
        return aesKey;
    }

    private static async Task<byte[]> GetSegmentAsync(HttpClient http, SegmentRef seg, CancellationToken ct)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, seg.Url);
                if (seg.RangeStart.HasValue)
                    req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(
                        seg.RangeStart.Value, seg.RangeEnd);
                using var res = await http.SendAsync(req, ct);
                if ((int)res.StatusCode >= 500 && attempt < TransferEngine.MaxAttempts)
                {
                    await Task.Delay(TransferEngine.RetryDelay, ct);
                    continue;
                }
                res.EnsureSuccessStatusCode();
                return await res.Content.ReadAsByteArrayAsync(ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (HttpRequestException) when (attempt < TransferEngine.MaxAttempts)
            {
                await Task.Delay(TransferEngine.RetryDelay, ct);
            }
        }
    }

    private static async Task DownloadViaFfmpegAsync(string ffmpeg, string playlistUrl, string dest, CancellationToken ct)
    {
        if (!File.Exists(ffmpeg)) throw new InvalidOperationException($"ffmpeg not found at '{ffmpeg}'.");
        System.Diagnostics.Process? p;
        try
        {
            p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(ffmpeg,
                $"-y -i \"{playlistUrl}\" -c copy \"{dest}\"")
            { UseShellExecute = false, CreateNoWindow = true });
        }
        catch (Exception ex) { throw new InvalidOperationException($"Could not start ffmpeg: {ex.Message.Split('\n')[0]}"); }
        using (p)
        {
            if (p is null) throw new InvalidOperationException("Could not start ffmpeg.");
            await p.WaitForExitAsync(ct);
            if (p.ExitCode != 0) throw new InvalidOperationException($"ffmpeg exited with code {p.ExitCode}.");
        }
    }

    private static async Task<string> GetWithRetryAsync(HttpClient http, string url, CancellationToken ct)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                using var res = await http.GetAsync(url, ct);
                if ((int)res.StatusCode >= 500 && attempt < TransferEngine.MaxAttempts)
                {
                    await Task.Delay(TransferEngine.RetryDelay, ct);
                    continue;
                }
                res.EnsureSuccessStatusCode();
                return await res.Content.ReadAsStringAsync(ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (HttpRequestException) when (attempt < TransferEngine.MaxAttempts)
            {
                await Task.Delay(TransferEngine.RetryDelay, ct);
            }
        }
    }

    private static async Task<byte[]> GetBytesWithRetryAsync(HttpClient http, string url, CancellationToken ct)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                using var res = await http.GetAsync(url, ct);
                if ((int)res.StatusCode >= 500 && attempt < TransferEngine.MaxAttempts)
                {
                    await Task.Delay(TransferEngine.RetryDelay, ct);
                    continue;
                }
                res.EnsureSuccessStatusCode();
                return await res.Content.ReadAsByteArrayAsync(ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (HttpRequestException) when (attempt < TransferEngine.MaxAttempts)
            {
                await Task.Delay(TransferEngine.RetryDelay, ct);
            }
        }
    }
}
