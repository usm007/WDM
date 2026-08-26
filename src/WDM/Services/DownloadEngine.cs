using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using WDM.Models;

namespace WDM.Services;

public sealed class DownloadEngine
{
    private readonly HttpClient _http;
    private readonly object _lock = new();
    private readonly Dictionary<Guid, Session> _sessions = new();
    private readonly List<DownloadTask> _queue = new();
    private readonly HashSet<string> _reservedPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly System.Timers.Timer _meter;
    private readonly SpeedGovernor _governor = new();
    private long _totalSpeedBps;
    private int _maxConcurrent = 3;
    private int _maxRetries = 3;
    private long _baseLimitKbps;

    public event Action? TaskChanged;
    public event Action<DownloadTask>? TaskCompleted;
    public event Action<DownloadTask, double[]>? ChunkProgressUpdated;

    public DownloadEngine()
    {
        _http = CreateClient();
        _meter = new System.Timers.Timer(500);
        _meter.AutoReset = true;
        _meter.Elapsed += (_, _) => RefreshSpeeds();
    }

    public int MaxConcurrent
    {
        get { lock (_lock) return _maxConcurrent; }
        set { lock (_lock) _maxConcurrent = Math.Max(1, value); }
    }

    public int MaxRetries
    {
        get { lock (_lock) return _maxRetries; }
        set { lock (_lock) _maxRetries = Math.Max(0, value); }
    }

    public long GlobalSpeedLimitKbps
    {
        get { lock (_lock) return _baseLimitKbps; }
        set
        {
            lock (_lock) _baseLimitKbps = Math.Max(0, value);
            ApplySpeedLimit();
        }
    }

    public int ActiveCount
    {
        get { lock (_lock) return _sessions.Count; }
    }

    public int QueuedCount
    {
        get { lock (_lock) return _queue.Count; }
    }

    public int GetQueuePosition(DownloadTask task)
    {
        lock (_lock)
        {
            int index = _queue.IndexOf(task);
            return index < 0 ? 0 : index + 1;
        }
    }

    public long TotalSpeedBps => Interlocked.Read(ref _totalSpeedBps);

    private static HttpClient CreateClient()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            MaxConnectionsPerServer = 64,
            AutomaticDecompression = DecompressionMethods.None,
            UseCookies = false, // Must be false so custom Cookie headers are sent raw without .NET stripping them
        };
        var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(60),
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/125.0 Safari/537.36");
        client.DefaultRequestHeaders.Accept.ParseAdd("*/*");
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US,en;q=0.9");
        return client;
    }

    public void Start(DownloadTask task)
    {
        bool startNow;
        lock (_lock)
        {
            if (_sessions.ContainsKey(task.Id) || _queue.Contains(task))
                return;

            if (_sessions.Count >= _maxConcurrent)
            {
                task.Status = TaskStatus.Queued;
                _queue.Add(task);
                TaskChanged?.Invoke();
                return;
            }

            startNow = true;
        }

        if (startNow)
        {
            BeginSession(task);
        }
        _meter.Start();
    }

    /// <summary>Swaps the task's download link. The stored ETag/Last-Modified identity
    /// is cleared (a refreshed URL may serve the same file with different headers) and
    /// the task is flagged so the next start resumes from the existing progress when
    /// the new file matches in size, or restarts from zero otherwise.</summary>
    public void UpdateLink(DownloadTask task, string newUrl)
    {
        lock (_lock)
        {
            if (_sessions.ContainsKey(task.Id))
                throw new InvalidOperationException("Pause the download before changing its link.");
            if (string.IsNullOrWhiteSpace(newUrl))
                throw new ArgumentException("A URL is required.", nameof(newUrl));
            task.Url = newUrl;
            task.Etag = null;
            task.LastModified = null;
            task.LinkRefreshed = true;
        }
        TaskChanged?.Invoke();
    }

    public void Pause(DownloadTask task)
    {
        lock (_lock)
        {
            if (_sessions.TryGetValue(task.Id, out var session))
            {
                session.Cancel();
                return;
            }
            // Queued tasks have no session yet; take them out of the queue so they
            // don't start when a slot frees up.
            if (_queue.Remove(task))
            {
                task.Status = TaskStatus.Paused;
                task.SpeedBps = 0;
                task.Eta = "";
                TaskChanged?.Invoke();
            }
        }
    }

    public void PauseAll()
    {
        Session[] snapshot;
        lock (_lock) snapshot = _sessions.Values.ToArray();
        foreach (var session in snapshot)
            session.Cancel();

        // Also hold back queued tasks so they don't sneak in when a slot frees up.
        DownloadTask[] queued;
        lock (_lock)
        {
            queued = _queue.ToArray();
            _queue.Clear();
        }
        foreach (var task in queued)
        {
            task.Status = TaskStatus.Paused;
            task.SpeedBps = 0;
            task.Eta = "";
        }
        if (queued.Length > 0)
            TaskChanged?.Invoke();
    }

    public void ResumeAll()
    {
        DownloadTask[] tasks;
        lock (_lock) tasks = _queue.ToArray();
        foreach (var task in tasks)
        {
            RemoveQueued(task);
            Start(task);
        }
    }

    public void Stop(DownloadTask task)
    {
        Session? session;
        lock (_lock) _sessions.TryGetValue(task.Id, out session);

        if (session is null)
        {
            RemoveQueued(task);
            task.Status = TaskStatus.Paused;
            task.Error = "Stopped";
            TaskChanged?.Invoke();
            return;
        }

        session.Cancel();
        session.Removed = true;
        lock (_lock) _sessions.Remove(task.Id);
        // Reflect the stopped state immediately instead of waiting for all chunk tasks
        // to unwind (they can linger up to the HTTP timeout).
        task.Status = TaskStatus.Paused;
        task.SpeedBps = 0;
        task.Eta = "";
        task.Error = "Stopped";
        TaskChanged?.Invoke();

        _ = Task.Run(async () =>
        {
            // Wait until the session (and every chunk worker) has fully unwound before
            // deleting anything; otherwise deletion can race with in-flight writes.
            if (session.RunningTask is Task running)
            {
                try { await running; }
                catch { /* session state already reconciled by RunSessionAsync */ }
            }
            // Only clean up the partial files if the task wasn't restarted in the
            // meantime (a new session for the same task would be writing there).
            lock (_lock)
            {
                if (_sessions.ContainsKey(task.Id))
                    return;
            }
            TryDelete(session.StatePath);
            TryDelete(task.FullPath);
            session.Dispose();
        });
    }

    public void Remove(DownloadTask task, bool deleteFiles = false)
    {
        RemoveQueued(task);

        Session? session;
        lock (_lock) _sessions.TryGetValue(task.Id, out session);

        if (session is null)
        {
            if (deleteFiles)
            {
                TryDelete(task.FullPath);
                TryDelete(StatePath(task));
            }
            return;
        }

        session.Cancel();
        session.Removed = true;
        lock (_lock) _sessions.Remove(task.Id);
        TaskChanged?.Invoke();

        _ = Task.Run(async () =>
        {
            if (session.RunningTask is Task running)
            {
                try { await running; }
                catch { /* session state already reconciled by RunSessionAsync */ }
            }
            if (deleteFiles)
            {
                TryDelete(task.FullPath);
                TryDelete(session.StatePath);
            }
            session.Dispose();
        });
    }

    public void SetPriority(DownloadTask task, PriorityLevel level)
    {
        task.Priority = level;
        TaskChanged?.Invoke();
        PumpQueue();
    }

    public void MoveQueued(DownloadTask task, int direction)
    {
        lock (_lock)
        {
            int index = _queue.IndexOf(task);
            int target = index + direction;
            if (index < 0 || target < 0 || target >= _queue.Count)
                return;
            (_queue[index], _queue[target]) = (_queue[target], _queue[index]);
            TaskChanged?.Invoke();
        }
    }

    private void RemoveQueued(DownloadTask task)
    {
        lock (_lock)
        {
            if (_queue.Remove(task))
                TaskChanged?.Invoke();
        }
    }

    private void BeginSession(DownloadTask task)
    {
        var session = new Session(task);
        lock (_lock) _sessions[task.Id] = session;
        task.Status = TaskStatus.Downloading;
        TaskChanged?.Invoke();
        // Track the run so Stop/Remove can wait for every in-flight chunk worker to
        // unwind before touching the partial files.
        var run = RunSessionAsync(session);
        session.RunningTask = run;
        _ = run;
    }

    private void PumpQueue()
    {
        List<DownloadTask> toStart = new();
        lock (_lock)
        {
            var ordered = _queue
                .OrderByDescending(t => t.Priority)
                .ThenBy(t => _queue.IndexOf(t))
                .ToList();
            foreach (var task in ordered)
            {
                if (_sessions.Count >= _maxConcurrent)
                    break;
                _queue.Remove(task);
                toStart.Add(task);
            }
        }
        foreach (var task in toStart)
            BeginSession(task);
        if (toStart.Count > 0)
        {
            TaskChanged?.Invoke();
            _meter.Start();
        }
    }

    private async Task RunSessionAsync(Session session)
    {
        var task = session.Task;
        if (task.IsYouTube)
        {
            await RunYouTubeSessionAsync(session);
            return;
        }

        bool linkRefreshed = task.LinkRefreshed;
        task.LinkRefreshed = false;
        try
        {
            long previousTotalBytes = task.TotalBytes;
            var meta = await ProbeAsync(task, session.Token);
            task.TotalBytes = meta.TotalBytes;
            session.CurrentUrlIndex = meta.UrlIndex;
            ApplyResumeCapability(task, meta);
            if (string.IsNullOrWhiteSpace(task.FileName))
            {
                task.FileName = meta.SuggestedName ?? DeriveName(task.Url, meta.ContentType);
                task.FileName = SanitizeFileName(task.FileName);
                task.FileName = EnsureUniqueName(task.SaveFolder, task.FileName, task.Id);
            }
            Directory.CreateDirectory(task.SaveFolder);

            // On a fresh start we record the server's file identity for later resume
            // checks; on a resume we verify nothing changed before writing more bytes.
            // A link refresh (UpdateLink) intentionally bypasses the ETag check — the
            // new URL may serve the same file with different headers — but still guards
            // on size: a different size means a different file, so we restart from zero.
            if (!meta.IsHls && IsResuming(session) && !linkRefreshed)
            {
                ValidateFileIdentity(task, previousTotalBytes, meta.Etag, meta.LastModified);
            }
            else
            {
                if (!meta.IsHls && linkRefreshed && IsResuming(session) &&
                    previousTotalBytes > 0 && meta.TotalBytes > 0 && previousTotalBytes != meta.TotalBytes)
                {
                    if (File.Exists(session.StatePath))
                        File.Delete(session.StatePath);
                    if (File.Exists(task.FullPath))
                        File.Delete(task.FullPath);
                }
                RecordIdentity(task, meta.Etag, meta.LastModified);
            }

            if (meta.IsHls)
            {
                await RunHlsAsync(session, meta.ContentType);
            }
            else if (meta.TotalBytes > 0 && meta.SupportsRanges)
                await RunChunkedAsync(session, meta.TotalBytes);
            else
                await RunSingleStreamAsync(session, meta.ProbeBody);

            session.Token.ThrowIfCancellationRequested();
            task.Status = TaskStatus.Completed;
            task.CompletedAt = DateTime.Now;
            task.Progress = 100;
            task.SpeedBps = 0;
            task.Eta = "";
            TaskCompleted?.Invoke(task);
        }
        catch (OperationCanceledException)
        {
            if (session.Removed)
                return;
            // A cancel can land a hair after the last byte was written; don't mark a
            // fully downloaded file as Paused.
            if (IsFileComplete(task))
            {
                task.Status = TaskStatus.Completed;
                task.CompletedAt = DateTime.Now;
                task.Progress = 100;
                task.SpeedBps = 0;
                task.Eta = "";
                TaskCompleted?.Invoke(task);
                return;
            }
            task.Status = TaskStatus.Paused;
        }
        catch (Exception ex)
        {
            if (session.Removed)
                return;
            task.Status = ex is FileChangedException ? TaskStatus.Paused : TaskStatus.Failed;
            task.Error = ex.Message;
        }
        finally
        {
            session.Finish();
            lock (_lock)
            {
                // Only remove the session that is actually finishing; a paused task may
                // already have been resumed and started a brand-new session with the
                // same Id. Removing that one would orphan it (no pause/stop, no meter).
                if (_sessions.TryGetValue(task.Id, out var current) &&
                    ReferenceEquals(current, session) && session.Done)
                {
                    _sessions.Remove(task.Id);
                }
            }
            ReleaseReservedPath(task);
            TaskChanged?.Invoke();
            PumpQueue();
            lock (_lock)
            {
                if (ActiveCount == 0 && QueuedCount == 0)
                    _meter.Stop();
            }
            session.Dispose();
        }
    }

    private static IEnumerable<string> AllUrls(DownloadTask task)
    {
        yield return task.Url;
        if (task.Mirrors is not null)
        {
            foreach (var mirror in task.Mirrors)
            {
                string url = mirror.Trim();
                if (!string.IsNullOrWhiteSpace(url))
                    yield return url;
            }
        }
    }

    private async Task<ProbeMeta> ProbeAsync(DownloadTask task, CancellationToken ct)
    {
        // Try the primary URL first, then mirrors, until one yields usable info
        // (a size or a filename). Mirrors exist so a dead primary doesn't sink the
        // whole download.
        ProbeMeta? fallback = null;
        int index = 0;
        foreach (string url in AllUrls(task))
        {
            var meta = await ProbeUrlAsync(task, url, index, ct);
            fallback ??= meta;
            if (meta.TotalBytes > 0 || !string.IsNullOrWhiteSpace(meta.SuggestedName))
                return meta;
            index++;
        }
        return fallback ?? new ProbeMeta(-1, false, null, null, false, null, null, null, 0);
    }

    private async Task<ProbeMeta> ProbeUrlAsync(DownloadTask task, string url, int urlIndex, CancellationToken ct)
    {
        // Name sources in priority order: Content-Disposition (incl. S3 query form),
        // then URL path. This mirrors what IDM-class tools do before starting a download.
        string? suggestedName = null;
        string? contentType = null;
        string? etag = null;
        string? lastModified = null;
        long totalBytes = -1;
        bool supportsRanges = false;
        HttpResponseMessage? probeBody = null;

        // 1) HEAD probe - cheap, gives size + range support + disposition.
        try
        {
            var head = await SendWithRetryAsync(() => BuildRequest(HttpMethod.Head, task, null, url), ct);
            using (head)
            {
                if (IsCloudflareChallenge(head))
                    throw new CloudflareBlockedException(CloudflareMessage(url));
                if (head.IsSuccessStatusCode)
                {
                    supportsRanges = head.Headers.AcceptRanges.Any(r => r.Equals("bytes", StringComparison.OrdinalIgnoreCase));
                    long total = head.Content.Headers.ContentLength ?? -1;
                    if (total > 0)
                        totalBytes = total;
                    contentType = head.Content.Headers.ContentType?.ToString();
                    suggestedName = NameFromDisposition(head.Content.Headers.ContentDisposition);
                    etag = head.Headers.ETag?.ToString();
                    lastModified = head.Content.Headers.LastModified?.ToString("R");
                }
            }
        }
        catch (CloudflareBlockedException) { throw; }
        catch
        {
            // HEAD unsupported or rejected; fall through to ranged GET.
        }

        // 2) Ranged GET probe (bytes=1-1) - authoritative for size via Content-Range
        //    and proves range support. Sends Accept-Encoding: identity so Content-Length
        //    reflects the real size (a compressed body would corrupt chunk math).
        if (totalBytes <= 0 || !supportsRanges)
        {
            try
            {
                var get = await SendWithRetryAsync(() => BuildRequest(HttpMethod.Get, task, new RangeHeaderValue(1, 1), url), ct);
                if (IsCloudflareChallenge(get))
                {
                    string msg = CloudflareMessage(url);
                    get.Dispose();
                    throw new CloudflareBlockedException(msg);
                }
                if (get.StatusCode == HttpStatusCode.PartialContent)
                {
                    supportsRanges = true;
                    if (get.Content.Headers.ContentRange?.Length is long len && len > 0)
                        totalBytes = len;
                    etag ??= get.Headers.ETag?.ToString();
                    lastModified ??= get.Content.Headers.LastModified?.ToString("R");
                    suggestedName ??= NameFromDisposition(get.Content.Headers.ContentDisposition);
                    contentType ??= get.Content.Headers.ContentType?.ToString();
                    get.Dispose();
                }
                else if (get.StatusCode == HttpStatusCode.OK)
                {
                    // Server ignored the Range header; use Content-Length if present.
                    long len = get.Content.Headers.ContentLength ?? -1;
                    if (len > 0)
                        totalBytes = len;
                    supportsRanges = false;
                    etag ??= get.Headers.ETag?.ToString();
                    lastModified ??= get.Content.Headers.LastModified?.ToString("R");
                    suggestedName ??= NameFromDisposition(get.Content.Headers.ContentDisposition);
                    contentType ??= get.Content.Headers.ContentType?.ToString();
                    // Non-resumable (and often one-time) URL. Keep the response open so
                    // the body already sent by the server isn't wasted; single-stream
                    // consumes it directly instead of re-requesting the link.
                    probeBody = get;
                }
                else if (get.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
                {
                    // 416 with a Content-Range still tells us the total size, and
                    // proves the server understands ranges.
                    supportsRanges = true;
                    if (get.Content.Headers.ContentRange?.Length is long len && len > 0)
                        totalBytes = len;
                    etag ??= get.Headers.ETag?.ToString();
                    lastModified ??= get.Content.Headers.LastModified?.ToString("R");
                    suggestedName ??= NameFromDisposition(get.Content.Headers.ContentDisposition);
                    contentType ??= get.Content.Headers.ContentType?.ToString();
                    get.Dispose();
                }
                else
                {
                    if (get.StatusCode == HttpStatusCode.Forbidden && IsCloudflareChallenge(get))
                    {
                        string msg = CloudflareMessage(url);
                        get.Dispose();
                        throw new CloudflareBlockedException(msg);
                    }
                    suggestedName ??= NameFromDisposition(get.Content.Headers.ContentDisposition);
                    contentType ??= get.Content.Headers.ContentType?.ToString();
                    get.Dispose();
                }
            }
            catch (CloudflareBlockedException) { throw; }
            catch
            {
                // Server doesn't accept ranged GET requests.
            }
        }

        // 3) S3/Google-signed URL: filename may live in response-content-disposition
        //    query param, even when the response omits the header.
        suggestedName ??= FileNameHelper.FileNameFromS3Query(url);

        bool isHls = IsHlsContentType(contentType) || LooksLikeHlsUrl(url);
        return new ProbeMeta(totalBytes, supportsRanges, suggestedName, contentType, isHls, etag, lastModified, probeBody, urlIndex);
    }

    private static bool IsResuming(Session session)
    {
        var task = session.Task;
        if (File.Exists(session.StatePath))
            return true;
        return File.Exists(task.FullPath) && new FileInfo(task.FullPath).Length > 0;
    }

    /// <summary>True when the on-disk file already holds every byte the server
    /// promised (used to distinguish "cancelled before finishing" from "cancelled
    /// right after the last byte landed").</summary>
    private static bool IsFileComplete(DownloadTask task)
    {
        if (task.TotalBytes <= 0)
            return false;
        try
        {
            return File.Exists(task.FullPath) && new FileInfo(task.FullPath).Length >= task.TotalBytes;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Stores the server's identity (ETag/Last-Modified) on the task so a later
    /// resume can detect that the remote file changed.</summary>
    private void RecordIdentity(DownloadTask task, string? etag, string? lastModified)
    {
        bool changed = false;
        if (!string.IsNullOrWhiteSpace(etag) && !string.Equals(task.Etag, etag, StringComparison.Ordinal))
        {
            task.Etag = etag;
            changed = true;
        }
        if (!string.IsNullOrWhiteSpace(lastModified) &&
            !string.Equals(task.LastModified, lastModified, StringComparison.OrdinalIgnoreCase))
        {
            task.LastModified = lastModified;
            changed = true;
        }
        if (changed)
            TaskChanged?.Invoke();
    }

    /// <summary>Verifies the file we are resuming is still the same one we started.
    /// Throws FileChangedException when ETag/Last-Modified/size indicate the server's
    /// copy was replaced, so we don't assemble a corrupt file.</summary>
    private static void ValidateFileIdentity(DownloadTask task, long previousTotalBytes, string? etag, string? lastModified)
    {
        if (!string.IsNullOrWhiteSpace(task.Etag) && !string.IsNullOrWhiteSpace(etag) &&
            !string.Equals(task.Etag, etag, StringComparison.Ordinal))
        {
            throw new FileChangedException("The file changed on the server (ETag mismatch). Paused to avoid a corrupt file.");
        }
        if (string.IsNullOrWhiteSpace(task.Etag) && !string.IsNullOrWhiteSpace(task.LastModified) &&
            !string.IsNullOrWhiteSpace(lastModified) &&
            !string.Equals(task.LastModified, lastModified, StringComparison.OrdinalIgnoreCase))
        {
            throw new FileChangedException("The file changed on the server (Last-Modified mismatch). Paused to avoid a corrupt file.");
        }
        if (previousTotalBytes > 0 && task.TotalBytes > 0 && previousTotalBytes != task.TotalBytes)
        {
            throw new FileChangedException("The file size changed on the server. Paused to avoid a corrupt file.");
        }
    }

    private static bool LooksLikeHlsUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return false;
        return uri.AbsolutePath.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase)
            || uri.Query.Contains(".m3u8", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsHlsContentType(string? contentType) =>
        !string.IsNullOrWhiteSpace(contentType)
        && (contentType.Contains("mpegurl", StringComparison.OrdinalIgnoreCase)
            || contentType.Contains("m3u8", StringComparison.OrdinalIgnoreCase));

    private static string? NameFromDisposition(ContentDispositionHeaderValue? disposition)
    {
        if (disposition is null)
            return null;
        string? name = disposition.FileNameStar?.Trim('"');
        if (string.IsNullOrWhiteSpace(name))
            name = disposition.FileName?.Trim('"');
        // The .NET parser already decodes filename*= percent-encoding, but not always
        // for exotic RFC 2231 forms; re-parse the raw value defensively.
        if (!string.IsNullOrWhiteSpace(name))
        {
            string? parsed = FileNameHelper.ParseDispositionFileName(disposition.ToString());
            if (!string.IsNullOrWhiteSpace(parsed))
                name = parsed;
        }
        if (string.IsNullOrWhiteSpace(name) || !LooksLikeFileName(name))
            return null;
        name = SanitizeFileName(name);
        return IsMediaFile(name) ? CleanReleaseName(name) : name;
    }

    private static int AutoChunkCount(long totalBytes)
    {
        if (totalBytes <= 0)
            return 1;
        long mb = totalBytes / (1024 * 1024);
        if (mb < 1) return 1;
        if (mb < 5) return 2;
        if (mb < 25) return 4;
        if (mb < 100) return 8;
        if (mb < 500) return 16;
        return 32;
    }

    private async Task RunChunkedAsync(Session session, long totalBytes)
    {
        var task = session.Task;
        int count = task.ChunkCount > 0 ? task.ChunkCount : AutoChunkCount(totalBytes);
        if (task.ChunkCount != count)
            task.ChunkCount = count;

        // Dynamic segmentation: a shared pool of chunks keeps every thread busy until
        // the file is done, regardless of which segments finish early.
        long chunkSize = totalBytes / (count * 8L);
        chunkSize = Math.Clamp(chunkSize, 128 * 1024, 16 * 1024 * 1024);
        if (chunkSize < 1)
            chunkSize = 1;
        int chunkCount = (int)((totalBytes + chunkSize - 1) / chunkSize);

        session.State = ChunkState.Load(session.StatePath, totalBytes, chunkSize, chunkCount);
        session.ChunkSize = chunkSize;
        Interlocked.Exchange(ref session.BytesDownloaded, session.State.CompletedBytes);
        session.LastBytes = session.State.CompletedBytes;

        await using (var prealloc = new FileStream(task.FullPath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.ReadWrite))
        {
            if (prealloc.Length != totalBytes)
                prealloc.SetLength(totalBytes);
        }

        var workers = new List<Task>();
        for (int w = 0; w < count; w++)
        {
            int worker = w;
            workers.Add(Task.Run(() => RunChunkWorkerAsync(session, worker)));
        }
        await Task.WhenAll(workers);
        session.Token.ThrowIfCancellationRequested();

        if (session.State.Completed != chunkCount)
            throw new InvalidOperationException("Download did not complete all segments.");

        session.State.Delete(session.StatePath);
    }

    private async Task RunChunkWorkerAsync(Session session, int worker)
    {
        var task = session.Task;
        var state = session.State ?? throw new InvalidOperationException("Chunk state missing.");
        await using var output = new FileStream(task.FullPath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.ReadWrite);

        while (true)
        {
            int index = Interlocked.Increment(ref session.NextChunk) - 1;
            if (index >= state.ChunkCount)
                return;
            if (state.IsCompleted(index))
                continue;

            long from = (long)index * session.ChunkSize;
            long to = Math.Min(from + session.ChunkSize, task.TotalBytes) - 1;
            if (from > to)
            {
                state.SetCompleted(index);
                continue;
            }

            await DownloadChunkWithRetryAsync(session, output, from, to, index, state);
        }
    }

    private async Task DownloadChunkWithRetryAsync(Session session, FileStream output, long from, long to, int index, ChunkState state)
    {
        var task = session.Task;
        int attempt = 0;
        while (true)
        {
            long chunkBytes;
            try
            {
                chunkBytes = await DownloadChunkAsync(session, output, from, to);
            }
            catch (Exception ex) when ((IsTransient(ex) || ex is InvalidOperationException or HttpRequestException) &&
                                       !session.Token.IsCancellationRequested)
            {
                if (attempt < MaxRetries)
                {
                    await BackoffAsync(attempt, session.Token);
                    attempt++;
                    continue;
                }
                // Retries on the current URL are exhausted; fall over to the next
                // mirror and give the chunk a fresh set of attempts.
                if (session.RotateUrl(task))
                {
                    attempt = 0;
                    continue;
                }
                throw;
            }

            if (chunkBytes < 0)
                return;
            state.SetCompleted(index);
            state.SaveIfDirty(session.StatePath);
            return;
        }
    }

    private async Task<long> DownloadChunkAsync(Session session, FileStream output, long from, long to)
    {
        var task = session.Task;
        var response = await SendWithRetryAsync(() => BuildRequest(HttpMethod.Get, task, new RangeHeaderValue(from, to), session.CurrentUrl(task)), session.Token);
        using (response)
        {
            if (IsCloudflareChallenge(response))
                throw new CloudflareBlockedException(CloudflareMessage(session.CurrentUrl(task)));
            if (response.StatusCode != HttpStatusCode.PartialContent)
                throw new InvalidOperationException("Server does not support range downloads.");

            await using var input = await response.Content.ReadAsStreamAsync(session.Token);
            output.Position = from;
            var buffer = new byte[256 * 1024];
            long written = 0;
            int read;
            while ((read = await input.ReadAsync(buffer, session.Token)) > 0)
            {
                await _governor.ThrottleAsync(EffectiveLimitKbps(), read, session.Token);
                await session.Governor.ThrottleAsync(task.SpeedLimitKbps, read, session.Token);
                await output.WriteAsync(buffer.AsMemory(0, read), session.Token);
                written += read;
                Interlocked.Add(ref session.BytesDownloaded, read);
                session.Token.ThrowIfCancellationRequested();
            }
            if (written < to - from + 1)
                throw new HttpRequestException($"Chunk incomplete: got {written} of {to - from + 1} bytes.");
            return written;
        }
    }

    private async Task RunHlsAsync(Session session, string? contentType)
    {
        var task = session.Task;

        // HLS streams download as one continuous media file; the manifest URL usually
        // ends in .m3u8, so give the output a real media extension.
        string extension = ".ts";
        if (contentType is not null && contentType.Contains("mp4", StringComparison.OrdinalIgnoreCase))
            extension = ".mp4";
        if (task.FileName.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase)
            || task.FileName.EndsWith(".m3u", StringComparison.OrdinalIgnoreCase))
        {
            task.FileName = Path.ChangeExtension(task.FileName, extension);
            task.FileName = EnsureUniqueName(task.SaveFolder, task.FileName, task.Id);
        }

        await HlsDownloader.DownloadAsync(
            _http,
            task.Url,
            task.Referer,
            task.FullPath,
            session.Token,
            bytes => Interlocked.Add(ref session.BytesDownloaded, bytes),
            total => task.TotalBytes = total,
            async (bytes, ct) =>
            {
                await _governor.ThrottleAsync(EffectiveLimitKbps(), bytes, ct);
                await session.Governor.ThrottleAsync(task.SpeedLimitKbps, bytes, ct);
            });

        session.Token.ThrowIfCancellationRequested();
    }

    private async Task RunSingleStreamAsync(Session session, HttpResponseMessage? probeBody)
    {
        var task = session.Task;

        // If the probe already holds the full body (non-resumable / one-time URL),
        // stream it straight to disk instead of re-requesting the link. Only when
        // nothing is on disk yet — resuming a non-range download must re-request.
        if (probeBody is not null && !(File.Exists(task.FullPath) && new FileInfo(task.FullPath).Length > 0))
        {
            try
            {
                await RunSingleStreamBodyAsync(session, probeBody);
                return;
            }
            catch (Exception ex) when (IsTransient(ex) && !session.Token.IsCancellationRequested)
            {
                // Probe body stream died mid-transfer; fall through to fresh requests.
            }
            finally
            {
                probeBody.Dispose();
            }
        }
        else
        {
            probeBody?.Dispose();
        }

        int attempt = 0;
        while (true)
        {
            try
            {
                await RunSingleStreamAttemptAsync(session);
                return;
            }
            catch (Exception ex) when (IsTransient(ex) && !session.Token.IsCancellationRequested)
            {
                if (attempt < MaxRetries)
                {
                    await BackoffAsync(attempt, session.Token);
                    attempt++;
                }
                else if (session.RotateUrl(task))
                {
                    attempt = 0;
                }
                else
                {
                    throw;
                }
            }
        }
    }

    private async Task RunSingleStreamBodyAsync(Session session, HttpResponseMessage response)
    {
        var task = session.Task;
        response.EnsureSuccessStatusCode();
        await using var input = await response.Content.ReadAsStreamAsync(session.Token);
        await using var output = new FileStream(task.FullPath, FileMode.Create, FileAccess.Write, FileShare.Read);

        var buffer = new byte[256 * 1024];
        int read;
        while ((read = await input.ReadAsync(buffer, session.Token)) > 0)
        {
            await _governor.ThrottleAsync(EffectiveLimitKbps(), read, session.Token);
            await session.Governor.ThrottleAsync(task.SpeedLimitKbps, read, session.Token);
            await output.WriteAsync(buffer.AsMemory(0, read), session.Token);
            Interlocked.Add(ref session.BytesDownloaded, read);
            session.Token.ThrowIfCancellationRequested();
        }
    }

    private async Task RunSingleStreamAttemptAsync(Session session)
    {
        var task = session.Task;
        long existingLength = 0;
        if (File.Exists(task.FullPath))
        {
            existingLength = new FileInfo(task.FullPath).Length;
        }

        RangeHeaderValue? range = existingLength > 0 ? new RangeHeaderValue(existingLength, null) : null;
        var response = await SendWithRetryAsync(() => BuildRequest(HttpMethod.Get, task, range, session.CurrentUrl(task)), session.Token);
        using (response)
        {
            if (IsCloudflareChallenge(response))
                throw new CloudflareBlockedException(CloudflareMessage(session.CurrentUrl(task)));
            // Resuming at EOF: server says the range is unsatisfiable because the file
            // is already fully downloaded. Treat that as success.
            if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable && existingLength > 0)
            {
                if (response.Content.Headers.ContentRange?.Length is long len && len > 0)
                    task.TotalBytes = len;
                Interlocked.Exchange(ref session.BytesDownloaded, existingLength);
                return;
            }

            response.EnsureSuccessStatusCode();

            bool isPartial = response.StatusCode == HttpStatusCode.PartialContent;
            if (!isPartial)
            {
                existingLength = 0;
            }

            if (response.Content.Headers.ContentLength is long length && length > 0)
            {
                if (isPartial)
                    task.TotalBytes = existingLength + length;
                else if (task.TotalBytes < 0)
                    task.TotalBytes = length;
            }

            Interlocked.Exchange(ref session.BytesDownloaded, existingLength);
            session.LastBytes = existingLength;

            await using var input = await response.Content.ReadAsStreamAsync(session.Token);
            FileMode mode = isPartial && existingLength > 0 ? FileMode.Append : FileMode.Create;
            await using var output = new FileStream(task.FullPath, mode, FileAccess.Write, FileShare.Read);

            var buffer = new byte[256 * 1024];
            int read;
            while ((read = await input.ReadAsync(buffer, session.Token)) > 0)
            {
                await _governor.ThrottleAsync(EffectiveLimitKbps(), read, session.Token);
                await session.Governor.ThrottleAsync(task.SpeedLimitKbps, read, session.Token);
                await output.WriteAsync(buffer.AsMemory(0, read), session.Token);
                Interlocked.Add(ref session.BytesDownloaded, read);
                session.Token.ThrowIfCancellationRequested();
            }
        }
    }

    private void RefreshSpeeds()
    {
        Session[] snapshot;
        lock (_lock) snapshot = _sessions.Values.Where(s => !s.Removed).ToArray();
        if (snapshot.Length == 0)
            return;

        long total = 0;
        foreach (var session in snapshot)
        {
            long now = Interlocked.Read(ref session.BytesDownloaded);
            double speed = (now - session.LastBytes) * 2.0;
            session.LastBytes = now;
            session.Task.SpeedBps = speed;
            session.Task.DownloadedBytes = now;
            total += (long)speed;

            if (session.Task.TotalBytes > 0)
            {
                int percent = (int)(now * 100 / session.Task.TotalBytes);
                session.Task.Progress = Math.Clamp(percent, 0, 100);
                double remaining = session.Task.TotalBytes - now;
                session.Task.Eta = speed > 1 ? FormatEta(remaining / speed) : "";
            }

            if (session.State is not null)
                ChunkProgressUpdated?.Invoke(session.Task, session.State.ProgressPercent());
        }
        Interlocked.Exchange(ref _totalSpeedBps, total);
    }

    private long EffectiveLimitKbps()
    {
        lock (_lock)
        {
            return _baseLimitKbps;
        }
    }

    private void ApplySpeedLimit()
    {
        _governor.LimitKbps = EffectiveLimitKbps();
    }

    private async Task<HttpResponseMessage> SendWithRetryAsync(Func<HttpRequestMessage> build, CancellationToken ct)
    {
        int attempt = 0;
        while (true)
        {
            using var request = build();
            HttpResponseMessage response;
            try
            {
                response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            }
            catch (Exception ex) when (IsTransient(ex) && !ct.IsCancellationRequested && attempt < MaxRetries)
            {
                await BackoffAsync(attempt, ct);
                attempt++;
                continue;
            }
            catch (OperationCanceledException)
            {
                throw;
            }

            int code = (int)response.StatusCode;
            bool serverError = code == 408 || code == 429 || code >= 500;
            if (serverError && attempt < MaxRetries)
            {
                response.Dispose();
                await BackoffAsync(attempt, ct);
                attempt++;
                continue;
            }
            return response;
        }
    }

    private static bool IsTransient(Exception ex) =>
        ex is HttpRequestException or IOException or TaskCanceledException;

    private static async Task BackoffAsync(int attempt, CancellationToken ct)
    {
        int ms = (int)Math.Min(8000, 500 * Math.Pow(2, attempt));
        await Task.Delay(ms, ct);
    }

    private static HttpRequestMessage BuildRequest(HttpMethod method, DownloadTask task, RangeHeaderValue? range, string? url = null)
    {
        var request = new HttpRequestMessage(method, url ?? task.Url);
        if (range is not null)
            request.Headers.Range = range;
        if (!string.IsNullOrWhiteSpace(task.Referer) && Uri.TryCreate(task.Referer, UriKind.Absolute, out var referer))
            request.Headers.Referrer = referer;
        // Apply per-task custom headers (e.g. Cookie, Authorization, Referer).
        foreach (var kv in task.Headers)
        {
            if (string.IsNullOrWhiteSpace(kv.Key) || string.IsNullOrWhiteSpace(kv.Value))
                continue;
            request.Headers.TryAddWithoutValidation(kv.Key, kv.Value);
        }
        // Ask for the raw (uncompressed) representation so Content-Length is the true
        // file size and byte ranges line up with what we actually receive.
        request.Headers.TryAddWithoutValidation("Accept-Encoding", "identity");
        // Browser-like headers reduce Cloudflare/bot-filter false positives (testfile.org etc.)
        request.Headers.TryAddWithoutValidation("Accept", "*/*");
        request.Headers.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9");
        request.Headers.TryAddWithoutValidation("Cache-Control", "no-cache");
        request.Headers.TryAddWithoutValidation("Pragma", "no-cache");
        if (string.IsNullOrWhiteSpace(task.Referer) && Uri.TryCreate(url ?? task.Url, UriKind.Absolute, out var targetUri))
            request.Headers.TryAddWithoutValidation("Referer", $"{targetUri.Scheme}://{targetUri.Host}/");
        return request;
    }

    private static bool IsCloudflareChallenge(HttpResponseMessage response)
    {
        if (response.StatusCode != HttpStatusCode.Forbidden)
            return false;
        if (response.Headers.TryGetValues("cf-mitigated", out var vals) && vals.Any(v => v.IndexOf("challenge", StringComparison.OrdinalIgnoreCase) >= 0))
            return true;
        bool hasCfRay = response.Headers.Contains("cf-ray") || response.Headers.Contains("CF-RAY");
        bool isCloudflare = response.Headers.Server.Any(s => string.Equals(s.Product?.Name, "cloudflare", StringComparison.OrdinalIgnoreCase));
        return hasCfRay && isCloudflare;
    }

    private static string CloudflareMessage(string url) =>
        $"Cloudflare blocked this download (403). The server flagged WDM as a bot. Try: 1) Open {url} in your browser and let it download once, then paste the final direct link (copy link address) into WDM, or 2) install the WDM browser extension (Options → Browser Integration) and capture the download from the page.";

    public sealed class CloudflareBlockedException : Exception
    {
        public CloudflareBlockedException(string message) : base(message) { }
    }

    internal static string FormatEta(double seconds)
    {
        if (seconds <= 0 || double.IsInfinity(seconds) || double.IsNaN(seconds))
            return "";
        if (seconds < 60)
            return $"{Math.Ceiling(seconds)}s";
        if (seconds < 3600)
            return $"{Math.Ceiling(seconds / 60)}m";
        return $"{Math.Round(seconds / 3600, 1)}h";
    }

    public static string DeriveName(string url, string? contentType = null)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            string name = Uri.UnescapeDataString(Path.GetFileName(uri.AbsolutePath));
            if (!string.IsNullOrWhiteSpace(name) && LooksLikeFileName(name))
            {
                // HLS manifests download as a single concatenated media file.
                if (name.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase)
                    || name.EndsWith(".m3u", StringComparison.OrdinalIgnoreCase))
                    name = Path.ChangeExtension(name, ".ts");
                string cleaned = SanitizeFileName(name);
                if (IsMediaFile(cleaned))
                    cleaned = CleanReleaseName(cleaned);
                return cleaned;
            }
        }

        // URL carries no usable filename (signed/tokenized paths): fall back to a
        // proper extension derived from the MIME type instead of a generic .bin.
        string ext = FileNameHelper.ExtensionFromMime(contentType);
        if (ext.Length > 0)
            return $"download_{DateTime.Now:yyyyMMdd_HHmmss}{ext}";
        return FallbackName();
    }

    /// <summary>True if the file extension indicates a video or audio media file.</summary>
    public static bool IsMediaFile(string fileName)
    {
        string ext = Path.GetExtension(fileName).TrimStart('.').ToLowerInvariant();
        return VideoExtensions.Contains(ext) || AudioExtensions.Contains(ext);
    }

    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "mp4", "mkv", "avi", "mov", "wmv", "flv", "webm", "m4v", "mpg", "mpeg", "3gp", "ts", "mts", "m2ts",
    };
    private static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "mp3", "wav", "flac", "aac", "ogg", "wma", "m4a", "opus", "mid", "midi", "ape", "aiff",
    };

    /// <summary>
    /// Removes percent-encoding junk and release-group metadata tags (resolution,
    /// codec, year, language tags, scene groups) from a media filename, while keeping
    /// the full human-readable title. Only applied to names that carry a file extension.
    /// </summary>
    public static string CleanReleaseName(string name)
    {
        string ext = Path.GetExtension(name);
        string stem = Path.GetFileNameWithoutExtension(name);
        if (string.IsNullOrWhiteSpace(ext) || string.IsNullOrWhiteSpace(stem))
            return name;

        // Split into tokens on common separators (whitespace, dot, underscore, dash,
        // parens, plus, percent). Drop tokens that are release metadata, keep the rest.
        string[] tokens = System.Text.RegularExpressions.Regex.Split(
            stem, @"[\s._\-–—()\[\]+%]+", System.Text.RegularExpressions.RegexOptions.ExplicitCapture);

        var kept = new List<string>();
        foreach (string raw in tokens)
        {
            string t = raw.Trim();
            if (string.IsNullOrWhiteSpace(t))
                continue;
            if (IsReleaseToken(t))
                continue;
            kept.Add(t);
        }

        // Collapse duplicate spaces / normalize.
        string title = string.Join(" ", kept).Trim();
        if (string.IsNullOrWhiteSpace(title))
            title = stem;

        return $"{title}{ext}";
    }

    private static bool IsReleaseToken(string token)
    {
        if (System.Text.RegularExpressions.Regex.IsMatch(token, @"^(19|20)\d{2}$"))
            return true; // year, e.g. 2024
        if (System.Text.RegularExpressions.Regex.IsMatch(token, @"^\d{3,4}[pi]$", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            return true; // resolution, e.g. 720p / 1080p / 2160p
        if (System.Text.RegularExpressions.Regex.IsMatch(token, @"^\d{1,3}0?fps$", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            return true; // frame rate, e.g. 60fps
        if (System.Text.RegularExpressions.Regex.IsMatch(token, @"^(S\d{1,2})(E\d{1,2})$", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            return true; // episode marker, e.g. S01E01
        if (System.Text.RegularExpressions.Regex.IsMatch(token, @"^\d{1,3}bit$", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            return true; // bit depth, e.g. 10bit

        string[] tags =
        {
            // Codecs / containers
            "HEVC", "x264", "x265", "H264", "H265", "AVC", "AV1", "HDR", "HDR10", "DV", "Dolby",
            "AAC", "DDP", "AC3", "DTS", "TrueHD", "5.1", "7.1", "2.0", "10BIT", "8BIT",
            // Sources / quality
            "HDTV", "WEB", "WEBRIP", "WEB-DL", "BluRay", "BRRip", "HDRip", "DVDRip", "REMUX",
            "CAM", "HQCam", "HDCAM", "HDTS", "TS", "PDVD", "BDRip", "DVDRip", "HDDVDRip",
            // Subtitle / encode markers
            "ESub", "Subs", "MultiSub", "Proper", "Repack", "Retail", "READNFO",
            // Scene / release groups
            "YIFY", "RARBG", "MoviesMod", "World4uFree", "WorldFree4u", "GalaxyRG", "Team", "Film",
            "HDHub", "Torrent", "x0r", "eztv", "SVA", "Hub", "CtrlHD", "GECKOS", "D-Z0N3",
            // Domain / group TLD suffixes that leak through (e.g. MoviesMod.at)
            "AT", "COM", "NET", "ORG", "XYZ", "CC", "IN",
        };
        return tags.Contains(token, StringComparer.OrdinalIgnoreCase);
    }

    public static bool LooksLikeFileName(string name)
    {
        if (name.Length > 120)
            return false;
        string ext = Path.GetExtension(name);
        return ext.Length is >= 2 and <= 8;
    }

    private static string FallbackName() =>
        $"download_{DateTime.Now:yyyy-MM-dd_HHmmss}.bin";

    public static string SanitizeFileName(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        name = name.Trim();
        return string.IsNullOrWhiteSpace(name) ? $"download_{DateTime.Now:yyyyMMddHHmmss}.bin" : name;
    }

    private string EnsureUniqueName(string folder, string name, Guid taskId)
    {
        string full = Path.Combine(folder, name);
        lock (_lock)
        {
            if (!File.Exists(full) && !_reservedPaths.Contains(full))
            {
                _reservedPaths.Add(full);
                return name;
            }

            string baseName = Path.GetFileNameWithoutExtension(name);
            string ext = Path.GetExtension(name);
            for (int i = 1; ; i++)
            {
                string candidate = $"{baseName} ({i}){ext}";
                string candidateFull = Path.Combine(folder, candidate);
                if (!File.Exists(candidateFull) && !_reservedPaths.Contains(candidateFull))
                {
                    _reservedPaths.Add(candidateFull);
                    return candidate;
                }
            }
        }
    }

    private void ReleaseReservedPath(DownloadTask task)
    {
        lock (_lock) _reservedPaths.Remove(task.FullPath);
    }

    private static string StatePath(DownloadTask task) => $"{task.FullPath}.wdmstate";

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // Ignore transient file locks.
        }
    }

    /// <summary>Raised when the file's identity (ETag/Last-Modified/size) changed on the
    /// server between download runs, so resuming would produce a corrupt file.</summary>
    public sealed class FileChangedException : Exception
    {
        public FileChangedException(string message) : base(message) { }
    }

    private sealed record ProbeMeta(
        long TotalBytes,
        bool SupportsRanges,
        string? SuggestedName,
        string? ContentType,
        bool IsHls,
        string? Etag,
        string? LastModified,
        HttpResponseMessage? ProbeBody,
        int UrlIndex);

    /// <summary>Records whether the current source can be resumed mid-transfer. Mirrors
    /// the branch taken in <see cref="RunSessionAsync"/>: chunked only when the size is
    /// known and the server honors Range requests.</summary>
    private static void ApplyResumeCapability(DownloadTask task, ProbeMeta meta)
    {
        task.IsResumable = false;
        if (meta.IsHls)
            task.ResumeCapabilityText = "No — HLS segment stream";
        else if (meta.TotalBytes > 0 && meta.SupportsRanges)
        {
            task.IsResumable = true;
            task.ResumeCapabilityText = "Yes — multithreaded chunking";
        }
        else if (meta.TotalBytes <= 0)
            task.ResumeCapabilityText = "No — size unknown";
        else
            task.ResumeCapabilityText = "No — server doesn't support ranges";
    }

    private sealed class Session : IDisposable
    {
        public Session(DownloadTask task)
        {
            Task = task;
        }

        public DownloadTask Task { get; }
        public CancellationTokenSource Cts { get; } = new();
        public CancellationToken Token => Cts.Token;
        public ChunkState? State { get; set; }
        public long ChunkSize;
        public int NextChunk;
        public long BytesDownloaded;
        public long LastBytes;
        public bool Removed;
        public bool Done;
        public SpeedGovernor Governor { get; } = new();

        public int CurrentUrlIndex;
        public string CurrentUrl(DownloadTask task)
        {
            var mirrors = task.Mirrors;
            if (CurrentUrlIndex > 0 && mirrors is { Count: > 0 } && CurrentUrlIndex <= mirrors.Count)
                return mirrors[CurrentUrlIndex - 1];
            return task.Url;
        }

        public bool RotateUrl(DownloadTask task)
        {
            int total = 1 + (task.Mirrors?.Count ?? 0);
            if (total <= 1)
                return false;
            CurrentUrlIndex = (CurrentUrlIndex + 1) % total;
            return true;
        }

        public string StatePath => $"{Task.FullPath}.wdmstate";

        /// <summary>Task backing <see cref="DownloadEngine.RunSessionAsync"/>; completes
        /// only after every chunk worker and file stream has unwound.</summary>
        public Task? RunningTask { get; set; }

        public void Cancel() => Cts.Cancel();

        public void Finish()
        {
            lock (this)
            {
                Done = true;
                Cts.Cancel();
            }
        }

        public void Dispose() => Cts.Dispose();
    }

    private sealed class ChunkState
    {
        private const string Magic = "WDMSTATE1";
        private byte[] _bits;
        private long _totalBytes;
        private long _chunkSize;
        private readonly int _chunkCount;
        private long _completed;
        private long _lastSaveTick;
        private readonly object _lock = new();

        private ChunkState(long totalBytes, long chunkSize, int chunkCount)
        {
            _totalBytes = totalBytes;
            _chunkSize = chunkSize;
            _chunkCount = chunkCount;
            _bits = new byte[(chunkCount + 7) / 8];
        }

        public int ChunkCount => _chunkCount;
        public long CompletedBytes
        {
            get
            {
                int completed = (int)Interlocked.Read(ref _completed);
                if (completed <= 0)
                    return 0;
                // The final chunk is usually smaller than _chunkSize, so count it by its real length.
                long lastChunkSize = _totalBytes - ((long)_chunkCount - 1) * _chunkSize;
                if (lastChunkSize <= 0 || lastChunkSize > _chunkSize)
                    lastChunkSize = _chunkSize;
                lock (_lock)
                {
                    bool lastCompleted = (_bits[(_chunkCount - 1) >> 3] & (1 << ((_chunkCount - 1) & 7))) != 0;
                    if (lastCompleted)
                        return ((long)completed - 1) * _chunkSize + lastChunkSize;
                }
                return (long)completed * _chunkSize;
            }
        }
        public int Completed { get { lock (_lock) return CountBits(); } }

        public static ChunkState Load(string path, long totalBytes, long chunkSize, int chunkCount)
        {
            try
            {
                if (File.Exists(path))
                {
                    var state = JsonSerializer.Deserialize<StateRecord>(File.ReadAllText(path));
                    if (state is not null && state.Magic == Magic && state.TotalBytes == totalBytes &&
                        state.ChunkSize == chunkSize && state.ChunkCount == chunkCount)
                    {
                        var loaded = new ChunkState(totalBytes, chunkSize, chunkCount)
                        {
                            _bits = Convert.FromBase64String(state.Bits),
                        };
                        if (loaded._bits.Length == (chunkCount + 7) / 8)
                        {
                            loaded.CountCompleted(ref loaded._completed);
                            return loaded;
                        }
                    }
                }
            }
            catch
            {
                // Fall through to a fresh state.
            }
            var fresh = new ChunkState(totalBytes, chunkSize, chunkCount);
            fresh.Save(path);
            return fresh;
        }

        public bool IsCompleted(int index)
        {
            lock (_lock)
                return (_bits[index >> 3] & (1 << (index & 7))) != 0;
        }

        public void SetCompleted(int index)
        {
            lock (_lock)
            {
                if (index < _chunkCount && (_bits[index >> 3] & (1 << (index & 7))) == 0)
                {
                    _bits[index >> 3] |= (byte)(1 << (index & 7));
                    Interlocked.Increment(ref _completed);
                }
            }
        }

        public void SaveIfDirty(string path)
        {
            long now = Environment.TickCount64;
            long last = Interlocked.Read(ref _lastSaveTick);
            if (last != 0 && now - last < 1000)
                return;
            Save(path);
            Interlocked.Exchange(ref _lastSaveTick, Environment.TickCount64);
        }

        public void Save(string path)
        {
            lock (_lock)
            {
                var record = new StateRecord
                {
                    Magic = Magic,
                    TotalBytes = _totalBytes,
                    ChunkSize = _chunkSize,
                    ChunkCount = _chunkCount,
                    Bits = Convert.ToBase64String(_bits),
                };
                AtomicFile.Write(path, JsonSerializer.Serialize(record));
            }
        }

        public void Delete(string statePath)
        {
            try
            {
                File.Delete(statePath);
            }
            catch
            {
                // Ignore.
            }
        }

        private int CountBits()
        {
            int count = 0;
            for (int i = 0; i < _chunkCount; i++)
            {
                if ((_bits[i >> 3] & (1 << (i & 7))) != 0)
                    count++;
            }
            return count;
        }

        private void CountCompleted(ref long completed)
        {
            Interlocked.Exchange(ref completed, CountBits());
        }

        public double[] ProgressPercent()
        {
            lock (_lock)
            {
                var result = new double[_chunkCount];
                for (int i = 0; i < _chunkCount; i++)
                {
                    if ((_bits[i >> 3] & (1 << (i & 7))) != 0)
                        result[i] = 100.0;
                }
                return result;
            }
        }

        private sealed class StateRecord
        {
            public string Magic { get; set; } = "";
            public long TotalBytes { get; set; }
            public long ChunkSize { get; set; }
            public int ChunkCount { get; set; }
            public string Bits { get; set; } = "";
        }
    }

    private async Task RunYouTubeSessionAsync(Session session)
    {
        var task = session.Task;
        task.Status = TaskStatus.Downloading;
        TaskChanged?.Invoke();

        try
        {
            Directory.CreateDirectory(task.SaveFolder);
            var outFile = task.FullPath;

            var args = new List<string>
            {
                "--no-warnings",
                "--no-color",
                "--newline",
                "-o", outFile,
                task.Url
            };

            if (!string.IsNullOrWhiteSpace(task.YouTubeFormatArg))
            {
                args.Add("-f");
                args.Add(task.YouTubeFormatArg);
            }

            if (!string.IsNullOrWhiteSpace(task.YouTubeExtraArgs))
            {
                foreach (var a in task.YouTubeExtraArgs.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    args.Add(a);
            }

            if (File.Exists(EngineManager.FfmpegPath))
            {
                args.Add("--ffmpeg-location");
                args.Add(EngineManager.FfmpegPath);
            }

            var psi = YtDlpRunner.CreateInfo(args);
            using var proc = Process.Start(psi);
            if (proc is null)
                throw new Exception("Failed to start yt-dlp process.");

            session.Token.Register(() => YtDlpRunner.KillTree(proc));

            var outTask = Task.Run(async () =>
            {
                using var reader = proc.StandardOutput;
                while (await reader.ReadLineAsync() is { } line)
                {
                    ParseYtDlpOutputLine(line, task);
                    TaskChanged?.Invoke();
                }
            });

            var errTask = Task.Run(async () =>
            {
                using var reader = proc.StandardError;
                while (await reader.ReadLineAsync() is { } line)
                {
                    ParseYtDlpOutputLine(line, task);
                    TaskChanged?.Invoke();
                }
            });

            await proc.WaitForExitAsync(session.Token);
            await Task.WhenAll(outTask, errTask);

            if (session.Token.IsCancellationRequested)
            {
                task.Status = TaskStatus.Paused;
                task.SpeedBps = 0;
                task.Eta = "";
            }
            else if (proc.ExitCode == 0)
            {
                task.DownloadedBytes = task.TotalBytes > 0 ? task.TotalBytes : (File.Exists(outFile) ? new FileInfo(outFile).Length : 0);
                if (task.TotalBytes <= 0) task.TotalBytes = task.DownloadedBytes;
                task.Status = TaskStatus.Completed;
                task.CompletedAt = DateTime.Now;
                task.SpeedBps = 0;
                task.Eta = "";
                TaskCompleted?.Invoke(task);
            }
            else
            {
                task.Status = TaskStatus.Failed;
                task.Error = "yt-dlp exited with error code " + proc.ExitCode;
                task.SpeedBps = 0;
                task.Eta = "";
            }
        }
        catch (OperationCanceledException)
        {
            task.Status = TaskStatus.Paused;
            task.SpeedBps = 0;
            task.Eta = "";
        }
        catch (Exception ex)
        {
            task.Status = TaskStatus.Failed;
            task.Error = ex.Message;
            task.SpeedBps = 0;
            task.Eta = "";
        }
        finally
        {
            lock (_lock) _sessions.Remove(task.Id);
            TaskChanged?.Invoke();
            PumpQueue();
        }
    }

    private static void ParseYtDlpOutputLine(string line, DownloadTask task)
    {
        if (line.StartsWith("[download]") && line.Contains("%"))
        {
            try
            {
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                for (int i = 0; i < parts.Length; i++)
                {
                    if (parts[i] == "of" && i + 1 < parts.Length)
                    {
                        long bytes = ParseSizeToBytes(parts[i + 1]);
                        if (bytes > 0)
                        {
                            task.TotalBytes = bytes;
                        }
                    }
                    if (parts[i] == "at" && i + 1 < parts.Length)
                    {
                        long bps = ParseSpeedToBps(parts[i + 1]);
                        if (bps >= 0)
                        {
                            task.SpeedBps = bps;
                        }
                    }
                    if (parts[i] == "ETA" && i + 1 < parts.Length)
                    {
                        task.Eta = parts[i + 1];
                    }
                }
                if (task.TotalBytes > 0 && line.Contains("%"))
                {
                    int pctIdx = line.IndexOf('%');
                    int startIdx = line.LastIndexOf(' ', pctIdx);
                    if (startIdx >= 0 && double.TryParse(line.Substring(startIdx, pctIdx - startIdx).Trim(), out double p))
                    {
                        task.DownloadedBytes = (long)(task.TotalBytes * (p / 100.0));
                    }
                }
            }
            catch
            {
                // ignore parsing errors
            }
        }
    }

    private static long ParseSizeToBytes(string sizeStr)
    {
        try
        {
            double mult = 1;
            if (sizeStr.EndsWith("GiB", StringComparison.OrdinalIgnoreCase)) mult = 1024L * 1024 * 1024;
            else if (sizeStr.EndsWith("MiB", StringComparison.OrdinalIgnoreCase)) mult = 1024L * 1024;
            else if (sizeStr.EndsWith("KiB", StringComparison.OrdinalIgnoreCase)) mult = 1024L;
            else if (sizeStr.EndsWith("GB", StringComparison.OrdinalIgnoreCase)) mult = 1000L * 1000 * 1000;
            else if (sizeStr.EndsWith("MB", StringComparison.OrdinalIgnoreCase)) mult = 1000L * 1000;
            else if (sizeStr.EndsWith("KB", StringComparison.OrdinalIgnoreCase)) mult = 1000L;

            var numStr = new string(sizeStr.Where(c => char.IsDigit(c) || c == '.').ToArray());
            if (double.TryParse(numStr, out double val))
                return (long)(val * mult);
        }
        catch { }
        return 0;
    }

    private static long ParseSpeedToBps(string speedStr)
    {
        try
        {
            var clean = speedStr.Replace("/s", "", StringComparison.OrdinalIgnoreCase);
            return ParseSizeToBytes(clean);
        }
        catch { }
        return 0;
    }
}
