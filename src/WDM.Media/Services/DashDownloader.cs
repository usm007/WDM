using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using WDM.Media;

namespace WDM.Services;

/// <summary>Native MPEG-DASH downloader (gap 7): parses the MPD, picks the best
/// video + best audio AdaptationSets, downloads init + media segments with the
/// engine client (proxy/cookies/headers/throttle honored), and muxes with
/// ffmpeg. Anything unparseable (live/dynamic, DRM, exotic templates) throws:
/// <see cref="DashUnsupportedException"/> fails fast with a clear message,
/// anything else lets the engine fall back to ffmpeg-direct passthrough.</summary>
public static class DashDownloader
{
    /// <summary>DRM-protected MPD: neither segments nor ffmpeg-direct can play
    /// it — surface guidance instead of a cryptic ffmpeg error.</summary>
    public sealed class DashUnsupportedException : Exception
    {
        public DashUnsupportedException(string message) : base(message) { }
    }

    private const int MaxSegmentParallelism = 4;
    private const int MaxSegmentRetries = 3;
    private const int MaxIncrementalSegments = 20000;

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
        string text = await FetchTextAsync(http, manifestUrl, referer, headers, ct);
        XDocument doc;
        try { doc = XDocument.Parse(text); }
        catch { throw new InvalidOperationException("Not a valid DASH manifest."); }
        var root = doc.Root;
        if (root is null || !root.Name.LocalName.Equals("MPD", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Not a valid DASH manifest.");
        if ((Attr(root, "type") ?? "static").Equals("dynamic", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Live DASH streams aren't supported - only on-demand (.mpd VOD).");

        double? mpdDuration = ParseDuration(Attr(root, "mediaPresentationDuration"));
        string chain = AustriaChain(root, manifestUrl);
        var period = Children(root, "Period").FirstOrDefault() ?? root;
        if (!ReferenceEquals(period, root))
        {
            chain = AustriaChain(period, chain);
        }
        double? periodDuration = ParseDuration(Attr(period, "duration")) ?? mpdDuration;

        var sets = Children(period, "AdaptationSet").ToList();
        if (sets.Count == 0)
            throw new InvalidOperationException("DASH manifest has no AdaptationSets.");
        var video = PickVideo(sets);
        var audio = PickAudio(sets, video);
        if (video is null)
            throw new InvalidOperationException("DASH manifest has no video AdaptationSet.");

        string dir = Path.GetDirectoryName(outputFile) ?? Directory.GetCurrentDirectory();
        string tempDir = Path.Combine(dir, $".wdmseg_{Path.GetFileNameWithoutExtension(outputFile)}_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            string? videoFile = video is null ? null : await DownloadTrackAsync(http, video, chain, periodDuration, tempDir, "v", referer, headers, ct, addBytes, throttle);
            string? audioFile = audio is null ? null : await DownloadTrackAsync(http, audio, chain, periodDuration, tempDir, "a", referer, headers, ct, addBytes, throttle);
            if (videoFile is null)
                throw new InvalidOperationException("DASH manifest produced no downloadable video segments.");
            await MuxAsync(videoFile, audioFile, outputFile, ct);
        }
        finally
        {
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); } catch { }
        }
    }

    private sealed class Track
    {
        public XElement Set = null!;
        public XElement Rep = null!;
        public string Base = "";
    }

    private static Track? PickVideo(List<XElement> sets)
    {
        Track? best = null;
        int bestHeight = -1;
        long bestBw = -1;
        foreach (var set in sets)
        {
            if (!IsKind(set, "video")) continue;
            foreach (var rep in Children(set, "Representation"))
            {
                int h = ParseInt(Attr(rep, "height") ?? Attr(set, "height"));
                long bw = ParseLong(Attr(rep, "bandwidth"));
                if (h > bestHeight || (h == bestHeight && bw > bestBw))
                {
                    best = new Track { Set = set, Rep = rep };
                    bestHeight = h;
                    bestBw = bw;
                }
            }
        }
        return best;
    }

    private static Track? PickAudio(List<XElement> sets, Track? video)
    {
        Track? best = null;
        long bestBw = -1;
        foreach (var set in sets)
        {
            if (ReferenceEquals(set, video?.Set) || !IsKind(set, "audio")) continue;
            foreach (var rep in Children(set, "Representation"))
            {
                long bw = ParseLong(Attr(rep, "bandwidth"));
                if (bw > bestBw) { best = new Track { Set = set, Rep = rep }; bestBw = bw; }
            }
        }
        return best;
    }

    private static bool IsKind(XElement set, string kind)
    {
        string mt = Attr(set, "mimeType") ?? "";
        string ct = Attr(set, "contentType") ?? "";
        if (mt.StartsWith(kind + "/", StringComparison.OrdinalIgnoreCase)) return true;
        if (ct.Equals(kind, StringComparison.OrdinalIgnoreCase)) return true;
        // mimeType may live on the Representation instead.
        var rep = Children(set, "Representation").FirstOrDefault();
        if (rep is not null)
        {
            string rmt = Attr(rep, "mimeType") ?? "";
            if (rmt.StartsWith(kind + "/", StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private static async Task<string?> DownloadTrackAsync(
        HttpClient http, Track track, string chain, double? periodDuration, string tempDir, string tag,
        string? referer, Dictionary<string, string>? headers, CancellationToken ct,
        Action<long> addBytes, Func<long, CancellationToken, Task> throttle)
    {
        if (Children(track.Set, "ContentProtection").Any() || Children(track.Rep, "ContentProtection").Any())
            throw new DashUnsupportedException("This DASH stream is DRM-protected and can't be downloaded. Only clear (unencrypted) streams are supported.");
        string repBase = AustriaChain(track.Rep, AustriaChain(track.Set, chain));
        var urls = BuildSegmentUrls(track, repBase, periodDuration);
        if (urls is IncrementalList inc)
            return await DownloadIncrementalAsync(http, inc, tempDir, tag, referer, headers, ct, addBytes, throttle);
        if (urls.Count == 0)
            return null;
        var files = new string[urls.Count];
        var opts = new ParallelOptions { MaxDegreeOfParallelism = MaxSegmentParallelism, CancellationToken = ct };
        await Parallel.ForEachAsync(urls.Select((u, i) => (u, i)), opts, async (item, token) =>
        {
            string path = Path.Combine(tempDir, $"{tag}_{item.i:D6}.part");
            await DownloadOneAsync(http, item.u, path, referer, headers, token, addBytes, throttle);
            files[item.i] = path;
        });
        return await ConcatAsync(tempDir, tag, files, ct);
    }

    /// <summary>Number-template with unknown segment count (no timeline, no
    /// duration): download in parallel batches until the first persistent 404,
    /// keeping the ordered prefix. A 404 gets one retry so a transient
    /// rate-limit isn't mistaken for end-of-stream; anything else propagates.</summary>
    private static async Task<string?> DownloadIncrementalAsync(
        HttpClient http, IncrementalList inc, string tempDir, string tag,
        string? referer, Dictionary<string, string>? headers, CancellationToken ct,
        Action<long> addBytes, Func<long, CancellationToken, Task> throttle)
    {
        var ordered = new List<string>();
        // The init (ftyp/moov) must head the stitched file — fetch it first.
        // Without it the muxed .mp4 is unplayable; failing here fails loudly
        // instead of reporting a corrupt success.
        if (!string.IsNullOrWhiteSpace(inc.InitTpl))
        {
            string initUrl = Resolve(inc.Base, Fill(inc.MediaTpl, inc.InitTpl, inc.Bw, inc.RepId, 0, 0, hasTime: false, isInit: true));
            string initPath = Path.Combine(tempDir, $"{tag}_000000_init.part");
            await DownloadOneAsync(http, initUrl, initPath, referer, headers, ct, addBytes, throttle);
            ordered.Add(initPath);
        }
        long n = inc.Start;
        var opts = new ParallelOptions { MaxDegreeOfParallelism = MaxSegmentParallelism, CancellationToken = ct };
        while (n < inc.Start + MaxIncrementalSegments)
        {
            var batch = new List<(long num, string path)>();
            for (int k = 0; k < MaxSegmentParallelism * 2 && n < inc.Start + MaxIncrementalSegments; k++, n++)
                batch.Add((n, Path.Combine(tempDir, $"{tag}_{ordered.Count + k:D6}.part")));
            if (batch.Count == 0)
                break;
            var missing = new List<long>();
            await Parallel.ForEachAsync(batch, opts, async (item, token) =>
            {
                string u = Resolve(inc.Base, Fill(inc.MediaTpl, null, inc.Bw, inc.RepId, item.num, 0, hasTime: false, isInit: false));
                try
                {
                    await DownloadOneAsync(http, u, item.path, referer, headers, token, addBytes, throttle);
                }
                catch (HttpRequestException hre) when (hre.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    // One retry separates a transient 404 (rate limit) from the
                    // true end of the stream. Only a persistent 404 truncates.
                    try
                    {
                        await DownloadOneAsync(http, u, item.path, referer, headers, token, addBytes, throttle);
                    }
                    catch (HttpRequestException hre2) when (hre2.StatusCode == System.Net.HttpStatusCode.NotFound)
                    {
                        lock (missing) missing.Add(item.num);
                        try { if (File.Exists(item.path)) File.Delete(item.path); } catch { }
                    }
                }
            });
            if (missing.Count == 0)
            {
                ordered.AddRange(batch.Select(b => b.path));
                continue;
            }
            long firstMissing = missing.Min();
            foreach (var b in batch)
            {
                if (b.num < firstMissing) ordered.Add(b.path);
                else try { if (File.Exists(b.path)) File.Delete(b.path); } catch { }
            }
            break;
        }
        if (ordered.Count == 0)
            return null;
        return await ConcatAsync(tempDir, tag, ordered.ToArray(), ct);
    }

    private static async Task<string> ConcatAsync(string tempDir, string tag, string[] files, CancellationToken ct)
    {
        string concat = Path.Combine(tempDir, tag + ".concat");
        using (var dst = File.Create(concat))
        {
            var buf = new byte[81920];
            foreach (var f in files)
            {
                using var src = File.OpenRead(f);
                int n;
                while ((n = await src.ReadAsync(buf, ct)) > 0)
                    await dst.WriteAsync(buf.AsMemory(0, n), ct);
            }
        }
        return concat;
    }

    private static List<string> BuildSegmentUrls(Track track, string repBase, double? periodDuration)
    {
        // SegmentList: explicit URLs.
        var list = Child(track.Rep, "SegmentList") ?? Child(track.Set, "SegmentList");
        if (list is not null)
        {
            var urls = new List<string>();
            var init = Child(list, "Initialization");
            string? initSrc = init is null ? null : Attr(init, "sourceURL");
            if (!string.IsNullOrWhiteSpace(initSrc)) urls.Add(Resolve(repBase, initSrc!));
            foreach (var s in Children(list, "SegmentURL"))
            {
                string? media = Attr(s, "media");
                if (string.IsNullOrWhiteSpace(media)) continue;
                urls.Add(Resolve(repBase, media!));
            }
            return urls;
        }
        // SegmentBase: one self-contained resource.
        if (Child(track.Rep, "SegmentBase") is not null || Child(track.Set, "SegmentBase") is not null)
            return new List<string> { repBase };
        // SegmentTemplate: expand number/timeline.
        var tpl = Child(track.Rep, "SegmentTemplate") ?? Child(track.Set, "SegmentTemplate");
        if (tpl is null)
        {
            // Bare Representation: single progressive file (rare but valid).
            return new List<string> { repBase };
        }
        string? initTpl = Attr(tpl, "initialization");
        string? mediaTpl = Attr(tpl, "media");
        if (string.IsNullOrWhiteSpace(mediaTpl))
            return new List<string> { repBase };
        string bw = Attr(track.Rep, "bandwidth") ?? "";
        string repId = Attr(track.Rep, "id") ?? "";
        var urls2 = new List<string>();
        if (!string.IsNullOrWhiteSpace(initTpl))
            urls2.Add(Resolve(repBase, Fill(mediaTpl!, initTpl!, bw, repId, 0, 0, hasTime: false, isInit: true)));
        double timescale = ParseDouble(Attr(tpl, "timescale"), 1);
        if (timescale <= 0) timescale = 1;
        var timeline = Child(tpl, "SegmentTimeline");
        if (timeline is not null)
        {
            long cur = 0;
            bool first = true;
            // $Number$ (when a timeline template uses it instead of $Time$)
            // counts from startNumber, exactly like duration addressing.
            long num = ParseLong(Attr(tpl, "startNumber"), 1);
            foreach (var s in Children(timeline, "S"))
            {
                string? ts = Attr(s, "t");
                if (ts is not null && long.TryParse(ts, out long tt)) cur = tt;
                else if (first) cur = 0;
                long d = ParseLong(Attr(s, "d"));
                if (d <= 0) throw new InvalidOperationException("DASH SegmentTimeline has no durations.");
                long r = ParseLong(Attr(s, "r"), 0);
                if (r < 0)
                {
                    // Repeat-to-end needs a known period duration; else bail to passthrough.
                    if (periodDuration is null)
                        throw new InvalidOperationException("DASH live-edge timeline without duration.");
                    long totalUnits = (long)(periodDuration.Value * timescale);
                    long remain = totalUnits - (cur - TimelineStart(timeline, timescale));
                    if (remain < 0) remain = 0;
                    r = remain / d;
                }
                for (long k = 0; k <= r; k++, num++)
                {
                    urls2.Add(Resolve(repBase, Fill(mediaTpl!, null, bw, repId, num, cur, hasTime: true, isInit: false)));
                    cur += d;
                }
                first = false;
            }
            return urls2;
        }
        double segDur = ParseDouble(Attr(tpl, "duration"), 0);
        long start = ParseLong(Attr(tpl, "startNumber"), 1);
        if (segDur > 0 && periodDuration is not null)
        {
            long count = (long)Math.Ceiling(periodDuration.Value * timescale / segDur);
            for (long n = start; n < start + Math.Max(count, 1); n++)
                urls2.Add(Resolve(repBase, Fill(mediaTpl!, null, bw, repId, n, (long)((n - start) * segDur * timescale), hasTime: false, isInit: false)));
            return urls2;
        }
        // Unknown count: probe incrementally until the first 404 (VOD only).
        return new IncrementalList(repBase, mediaTpl!, initTpl, bw, repId, start);
    }

    /// <summary>Lazy URL list that the downloader expands until the first 404.
    /// Materialized by <see cref="DownloadTrackAsync"/> via indexed probing.</summary>
    private sealed class IncrementalList : List<string>
    {
        public readonly string Base;
        public readonly string MediaTpl;
        public readonly string? InitTpl;
        public readonly string Bw;
        public readonly string RepId;
        public readonly long Start;
        public IncrementalList(string b, string m, string? init, string bw, string id, long s) { Base = b; MediaTpl = m; InitTpl = init; Bw = bw; RepId = id; Start = s; }
    }

    private static long TimelineStart(XElement timeline, double timescale)
    {
        var first = Children(timeline, "S").FirstOrDefault();
        string? t = first is null ? null : Attr(first, "t");
        return t is not null && long.TryParse(t, out long v) ? v : 0;
    }

    private static string Fill(string mediaTpl, string? initTpl, string bw, string repId, long number, long time, bool hasTime, bool isInit)
    {
        string tpl = isInit ? initTpl! : mediaTpl;
        // $$ escape first so later replacements don't corrupt it.
        tpl = tpl.Replace("$$", "\0");
        tpl = Regex.Replace(tpl, @"\$Number(%0(\d+)d)?\$", m =>
        {
            if (m.Groups[2].Success && int.TryParse(m.Groups[2].Value, out int w))
                return number.ToString().PadLeft(w, '0');
            return number.ToString();
        });
        tpl = Regex.Replace(tpl, @"\$Time(%0(\d+)d)?\$", m =>
        {
            if (m.Groups[2].Success && int.TryParse(m.Groups[2].Value, out int w))
                return time.ToString().PadLeft(w, '0');
            return time.ToString();
        });
        tpl = tpl.Replace("$Bandwidth$", bw).Replace("$RepresentationID$", repId);
        if (hasTime) tpl = tpl.Replace("$Number$", number.ToString());
        else tpl = tpl.Replace("$Time$", time.ToString());
        return tpl.Replace("\0", "$");
    }

    private static string AustriaChain(XElement el, string parentUrl)
    {
        var b = Child(el, "BaseURL");
        if (b is null || string.IsNullOrWhiteSpace(b.Value)) return parentUrl;
        return Resolve(parentUrl, b.Value.Trim());
    }

    private static string Resolve(string baseUrl, string rel)
    {
        try
        {
            if (Uri.TryCreate(rel, UriKind.Absolute, out var abs)) return abs.ToString();
            return new Uri(new Uri(baseUrl, UriKind.Absolute), rel).ToString();
        }
        catch { return rel; }
    }

    private static async Task DownloadOneAsync(
        HttpClient http, string url, string path, string? referer,
        Dictionary<string, string>? headers, CancellationToken ct,
        Action<long> addBytes, Func<long, CancellationToken, Task> throttle)
    {
        int attempt = 0;
        while (true)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                ApplyHeaders(req, url, referer, headers);
                using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
                resp.EnsureSuccessStatusCode();
                using var input = await resp.Content.ReadAsStreamAsync(ct);
                using var output = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
                var buffer = new byte[256 * 1024];
                int read;
                while ((read = await input.ReadAsync(buffer, ct)) > 0)
                {
                    await throttle(read, ct);
                    await output.WriteAsync(buffer.AsMemory(0, read), ct);
                    addBytes(read);
                }
                return;
            }
            catch (Exception ex) when (attempt < MaxSegmentRetries && !ct.IsCancellationRequested &&
                                       (ex is HttpRequestException || ex is IOException || ex is TaskCanceledException) &&
                                       !(ex is HttpRequestException hre && hre.StatusCode == System.Net.HttpStatusCode.NotFound))
            {
                if (FatalErrors.IsFatalDiskError(ex)) throw;
                attempt++;
                try { if (File.Exists(path)) File.Delete(path); } catch { }
                await Task.Delay(Math.Min(4000, 500 * attempt), ct);
            }
        }
    }

    private static async Task MuxAsync(string videoFile, string? audioFile, string outputFile, CancellationToken ct)
    {
        if (!File.Exists(MediaEnvironment.FfmpegPath()))
            throw new InvalidOperationException("ffmpeg is required to mux DASH video/audio. Please install ffmpeg.");
        // Mux to a staging file, then atomically rename — a crash/kill must
        // never leave a truncated file at the final path (same rule as HLS).
        string stagingFile = outputFile + ".wdmpart";
        try { if (File.Exists(stagingFile)) File.Delete(stagingFile); } catch { }
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = MediaEnvironment.FfmpegPath(),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-hide_banner");
        psi.ArgumentList.Add("-loglevel");
        psi.ArgumentList.Add("error");
        psi.ArgumentList.Add("-y");
        psi.ArgumentList.Add("-i");
        psi.ArgumentList.Add(videoFile);
        if (audioFile is not null)
        {
            psi.ArgumentList.Add("-i");
            psi.ArgumentList.Add(audioFile);
        }
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("copy");
        // fMP4 concat needs 32-bit-safe offsets for old players; faststart
        // moves moov front for immediate playback.
        psi.ArgumentList.Add("-movflags");
        psi.ArgumentList.Add("+faststart");
        psi.ArgumentList.Add(stagingFile);
        using var proc = System.Diagnostics.Process.Start(psi) ?? throw new InvalidOperationException("Failed to launch ffmpeg for DASH mux.");
        using var reg = ct.Register(() => { try { proc.Kill(); } catch { } });
        await proc.WaitForExitAsync(ct);
        if (proc.ExitCode != 0)
        {
            try { File.Delete(stagingFile); } catch { }
            throw new InvalidOperationException($"ffmpeg exited with code {proc.ExitCode} while muxing DASH.");
        }
        File.Move(stagingFile, outputFile, overwrite: true);
    }

    private static async Task<string> FetchTextAsync(
        HttpClient http, string url, string? referer, Dictionary<string, string>? headers, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        ApplyHeaders(req, url, referer, headers);
        using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(30));
        return await resp.Content.ReadAsStringAsync(cts.Token);
    }

    private static void ApplyHeaders(HttpRequestMessage req, string url, string? referer, Dictionary<string, string>? headers)
    {
        if (!string.IsNullOrWhiteSpace(referer))
        {
            try { req.Headers.Referrer = new Uri(referer); } catch { }
        }
        if (headers is null) return;
        string targetHost = "";
        try { targetHost = new Uri(url).Host; } catch { }
        foreach (var kv in headers)
        {
            if (string.IsNullOrWhiteSpace(kv.Key) || string.IsNullOrWhiteSpace(kv.Value)) continue;
            if (kv.Key.Equals("Proxy-Authorization", StringComparison.OrdinalIgnoreCase)) continue;
            // Session credentials belong to the page host — never forward
            // them to a segment CDN on an unrelated host (same rule as HLS).
            if ((kv.Key.Equals("Cookie", StringComparison.OrdinalIgnoreCase) ||
                 kv.Key.Equals("Authorization", StringComparison.OrdinalIgnoreCase)) &&
                !string.IsNullOrEmpty(targetHost) && !HlsDownloader.IsSameHostOrSubdomain(referer, targetHost))
                continue;
            try { req.Headers.TryAddWithoutValidation(kv.Key, kv.Value); } catch { }
        }
    }

    private static XElement? Child(XElement p, string local) =>
        p.Elements().FirstOrDefault(e => e.Name.LocalName.Equals(local, StringComparison.OrdinalIgnoreCase));

    private static IEnumerable<XElement> Children(XElement p, string local) =>
        p.Elements().Where(e => e.Name.LocalName.Equals(local, StringComparison.OrdinalIgnoreCase));

    private static string? Attr(XElement? e, string name) =>
        e?.Attributes().FirstOrDefault(a => a.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase))?.Value;

    private static int ParseInt(string? s) => int.TryParse(s, out int v) ? v : 0;
    private static long ParseLong(string? s, long dflt = -1) => long.TryParse(s, out long v) ? v : dflt;
    private static double ParseDouble(string? s, double dflt) =>
        double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double v) ? v : dflt;

    private static double? ParseDuration(string? iso)
    {
        if (string.IsNullOrWhiteSpace(iso)) return null;
        // ISO8601 subset used by MPDs: PT[nH][nM][n[.n]S].
        var m = Regex.Match(iso.Trim(), @"^PT(?:(\d+(?:\.\d+)?)H)?(?:(\d+(?:\.\d+)?)M)?(?:(\d+(?:\.\d+)?)S)?$", RegexOptions.IgnoreCase);
        if (!m.Success) return null;
        double total = 0;
        if (m.Groups[1].Success) total += ParseDouble(m.Groups[1].Value, 0) * 3600;
        if (m.Groups[2].Success) total += ParseDouble(m.Groups[2].Value, 0) * 60;
        if (m.Groups[3].Success) total += ParseDouble(m.Groups[3].Value, 0);
        return total;
    }
}
