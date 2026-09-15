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

    /// <summary>Raised when a download was blocked by a Cloudflare-style managed
    /// challenge. The UI layer may open its embedded-browser solver and requeue
    /// the task; the engine itself takes no further action for that task.</summary>
    public event Action<DownloadTask>? CloudflareBlocked;

    /// <summary>Raised when an embed page demands human interaction (captcha,
    /// device attestation, login) before releasing its stream. The UI layer should
    /// notify the user and open the page in the embedded browser; the task is left
    /// Failed until retried.</summary>
    public event Action<DownloadTask, string>? EmbedInteractionRequired;

    public DownloadEngine()
    {
        _http = CreateClient();
        _meter = new System.Timers.Timer(250);
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
        var inner = new SocketsHttpHandler
        {
            AllowAutoRedirect = false, // manual redirect to block HTTPS→HTTP (BUG-039)
            MaxConnectionsPerServer = 64,
            AutomaticDecompression = DecompressionMethods.All,
            UseCookies = false, // Must be false so custom Cookie headers are sent raw without .NET stripping them
        };
        var client = new HttpClient(new SchemeDowngradeGuard(inner))
        {
            Timeout = TimeSpan.FromSeconds(60),
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/125.0 Safari/537.36");
        client.DefaultRequestHeaders.Accept.ParseAdd("*/*");
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US,en;q=0.9");
        return client;
    }

    /// <summary>Delegating handler that follows HTTP redirects but refuses
    /// HTTPS→HTTP scheme downgrades (BUG-039).  MITM attackers on public Wi-Fi
    /// can forge redirects from https://cdn to http://cdn; this blocks the
    /// download from silently continuing over plaintext.</summary>
    private sealed class SchemeDowngradeGuard : DelegatingHandler
    {
        public SchemeDowngradeGuard(HttpMessageHandler inner) : base(inner) { }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            const int maxRedirects = 20;
            bool originWasBlocked = IsPrivateRedirectTarget(request.RequestUri);
            for (int i = 0; i <= maxRedirects; i++)
            {
                var response = await base.SendAsync(request, cancellationToken);
                if ((int)response.StatusCode is < 300 or >= 400)
                    return response;
                if (response.Headers.Location is null)
                    return response;

                var next = response.Headers.Location;
                if (!next.IsAbsoluteUri)
                    next = new Uri(request.RequestUri!, next);

                if (request.RequestUri!.Scheme == "https" && next.Scheme == "http")
                {
                    response.Dispose();
                    throw new HttpRequestException(
                        $"Blocked HTTPS→HTTP redirect downgrade from {request.RequestUri} to {next} (BUG-039).");
                }

                // A public URL redirecting into loopback/LAN/cloud-metadata space
                // is a classic SSRF channel (302 to 169.254.169.254 etc.).
                // User-initiated LAN downloads (origin already private) still pass.
                if (!originWasBlocked && IsPrivateRedirectTarget(next))
                {
                    response.Dispose();
                    throw new HttpRequestException(
                        $"Blocked redirect from {request.RequestUri} to private target {next}.");
                }

                response.Dispose();
                request.Dispose();
                request = new HttpRequestMessage(HttpMethod.Get, next);
            }
            request.Dispose();
            throw new HttpRequestException($"Too many redirects (>{maxRedirects}).");
        }

        /// <summary>True when a URI points at loopback/private-link space.
        /// Delegates to the capture server's block list so both surfaces agree.</summary>
        private static bool IsPrivateRedirectTarget(Uri? uri)
        {
            if (uri is null)
                return false;
            try { return CaptureServer.IsBlockedResolveTarget(uri.ToString()); }
            catch { return false; }
        }
    }

    public void Start(DownloadTask task)
    {
        bool startNow;
        lock (_lock)
        {
            if (_sessions.ContainsKey(task.Id))
            {
                // Pause->Start race: old session still unwinding. Queue a restart
                // so PumpQueue picks it up when the old session finishes instead
                // of silently dropping the resume.
                if (task.Status == TaskStatus.Paused && !_queue.Contains(task))
                    _queue.Add(task);
                return;
            }
            if (_queue.Contains(task))
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

    public void Resume(DownloadTask task)
    {
        lock (_lock)
        {
            if (task.Status != TaskStatus.Paused)
                return;
            task.Error = null;
            task.Eta = "";
        }
        Start(task);
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
        Session? session;
        lock (_lock) _sessions.TryGetValue(task.Id, out session);
        if (session is not null)
        {
            try { session.Cancel(); } catch { }
            lock (_lock)
            {
                task.Status = TaskStatus.Paused;
                task.SpeedBps = 0;
                task.Eta = "";
            }
            TaskChanged?.Invoke();
            return;
        }
        lock (_lock)
        {
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
        {
            try { session.Cancel(); } catch { }
        }

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
            ReleaseReservedPath(task.FullPath);
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
            // meantime (a new session for the same task would be writing there),
            // and never delete a file that just completed.
            lock (_lock)
            {
                if (_sessions.ContainsKey(task.Id))
                    return;
            }
            if (task.Status == TaskStatus.Completed)
                return;
            TryDelete(session.StatePath);
            TryDelete(task.FullPath);
            session.Dispose();
        });
    }

    public void Remove(DownloadTask task, bool deleteFiles = false)
    {
        RemoveQueued(task);
        ReleaseReservedPath(task.FullPath);

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
            lock (_lock)
            {
                if (_sessions.ContainsKey(task.Id))
                    return;
            }
            if (task.Status == TaskStatus.Completed)
                return;
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
        bool moved;
        lock (_lock)
        {
            int index = _queue.IndexOf(task);
            int target = index + direction;
            if (index < 0 || target < 0 || target >= _queue.Count)
                return;
            (_queue[index], _queue[target]) = (_queue[target], _queue[index]);
            moved = true;
        }
        if (moved)
            TaskChanged?.Invoke();
    }

    private void RemoveQueued(DownloadTask task)
    {
        bool removed;
        lock (_lock) removed = _queue.Remove(task);
        if (removed)
            TaskChanged?.Invoke();
    }

    private void BeginSession(DownloadTask task)
    {
        var session = new Session(task);
        lock (_lock)
        {
            _queue.Remove(task);
            _sessions[task.Id] = session;
        }
        task.Status = TaskStatus.Downloading;
        // Show "working" state immediately: the resolve/probe gap runs after
        // this with no bytes, speed or size yet (see RunSessionAsync).
        task.IsPreparing = true;
        task.PhaseText = task.IsYouTube ? "Preparing video…" : "Resolving stream…";
        task.Eta = "";
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
                .Select((t, i) => (Task: t, Index: i))
                .OrderByDescending(x => x.Task.Priority)
                .ThenBy(x => x.Index)
                .Select(x => x.Task)
                .ToList();
            foreach (var task in ordered)
            {
                if (_sessions.Count + toStart.Count >= _maxConcurrent)
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
            // Embed/player pages (/e/, /embed/) resolve to a direct stream first.
            // A stored SourcePageUrl always re-resolves so expiring signed links
            // (firestream/dood/voe/byse) are refreshed on every start/resume.
            string? pageForResolve = null;
            if (!task.IsYouTube)
            {
                if (!string.IsNullOrWhiteSpace(task.SourcePageUrl))
                    pageForResolve = task.SourcePageUrl;
                else if (StreamHintIs(task, "page"))
                    pageForResolve = task.Url;
                else if (Embed.EmbedResolver.IsEmbedCandidate(task.Url)
                    && !StreamHintIs(task, "HLS", "DASH", "Video"))
                    pageForResolve = task.Url;
            }
            if (pageForResolve is not null)
            {
                task.PhaseText = "Resolving stream…";
                try
                {
                    var embed = await Embed.EmbedResolver.TryResolveAsync(
                        pageForResolve, task.Referer, task.Headers, session.Token);
                    if (embed is not null)
                    {
                        task.SourcePageUrl = embed.SourcePageUrl;
                        task.Url = embed.DirectUrl;
                        if (!string.IsNullOrWhiteSpace(embed.Referer))
                            task.Referer = embed.Referer;
                        foreach (var kv in embed.Headers)
                            task.Headers[kv.Key] = kv.Value;
                        task.Headers.Remove("X-WDM-StreamType");
                        if (!string.IsNullOrWhiteSpace(embed.Title) &&
                            (string.IsNullOrWhiteSpace(task.FileName) ||
                             IsGenericOrPlaceholderName(task.FileName, pageForResolve)))
                        {
                            string ext = embed.IsHls ? ".ts" : ".mp4";
                            task.FileName = ReserveRenamedFile(task,
                                SanitizeFileName(embed.Title + ext, referer: task.Referer));
                        }
                        TaskChanged?.Invoke();
                    }
                }
                catch (Embed.EmbedInteractionRequiredException ex)
                {
                    task.Error = ex.Message + " Open the page in the WDM browser to continue.";
                    task.Status = TaskStatus.Failed;
                    task.IsPreparing = false;
                    task.PhaseText = "";
                    EmbedInteractionRequired?.Invoke(task, pageForResolve);
                    TaskChanged?.Invoke();
                    return;
                }
                catch
                {
                    // Resolver miss: fall through and probe the original URL so a
                    // directly downloadable link still works.
                }
            }

            // Internet title sync for auto-start captures that skipped the dialog:
            // when the name is still generic and no page title was captured, fetch
            // one (oEmbed, then page metadata) into X-WDM-PageTitle. Best-effort,
            // capped at a few seconds, never fails the download.
            try
            {
                if (TaskStore.LoadSettings().EnableTitleSync
                    && string.IsNullOrWhiteSpace(PageTitleHint(task)))
                {
                    string? page = !string.IsNullOrWhiteSpace(task.SourcePageUrl) ? task.SourcePageUrl : task.Referer;
                    if (TitleSync.TitleFetcher.ShouldAttempt(task.FileName, page))
                    {
                        using var titleCts = CancellationTokenSource.CreateLinkedTokenSource(session.Token);
                        titleCts.CancelAfter(TimeSpan.FromSeconds(8));
                        using var titleHttp = TitleSync.TitleFetcher.CreateClient();
                        string? fetched = await TitleSync.TitleFetcher.TryFetchTitleAsync(
                            page, task.Referer, task.Headers, titleHttp, titleCts.Token);
                        if (!string.IsNullOrWhiteSpace(fetched))
                        {
                            task.Headers["X-WDM-PageTitle"] = fetched.Trim();
                            TaskChanged?.Invoke();
                        }
                    }
                }
            }
            catch
            {
                // Title sync is cosmetic; ignore everything here.
            }

            long previousTotalBytes = task.TotalBytes;
            task.PhaseText = "Connecting to server…";
            var meta = await ProbeAsync(task, session.Token);
            task.TotalBytes = meta.TotalBytes;
            session.CurrentUrlIndex = meta.UrlIndex;
            ApplyResumeCapability(task, meta);
            // Auto-upgrade filename if task has no name OR has a generic/un-probed placeholder name (e.g. .bin, download_*)
            if (string.IsNullOrWhiteSpace(task.FileName) || IsGenericOrPlaceholderName(task.FileName, task.Url))
            {
                string? resolved = meta.SuggestedName;
                if (string.IsNullOrWhiteSpace(resolved))
                {
                    string ext = FileNameHelper.ExtensionFromMime(meta.ContentType);
                    if (!string.IsNullOrEmpty(ext))
                    {
                        if (!string.IsNullOrWhiteSpace(task.FileName) &&
                            !task.FileName.StartsWith("download_", StringComparison.OrdinalIgnoreCase) &&
                            !task.FileName.EndsWith(".bin", StringComparison.OrdinalIgnoreCase))
                        {
                            resolved = Path.ChangeExtension(task.FileName, ext);
                        }
                        else
                        {
                            resolved = DeriveName(task.Url, meta.ContentType);
                        }
                    }
                    else
                    {
                        resolved = DeriveName(task.Url, meta.ContentType);
                    }
                }

                if (!string.IsNullOrWhiteSpace(resolved) && !IsGenericOrPlaceholderName(resolved, task.Url))
                {
                    task.FileName = SanitizeFileName(resolved, PageTitleHint(task), task.Referer);
                    task.FileName = ReserveRenamedFile(task, task.FileName);
                }
                else if (string.IsNullOrWhiteSpace(task.FileName))
                {
                    task.FileName = SanitizeFileName(resolved ?? DeriveName(task.Url, meta.ContentType), PageTitleHint(task), task.Referer);
                    task.FileName = ReserveRenamedFile(task, task.FileName);
                }
            }
            Directory.CreateDirectory(task.SaveFolder);

            // Size is known now; transfer workers start next — this is the last
            // preparing step before bytes flow and RefreshSpeeds clears the flag.
            task.PhaseText = meta.IsHls ? "Preparing video…" : "Starting download…";

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
                if (!meta.IsHls && linkRefreshed && IsResuming(session))
                {
                    // A refreshed link bypasses the ETag guard, so an equal-size
                    // different file would resume onto a stale chunk bitmap and
                    // assemble a corrupt hybrid (BUG-032). Drop the bitmap so
                    // every range is re-fetched; delete the file itself only
                    // when the size actually changed.
                    try { if (File.Exists(session.StatePath)) File.Delete(session.StatePath); } catch { }
                    if (previousTotalBytes > 0 && meta.TotalBytes > 0 && previousTotalBytes != meta.TotalBytes)
                    {
                        try { if (File.Exists(task.FullPath)) File.Delete(task.FullPath); } catch { }
                    }
                }
                RecordIdentity(task, meta.Etag, meta.LastModified);
            }

            if (meta.IsHls)
            {
                meta.ProbeBody?.Dispose();
                meta = meta with { ProbeBody = null };
                await RunHlsAsync(session, meta.ContentType);
            }
            else if (meta.IsDash)
            {
                meta.ProbeBody?.Dispose();
                meta = meta with { ProbeBody = null };
                await RunDashAsync(session, meta.ContentType);
            }
            else if (meta.TotalBytes > 0 && meta.SupportsRanges)
            {
                meta.ProbeBody?.Dispose();
                meta = meta with { ProbeBody = null };
                await RunChunkedAsync(session, meta.TotalBytes);
            }
            else
                await RunSingleStreamAsync(session, meta.ProbeBody);

            session.Token.ThrowIfCancellationRequested();
            task.Status = TaskStatus.Completed;
            task.CompletedAt = DateTime.Now;
            task.Progress = 100;
            task.SpeedBps = 0;
            task.Eta = "";
            task.IsPreparing = false;
            task.PhaseText = "";
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
                task.IsPreparing = false;
                task.PhaseText = "";
                TaskCompleted?.Invoke(task);
                return;
            }
            task.Status = TaskStatus.Paused;
            task.IsPreparing = false;
            task.PhaseText = "";
        }
        catch (Exception ex)
        {
            if (session.Removed)
                return;
            if (ex is CloudflareBlockedException)
            {
                // Hand the task to the UI layer's auto-solver instead of dead-ending
                // as Failed. The solver requeues via Engine.Start when it succeeds;
                // a repeat block falls through to the normal failure path.
                task.Status = TaskStatus.Failed;
                task.Error = "Blocked by Cloudflare — opening built-in browser to solve…";
                task.IsPreparing = false;
                task.PhaseText = "";
                CloudflareBlocked?.Invoke(task);
            }
            else
            {
                task.Status = ex is FileChangedException ? TaskStatus.Paused : TaskStatus.Failed;
                task.Error = ex.Message;
                task.IsPreparing = false;
                task.PhaseText = "";
            }
        }
        finally
        {
            // Flush the chunk bitmap so a pause/cancel inside the 1s
            // SaveIfDirty window doesn't re-download a second of work.
            // Completed downloads already deleted the state file — skip them.
            if (task.Status != TaskStatus.Completed)
            {
                try { session.State?.Save(session.StatePath); } catch { }
            }
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
            if (meta.TotalBytes > 0 || !string.IsNullOrWhiteSpace(meta.SuggestedName))
            {
                // A losing mirror's kept-alive probe body would leak its socket.
                fallback?.ProbeBody?.Dispose();
                return meta;
            }
            // Losing probes are never consumed — release any kept body now.
            meta.ProbeBody?.Dispose();
            meta = meta with { ProbeBody = null };
            fallback ??= meta;
            index++;
        }
        return fallback ?? new ProbeMeta(-1, false, null, null, false, false, null, null, null, 0);
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
        catch
        {
            // HEAD unsupported or rejected (e.g. Cloudflare blocks HEAD); fall through to ranged GET probe.
            // A user cancel (Pause/Stop) must still abort immediately, not burn a full probe cycle.
            ct.ThrowIfCancellationRequested();
        }

        // 2) Ranged GET probe (bytes=0-0) - authoritative for size via Content-Range
        //    and proves range support. Sends Accept-Encoding: identity so Content-Length
        //    reflects the real size (a compressed body would corrupt chunk math).
        if (totalBytes <= 0 || !supportsRanges)
        {
            try
            {
                var get = await SendWithRetryAsync(() => BuildRequest(HttpMethod.Get, task, new RangeHeaderValue(0, 0), url), ct);
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
                else if (get.IsSuccessStatusCode)
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
                    suggestedName ??= NameFromDisposition(get.Content.Headers.ContentDisposition);
                    contentType ??= get.Content.Headers.ContentType?.ToString();
                    get.Dispose();
                }
            }
            catch (CloudflareBlockedException) { throw; }
            catch (OperationCanceledException) { throw; }
            catch
            {
                // Server doesn't accept ranged GET requests.
                // User cancel must propagate so Pause during probe is instant.
                ct.ThrowIfCancellationRequested();
            }
        }

        // 3) S3/Google-signed URL: filename may live in response-content-disposition
        //    query param, even when the response omits the header.
        suggestedName ??= FileNameHelper.FileNameFromS3Query(url);

        bool isHls = IsHlsContentType(contentType) || LooksLikeHlsUrl(url) || StreamHintIs(task, "HLS");
        bool isDash = !isHls && (IsDashContentType(contentType) || LooksLikeDashUrl(url) || StreamHintIs(task, "DASH"));
        if (!isHls && !isDash && (StreamHintIs(task, "Stream", "HLS") || LooksLikeHlsUrl(url) || LooksLikeDashUrl(url)))
        {
            // Ambiguous tokenized manifest: confirm via content signature instead of
            // downloading an HTML page as a .bin (e.g. playlist endpoints serving #EXTM3U
            // with a generic content-type).
            if (await SniffHlsContentAsync(task, url, ct))
                isHls = true;
        }
        return new ProbeMeta(totalBytes, supportsRanges, suggestedName, contentType, isHls, isDash, etag, lastModified, probeBody, urlIndex);
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
            return task.DownloadedBytes >= task.TotalBytes;
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
        // Last-Modified is checked whenever the ETags don't positively agree
        // (BUG-032): the old code skipped it whenever the task had an ETag,
        // missing ETag→Last-Modified rotations and servers that stop sending
        // ETags. Matching ETags still short-circuit (same content re-touched).
        bool etagAgrees = !string.IsNullOrWhiteSpace(task.Etag) && !string.IsNullOrWhiteSpace(etag) &&
            string.Equals(task.Etag, etag, StringComparison.Ordinal);
        if (!etagAgrees && !string.IsNullOrWhiteSpace(task.LastModified) &&
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
        string path = uri.AbsolutePath;
        string query = uri.Query;
        if (path.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase)
            || query.Contains(".m3u8", StringComparison.OrdinalIgnoreCase))
            return true;
        // Tokenized manifests often carry no .m3u8 literal (e.g. /playlist, /manifest,
        // /hls/, /master, /stream). Match the same IDM-grade patterns as the extension.
        if (path.Contains("/hls/", StringComparison.OrdinalIgnoreCase)
            || path.Contains("/playlist", StringComparison.OrdinalIgnoreCase)
            || path.Contains("/manifest", StringComparison.OrdinalIgnoreCase)
            || path.Contains("/master", StringComparison.OrdinalIgnoreCase)
            || System.Text.RegularExpressions.Regex.IsMatch(path, @"/stream\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            return true;
        return query.Contains("format=m3u8", StringComparison.OrdinalIgnoreCase)
            || query.Contains("ext=m3u8", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeDashUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return false;
        string path = uri.AbsolutePath;
        string query = uri.Query;
        if (path.EndsWith(".mpd", StringComparison.OrdinalIgnoreCase)
            || query.Contains(".mpd", StringComparison.OrdinalIgnoreCase))
            return true;
        if (path.Contains("/dash/", StringComparison.OrdinalIgnoreCase)
            || path.Contains("/manifest", StringComparison.OrdinalIgnoreCase)
            || path.Contains("/master", StringComparison.OrdinalIgnoreCase))
            return true;
        return query.Contains("format=mpd", StringComparison.OrdinalIgnoreCase)
            || query.Contains("ext=mpd", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Stream classification forwarded by the browser extension
    /// (CaptureServer injects it as X-WDM-StreamType). Lets tokenized manifests
    /// without a literal .m3u8/.mpd route to the right downloader.</summary>
    private static bool StreamHintIs(DownloadTask task, params string[] kinds)
    {
        if (task.Headers.TryGetValue("X-WDM-StreamType", out var hint) && !string.IsNullOrWhiteSpace(hint))
        {
            foreach (var kind in kinds)
            {
                if (hint.Equals(kind, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        return false;
    }

    /// <summary>Content-sniff fallback: fetch the first byte range and check for
    /// an HLS playlist signature. Only used when the URL is stream-ish (or the
    /// extension flagged it) but headers/patterns were inconclusive.</summary>
    private async Task<bool> SniffHlsContentAsync(DownloadTask task, string url, CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(6));
            using var request = BuildRequest(HttpMethod.Get, task, new RangeHeaderValue(0, 1023), url);
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            if (!response.IsSuccessStatusCode && response.StatusCode != HttpStatusCode.PartialContent)
                return false;
            // Cap the sniff read: a server ignoring Range could otherwise make
            // us buffer an entire (multi-GB) file just to check a magic prefix.
            const int sniffCap = 64 * 1024;
            await using var stream = await response.Content.ReadAsStreamAsync(cts.Token);
            var buf = new byte[sniffCap];
            int total = 0, n;
            while (total < sniffCap && (n = await stream.ReadAsync(buf.AsMemory(total, sniffCap - total), cts.Token)) > 0)
                total += n;
            if (total < 7)
                return false;
            string head = System.Text.Encoding.UTF8.GetString(buf, 0, Math.Min(total, 512)).TrimStart('\uFEFF', ' ', '\t', '\r', '\n');
            return head.StartsWith("#EXTM3U", StringComparison.OrdinalIgnoreCase);
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            ct.ThrowIfCancellationRequested();
            return false;
        }
    }

    private static bool IsHlsContentType(string? contentType) =>
        !string.IsNullOrWhiteSpace(contentType)
        && (contentType.Contains("mpegurl", StringComparison.OrdinalIgnoreCase)
            || contentType.Contains("m3u8", StringComparison.OrdinalIgnoreCase));

    private static bool IsDashContentType(string? contentType) =>
        !string.IsNullOrWhiteSpace(contentType)
        && (contentType.Contains("dash+xml", StringComparison.OrdinalIgnoreCase)
            || contentType.Contains("vnd.ms-sstr+xml", StringComparison.OrdinalIgnoreCase));

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
        long chunkCountLong = (totalBytes + chunkSize - 1) / chunkSize;
        if (chunkCountLong < 1 || chunkCountLong > 100_000)
            throw new InvalidOperationException("Server reported an implausible file size.");
        int chunkCount = (int)chunkCountLong;

        session.State = ChunkState.Load(session.StatePath, totalBytes, chunkSize, chunkCount);
        session.ChunkSize = chunkSize;
        session.NextChunk = session.State.GetNextIncomplete(0);
        Interlocked.Exchange(ref session.BytesDownloaded, session.State.CompletedBytes);
        Interlocked.Exchange(ref session.LastBytes, session.State.CompletedBytes);

        await using (var prealloc = new FileStream(task.FullPath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.ReadWrite))
        {
            // Resume-corruption guard: if the partial file was truncated
            // externally (AV cleaner, user edit, disk repair) after chunks were
            // marked complete, the bitmap would skip re-fetching those ranges
            // and assemble a file with zero-filled holes. Completed bytes can
            // never exceed the bytes actually on disk — if they do, the bitmap
            // is stale and the download restarts from scratch.
            long onDisk = prealloc.Length;
            if (onDisk < totalBytes && session.State.CompletedBytes > onDisk)
            {
                session.State = ChunkState.Fresh(session.StatePath, totalBytes, chunkSize, chunkCount);
                session.NextChunk = 0;
                Interlocked.Exchange(ref session.BytesDownloaded, 0);
                Interlocked.Exchange(ref session.LastBytes, 0);
            }
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
            int index;
            lock (session.ClaimLock)
            {
                // Atomically claim the smallest incomplete chunk at or past the
                // cursor so two workers can never take the same index and no
                // incomplete index is skipped.
                index = state.GetNextIncomplete(session.NextChunk);
                if (index >= state.ChunkCount)
                    return;
                session.NextChunk = index + 1;
            }
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
        int rotations = 0;
        int urlCount = 1 + (task.Mirrors?.Count ?? 0);
        while (true)
        {
            long chunkBytes;
            try
            {
                chunkBytes = await DownloadChunkAsync(session, output, from, to);
            }
            catch (Exception ex) when ((IsTransient(ex) || ex is HttpRequestException) &&
                                       !session.Token.IsCancellationRequested)
            {
                if (IsFatalDiskError(ex))
                    throw;
                if (attempt < MaxRetries)
                {
                    await BackoffAsync(attempt, session.Token);
                    attempt++;
                    continue;
                }
                // Retries on the current URL are exhausted; fall over to the next
                // mirror and give the chunk a fresh set of attempts — but only
                // until every URL has been tried, otherwise this loops forever.
                if (session.RotateUrl(task) && rotations + 1 < urlCount)
                {
                    rotations++;
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
        long attemptBytes = 0;
        try
        {
            var response = await SendWithRetryAsync(() => BuildRequest(HttpMethod.Get, task, new RangeHeaderValue(from, to), session.CurrentUrl(task)), session.Token);
            using (response)
            {
                if (IsCloudflareChallenge(response))
                    throw new CloudflareBlockedException(CloudflareMessage(session.CurrentUrl(task)));
                if (response.StatusCode != HttpStatusCode.PartialContent)
                    throw new InvalidOperationException("Server does not support range downloads.");
                var cr = response.Content.Headers.ContentRange;
                if (cr?.From != from || cr?.To != to)
                    throw new HttpRequestException($"Server returned wrong range (asked {from}-{to}, got {cr?.From}-{cr?.To}).");

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
                    attemptBytes += read;
                    Interlocked.Add(ref session.BytesDownloaded, read);
                    session.Token.ThrowIfCancellationRequested();
                }
                if (written < to - from + 1)
                    throw new HttpRequestException($"Chunk incomplete: got {written} of {to - from + 1} bytes.");
                return written;
            }
        }
        catch
        {
            if (attemptBytes > 0)
            {
                Interlocked.Add(ref session.BytesDownloaded, -attemptBytes);
            }
            throw;
        }
    }

    /// <summary>Page title captured by the browser extension (X-WDM-PageTitle).
    /// Used for filename recovery when the manifest URL carries no title.</summary>
    private static string? PageTitleHint(DownloadTask task) =>
        task.Headers.TryGetValue("X-WDM-PageTitle", out var t) && !string.IsNullOrWhiteSpace(t) ? t : null;

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
            task.FileName = ReserveRenamedFile(task, task.FileName);
        }
        // Manifest basenames without title signal ("master.ts") survive when the
        // page title was junk at capture time — retry once with the captured title.
        if (FileNameHelper.IsManifestStem(Path.GetFileNameWithoutExtension(task.FileName)))
        {
            string? hint = PageTitleHint(task);
            if (!string.IsNullOrWhiteSpace(hint))
            {
                string recovered = SanitizeFileName(FileNameHelper.CleanPageTitle(hint) + extension, referer: task.Referer);
                if (!FileNameHelper.IsManifestStem(Path.GetFileNameWithoutExtension(recovered)))
                {
                    task.FileName = ReserveRenamedFile(task, recovered);
                    TaskChanged?.Invoke();
                }
            }
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
            },
            task.Headers);

        session.Token.ThrowIfCancellationRequested();

        // Optional auto-remux of the TS concat into the container chosen in
        // settings (MP4 default, MKV, or KeepTs = off) when ffmpeg is available
        // so the finished file is "My Film.mp4", not "My Film.ts".
        if (task.FileName.EndsWith(".ts", StringComparison.OrdinalIgnoreCase)
            && File.Exists(EngineManager.FfmpegPath)
            && File.Exists(task.FullPath))
        {
            await RemuxHlsAsync(session, task);
        }
    }

    private async Task RemuxHlsAsync(Session session, DownloadTask task)
    {
        HlsContainer container;
        try
        {
            container = TaskStore.LoadSettings().HlsContainer;
        }
        catch
        {
            container = HlsContainer.Mp4;
        }
        if (container == HlsContainer.KeepTs)
            return;
        string targetExt = container == HlsContainer.Mkv ? ".mkv" : ".mp4";
        if (task.FileName.EndsWith(targetExt, StringComparison.OrdinalIgnoreCase))
            return;
        string tsPath = task.FullPath;
        string outPath = Path.ChangeExtension(tsPath, targetExt);
        if (File.Exists(outPath))
        {
            // Unique per completion: date + ms + counter loop so two remuxes
            // in the same millisecond (or a pre-existing collision name)
            // can never silently overwrite each other via ffmpeg -y.
            string stem = Path.GetFileNameWithoutExtension(task.FileName);
            int attempt = 0;
            do
            {
                string suffix = attempt == 0
                    ? $"_{DateTime.Now:yyyyMMdd_HHmmssfff}"
                    : $"_{DateTime.Now:yyyyMMdd_HHmmssfff}_{attempt}";
                outPath = Path.Combine(task.SaveFolder, stem + suffix + targetExt);
                attempt++;
            } while (File.Exists(outPath) && attempt < 1000);
        }
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = EngineManager.FfmpegPath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add("-hide_banner");
            psi.ArgumentList.Add("-loglevel");
            psi.ArgumentList.Add("error");
            psi.ArgumentList.Add("-y");
            psi.ArgumentList.Add("-i");
            psi.ArgumentList.Add(tsPath);
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add("copy");
            if (!targetExt.Equals(".mkv", StringComparison.OrdinalIgnoreCase))
            {
                psi.ArgumentList.Add("-movflags");
                psi.ArgumentList.Add("+faststart");
            }
            psi.ArgumentList.Add(outPath);

            using var proc = Process.Start(psi);
            if (proc is null)
                return;
            using var reg = session.Token.Register(() => { try { proc.Kill(); } catch {} });
            // Drain both redirected streams to avoid pipe-full deadlock on large remuxes.
            var stdoutDrain = proc.StandardOutput.ReadToEndAsync();
            var stderrDrain = proc.StandardError.ReadToEndAsync();
            await proc.WaitForExitAsync(session.Token);
            try { await Task.WhenAll(stdoutDrain, stderrDrain); } catch { }
            if (proc.ExitCode == 0 && File.Exists(outPath) && new FileInfo(outPath).Length > 0)
            {
                try { File.Delete(tsPath); } catch { }
                task.FileName = ReserveRenamedFile(task, Path.GetFileName(outPath));
                task.TotalBytes = new FileInfo(outPath).Length;
                Interlocked.Exchange(ref session.BytesDownloaded, task.TotalBytes);
                Interlocked.Exchange(ref session.LastBytes, task.TotalBytes);
                TaskChanged?.Invoke();
            }
            else
            {
                try { if (File.Exists(outPath)) File.Delete(outPath); } catch { }
            }
        }
        catch
        {
            // Remux is best-effort; the .ts output remains fully playable.
        }
    }

    private async Task RunDashAsync(Session session, string? contentType)
    {
        var task = session.Task;

        // DASH manifests (.mpd) can be downloaded and remuxed into .mp4 using ffmpeg/yt-dlp
        string extension = ".mp4";
        if (task.FileName.EndsWith(".mpd", StringComparison.OrdinalIgnoreCase))
        {
            task.FileName = Path.ChangeExtension(task.FileName, extension);
            task.FileName = ReserveRenamedFile(task, task.FileName);
        }

        // If ffmpeg is available, we stream and mux via ffmpeg directly
        if (File.Exists(EngineManager.FfmpegPath))
        {
            var psi = new ProcessStartInfo
            {
                FileName = EngineManager.FfmpegPath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            if (!string.IsNullOrWhiteSpace(task.Referer))
            {
                psi.ArgumentList.Add("-headers");
                psi.ArgumentList.Add($"Referer: {task.Referer}\r\nUser-Agent: Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/125.0 Safari/537.36\r\n");
            }

            psi.ArgumentList.Add("-y");
            psi.ArgumentList.Add("-i");
            psi.ArgumentList.Add(task.Url);
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add("copy");
            psi.ArgumentList.Add(task.FullPath);

            using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Failed to launch ffmpeg for DASH stream.");
            using var reg = session.Token.Register(() => { try { proc.Kill(); } catch {} });

            // Drain stdout (moov/header chatter) in the background so it can't
            // fill the pipe while we read progress from stderr.
            var stdoutDrain = Task.Run(async () =>
            {
                try
                {
                    var buf = new byte[81920];
                    while (await proc.StandardOutput.BaseStream.ReadAsync(buf, session.Token) > 0) { }
                }
                catch { }
            }, session.Token);
            string? errLine;
            while ((errLine = await proc.StandardError.ReadLineAsync(session.Token)) != null)
            {
                // Inspect progress from ffmpeg stderr
                if (errLine.Contains("size=", StringComparison.OrdinalIgnoreCase) && File.Exists(task.FullPath))
                {
                    long curSize = new FileInfo(task.FullPath).Length;
                    Interlocked.Exchange(ref session.BytesDownloaded, curSize);
                }
            }

            await proc.WaitForExitAsync(session.Token);
            try { await stdoutDrain; } catch { }
            if (proc.ExitCode != 0)
                throw new InvalidOperationException($"ffmpeg exited with code {proc.ExitCode} while capturing DASH stream.");
        }
        else
        {
            throw new InvalidOperationException("ffmpeg is required to capture DASH streams (.mpd). Please install ffmpeg.");
        }

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
        int rotations = 0;
        int urlCount = 1 + (task.Mirrors?.Count ?? 0);
        while (true)
        {
            try
            {
                await RunSingleStreamAttemptAsync(session);
                return;
            }
            catch (Exception ex) when (IsTransient(ex) && !session.Token.IsCancellationRequested)
            {
                if (IsFatalDiskError(ex))
                    throw;
                if (attempt < MaxRetries)
                {
                    await BackoffAsync(attempt, session.Token);
                    attempt++;
                }
                else if (session.RotateUrl(task) && rotations + 1 < urlCount)
                {
                    rotations++;
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

        if (IsGenericOrPlaceholderName(task.FileName, task.Url))
        {
            string? dispositionName = NameFromDisposition(response.Content.Headers.ContentDisposition);
            if (!string.IsNullOrWhiteSpace(dispositionName))
            {
                string sanitized = SanitizeFileName(dispositionName, referer: task.Referer);
                if (!string.Equals(task.FileName, sanitized, StringComparison.OrdinalIgnoreCase))
                {
                    task.FileName = ReserveRenamedFile(task, sanitized);
                }
            }
        }

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
            // is already fully downloaded. Treat that as success — but only when
            // the on-disk size exactly matches the server's length. Otherwise the
            // server shrank the file and we'd mark a corrupt over-long file done.
            if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable && existingLength > 0)
            {
                if (response.Content.Headers.ContentRange?.Length is long len && len > 0)
                {
                    if (existingLength != len)
                        throw new FileChangedException($"The file size changed on the server (local {existingLength} vs remote {len}). Paused to avoid a corrupt file.");
                    task.TotalBytes = len;
                }
                else if (task.TotalBytes > 0 && existingLength != task.TotalBytes)
                {
                    throw new FileChangedException("The server rejected the resume range and the local size does not match. Paused to avoid a corrupt file.");
                }
                Interlocked.Exchange(ref session.BytesDownloaded, existingLength);
                Interlocked.Exchange(ref session.LastBytes, existingLength);
                return;
            }

            response.EnsureSuccessStatusCode();

            if (existingLength == 0 && IsGenericOrPlaceholderName(task.FileName, task.Url))
            {
                string? dispositionName = NameFromDisposition(response.Content.Headers.ContentDisposition);
                if (!string.IsNullOrWhiteSpace(dispositionName))
                {
                    string sanitized = SanitizeFileName(dispositionName, referer: task.Referer);
                    if (!string.Equals(task.FileName, sanitized, StringComparison.OrdinalIgnoreCase))
                    {
                        task.FileName = ReserveRenamedFile(task, sanitized);
                    }
                }
            }

            bool isPartial = response.StatusCode == HttpStatusCode.PartialContent;
            if (!isPartial)
            {
                existingLength = 0;
            }
            else
            {
                // Server honored our resume offset — verify it started where asked.
                var cr = response.Content.Headers.ContentRange;
                if (cr?.From is long gotFrom && gotFrom != existingLength)
                    throw new HttpRequestException($"Server resumed at wrong offset (asked {existingLength}, got {gotFrom}).");
            }

            if (response.Content.Headers.ContentLength is long length && length > 0)
            {
                if (isPartial)
                    task.TotalBytes = existingLength + length;
                else
                    task.TotalBytes = length;
            }

            Interlocked.Exchange(ref session.BytesDownloaded, existingLength);
            Interlocked.Exchange(ref session.LastBytes, existingLength);

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
            long last = Interlocked.Read(ref session.LastBytes);
            double speed = Math.Max(0, (now - last) * 4.0);
            Interlocked.Exchange(ref session.LastBytes, now);
            session.Task.SpeedBps = speed;
            session.Task.DownloadedBytes = now;
            total += (long)speed;

            // First real bytes flowing: leave the preparing state so the dialog
            // swaps the status line + marquee for live speed/ETA/percent.
            if (session.Task.IsPreparing && speed > 1)
            {
                session.Task.IsPreparing = false;
                session.Task.PhaseText = "";
            }

            if (session.Task.TotalBytes > 0)
            {
                double percent = (double)now * 100.0 / session.Task.TotalBytes;
                session.Task.Progress = Math.Clamp((int)percent, 0, 100);
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
                TimeSpan? retryAfter = GetRetryAfter(response);
                response.Dispose();
                if (retryAfter is not null)
                {
                    // Honor the server's Retry-After (429/503 flood protection),
                    // capped so a malicious date can't park a worker forever.
                    try { await Task.Delay(retryAfter.Value, ct); } catch (OperationCanceledException) { throw; }
                }
                else
                    await BackoffAsync(attempt, ct);
                attempt++;
                continue;
            }
            return response;
        }
    }

    private static bool IsTransient(Exception ex) =>
        ex is HttpRequestException or IOException or TaskCanceledException;

    /// <summary>Parses Retry-After (delta-seconds or HTTP-date), capped at 30s.</summary>
    internal static TimeSpan? GetRetryAfter(HttpResponseMessage response)
    {
        try
        {
            if (response.Headers.RetryAfter is { } ra)
            {
                if (ra.Delta is TimeSpan d)
                    return d < TimeSpan.Zero ? TimeSpan.Zero : (d > TimeSpan.FromSeconds(30) ? TimeSpan.FromSeconds(30) : d);
                if (ra.Date is DateTimeOffset date)
                {
                    var wait = date - DateTimeOffset.UtcNow;
                    if (wait < TimeSpan.Zero) return TimeSpan.Zero;
                    return wait > TimeSpan.FromSeconds(30) ? TimeSpan.FromSeconds(30) : wait;
                }
            }
        }
        catch { }
        return null;
    }

    /// <summary>Local disk failures that no retry or mirror rotation will ever
    /// fix: full disk, ACL denial, over-long path (BUG-025).</summary>
    internal static bool IsFatalDiskError(Exception ex)
    {
        if (ex is UnauthorizedAccessException or PathTooLongException)
            return true;
        if (ex is IOException io)
        {
            // 0x80070070 ERROR_DISK_FULL, 0x80070027 drive full (FAT), 0x80070070 variants.
            int code = io.HResult & 0xFFFF;
            if (code is 0x70 or 0x27)
                return true;
        }
        return false;
    }

    private static async Task BackoffAsync(int attempt, CancellationToken ct)
    {
        // Full-jitter exponential backoff (BUG-026): deterministic 500*2^n
        // herds every chunk worker/mirror into synchronized retry storms.
        int cap = (int)Math.Min(8000, 500 * Math.Pow(2, attempt));
        int ms = Random.Shared.Next(cap / 2, cap + 1);
        await Task.Delay(ms, ct);
    }

    private static HttpRequestMessage BuildRequest(HttpMethod method, DownloadTask task, RangeHeaderValue? range, string? url = null)
    {
        string targetUrl = url ?? task.Url;
        var request = new HttpRequestMessage(method, targetUrl);
        if (range is not null)
            request.Headers.Range = range;
        if (!string.IsNullOrWhiteSpace(task.Referer) && Uri.TryCreate(task.Referer, UriKind.Absolute, out var referer))
            request.Headers.Referrer = referer;
        bool sameHost = IsSameHost(targetUrl, task.Url);
        // Apply per-task custom headers (e.g. Cookie, Authorization, Referer).
        foreach (var kv in task.Headers)
        {
            if (string.IsNullOrWhiteSpace(kv.Key) || string.IsNullOrWhiteSpace(kv.Value))
                continue;
            // Internal routing hints (e.g. X-WDM-StreamType) must never leave the client.
            if (kv.Key.StartsWith("X-WDM-", StringComparison.OrdinalIgnoreCase))
                continue;
            // Session credentials belong to the original host — never forward
            // them to a mirror CDN on a different host.
            if (!sameHost && (kv.Key.Equals("Cookie", StringComparison.OrdinalIgnoreCase) ||
                              kv.Key.Equals("Authorization", StringComparison.OrdinalIgnoreCase) ||
                              kv.Key.Equals("Proxy-Authorization", StringComparison.OrdinalIgnoreCase)))
                continue;
            request.Headers.TryAddWithoutValidation(kv.Key, kv.Value);
        }
        // Standard Chrome browser headers reduce Cloudflare/bot-filter false positives (testfile.org etc.)
        request.Headers.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,image/apng,*/*;q=0.8");
        request.Headers.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9");
        request.Headers.TryAddWithoutValidation("Sec-Fetch-Dest", "document");
        request.Headers.TryAddWithoutValidation("Sec-Fetch-Mode", "navigate");
        request.Headers.TryAddWithoutValidation("Sec-Fetch-Site", "cross-site");
        request.Headers.TryAddWithoutValidation("Sec-Fetch-User", "?1");
        request.Headers.TryAddWithoutValidation("Upgrade-Insecure-Requests", "1");
        if (string.IsNullOrWhiteSpace(task.Referer) && Uri.TryCreate(url ?? task.Url, UriKind.Absolute, out var targetUri))
        {
            // ponytail: label-count heuristic, not a real registrable-domain table
            // (no PSL); wrong for multi-part TLDs like co.uk — fine for a Referer hint.
            string[] labels = targetUri.Host.Split('.');
            string host = labels.Length >= 3 ? string.Join('.', labels.AsSpan(1).ToArray()) : targetUri.Host;
            request.Headers.TryAddWithoutValidation("Referer", $"{targetUri.Scheme}://{host}/");
        }
        return request;
    }

    private static bool IsSameHost(string a, string b)
    {
        try
        {
            // Fail closed: an unparseable URL must never be treated as
            // same-host, or session credentials (Cookie/Authorization)
            // would be forwarded to an attacker-controlled mirror.
            if (!Uri.TryCreate(a, UriKind.Absolute, out var ua) ||
                !Uri.TryCreate(b, UriKind.Absolute, out var ub))
                return false;
            return string.Equals(ua.Host, ub.Host, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
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
            string raw = Path.GetFileName(uri.AbsolutePath);
            string name;
            try { name = Uri.UnescapeDataString(raw); }
            catch { name = raw; }
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

    /// <summary>
    /// Checks whether a filename is an un-inspected generic fallback, placeholder, or extensionless temporary name
    /// (e.g. download_2026-09-01_..., download.bin, uc.bin, or ending in .bin / .tmp) that should be upgraded
    /// when the server announces the authoritative Content-Disposition or MIME Content-Type.
    /// </summary>
    public static bool IsGenericOrPlaceholderName(string? fileName, string? url = null)
    {
        if (string.IsNullOrWhiteSpace(fileName))
            return true;

        string name = fileName.Trim();
        string ext = Path.GetExtension(name);

        // 1. Files ending in .bin, .tmp, or missing extension completely
        if (string.IsNullOrEmpty(ext) ||
            ext.Equals(".bin", StringComparison.OrdinalIgnoreCase) ||
            ext.Equals(".tmp", StringComparison.OrdinalIgnoreCase))
            return true;

        // 2. Generic timestamp/fallback names e.g. download_2026-09-01_..., download_20260901..., uc.bin
        if (name.StartsWith("download_", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("download.", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("file_", StringComparison.OrdinalIgnoreCase))
            return true;

        // 3. If it matches the raw fallback name derived from an extensionless URL
        if (!string.IsNullOrWhiteSpace(url))
        {
            string fallback = DeriveName(url);
            if (string.Equals(name, fallback, StringComparison.OrdinalIgnoreCase) &&
                (fallback.EndsWith(".bin", StringComparison.OrdinalIgnoreCase) || fallback.StartsWith("download_", StringComparison.OrdinalIgnoreCase)))
                return true;
        }

        return false;
    }

    /// <summary>True for files yt-dlp could plausibly have just produced:
    /// media/subtitle/thumbnail outputs, never engine sidecars or temp files.</summary>
    internal static bool IsYtDlpResultCandidate(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
            return false;
        string lower = fileName.ToLowerInvariant();
        if (lower.EndsWith(".wdmstate", StringComparison.Ordinal) ||
            lower.EndsWith(".part", StringComparison.Ordinal) ||
            lower.EndsWith(".tmp", StringComparison.Ordinal) ||
            lower.EndsWith(".ytdl", StringComparison.Ordinal) ||
            lower.StartsWith(".wdmseg_", StringComparison.Ordinal))
            return false;
        string ext = Path.GetExtension(lower);
        // Compound extensions: "foo.info.json" → Path.GetExtension returns ".json",
        // not ".info.json"; test the full trailing segment instead (BUG-027).
        // Also accept bare "description" (no extension) — yt-dlp emits that as a
        // sidecar text file alongside video downloads.
        if (lower.EndsWith(".info.json", StringComparison.Ordinal) ||
            lower.Equals("description", StringComparison.Ordinal))
            return true;
        return ext is ".mp4" or ".mkv" or ".webm" or ".avi" or ".mov" or ".flv" or ".m4v" or ".ts" or ".m3u8" or ".mpd"
            or ".mp3" or ".m4a" or ".opus" or ".ogg" or ".wav" or ".flac" or ".aac" or ".wma"
            or ".vtt" or ".srt" or ".ass" or ".lrc" or ".jpg" or ".jpeg" or ".png" or ".webp";
    }

    private static string FallbackName() =>
        $"download_{DateTime.Now:yyyy-MM-dd_HHmmss}.bin";

    public static string SanitizeFileName(string name, string? pageTitle = null, string? referer = null)
    {
        string cleaned = FileNameHelper.SmartSanitizeFileName(name, pageTitle, referer);
        return string.IsNullOrWhiteSpace(cleaned) ? $"download_{DateTime.Now:yyyyMMddHHmmss}.bin" : cleaned;
    }

    private static bool IsVideoFile(string name)
    {
        string ext = Path.GetExtension(name).ToLowerInvariant();
        return ext switch
        {
            ".mp4" or ".mkv" or ".avi" or ".mov" or ".webm" or ".ts" or ".flv" or ".m4v" => true,
            _ => false
        };
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

    private void ReleaseReservedPath(string fullPath)
    {
        lock (_lock) _reservedPaths.Remove(fullPath);
    }

    private string ReserveRenamedFile(DownloadTask task, string newFileName)
    {
        string oldFull = task.FullPath;
        string reserved = EnsureUniqueName(task.SaveFolder, newFileName, task.Id);
        if (!string.Equals(oldFull, Path.Combine(task.SaveFolder, reserved), StringComparison.OrdinalIgnoreCase))
            ReleaseReservedPath(oldFull);
        return reserved;
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
        bool IsDash,
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
        else if (meta.IsDash)
            task.ResumeCapabilityText = "No — DASH stream";
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
        public readonly object ClaimLock = new();
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

        public void Cancel()
        {
            try { Cts.Cancel(); } catch (ObjectDisposedException) { }
        }

        public void Finish()
        {
            lock (ClaimLock)
            {
                Done = true;
                try { Cts.Cancel(); } catch (ObjectDisposedException) { }
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
                lock (_lock)
                {
                    long completed = _completed;
                    if (completed <= 0)
                        return 0;
                    // The final chunk is usually smaller than _chunkSize, so count it by its real length.
                    long lastChunkSize = _totalBytes - ((long)_chunkCount - 1) * _chunkSize;
                    if (lastChunkSize <= 0 || lastChunkSize > _chunkSize)
                        lastChunkSize = _chunkSize;

                    bool lastCompleted = (_bits[(_chunkCount - 1) >> 3] & (1 << ((_chunkCount - 1) & 7))) != 0;
                    if (lastCompleted)
                        return ((long)completed - 1) * _chunkSize + lastChunkSize;

                    return (long)completed * _chunkSize;
                }
            }
        }
        public int Completed { get { lock (_lock) return CountBits(); } }

        public int GetNextIncomplete(int fromIndex)
        {
            lock (_lock)
            {
                if (fromIndex >= _chunkCount)
                    return _chunkCount;

                int byteIndex = fromIndex >> 3;
                int bitIndex = fromIndex & 7;

                while (byteIndex < _bits.Length)
                {
                    byte b = _bits[byteIndex];
                    if (b != 0xFF)
                    {
                        for (int bit = bitIndex; bit < 8; bit++)
                        {
                            int chunk = (byteIndex << 3) + bit;
                            if (chunk >= _chunkCount)
                                return _chunkCount;
                            if ((b & (1 << bit)) == 0)
                                return chunk;
                        }
                    }
                    byteIndex++;
                    bitIndex = 0;
                }
                return _chunkCount;
            }
        }

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

        /// <summary>Discard any persisted bitmap and start over (used when the
        /// on-disk partial file no longer matches the recorded progress).</summary>
        public static ChunkState Fresh(string path, long totalBytes, long chunkSize, int chunkCount)
        {
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
        // YouTube tasks must not show HTTP-specific probe text; set immediately so the
        // progress window never flashes "Checking server support..." (see screenshot).
        task.ResumeCapabilityText = "YouTube — via yt-dlp (single stream)";
        task.IsResumable = false;
        task.IsPreparing = true;
        task.PhaseText = "Preparing video…";
        task.Eta = "";
        TaskChanged?.Invoke();

        try
        {
            Directory.CreateDirectory(task.SaveFolder);
            var outFile = (string.IsNullOrWhiteSpace(task.FileName) || task.FileName == "YouTube Video" || task.FileName.StartsWith("download_") || task.FileName == "watch")
                ? Path.Combine(task.SaveFolder, "%(title)s.%(ext)s")
                : task.FullPath;

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
                task.IsPreparing = false;
                task.PhaseText = "";
            }
            else if (proc.ExitCode == 0)
            {
                long fileLen = 0;
                try
                {
                    if (!outFile.Contains("%(") && File.Exists(outFile))
                        fileLen = new FileInfo(outFile).Length;
                    else if (!string.IsNullOrWhiteSpace(task.FullPath) && File.Exists(task.FullPath))
                        fileLen = new FileInfo(task.FullPath).Length;
                    else if (outFile.Contains("%("))
                    {
                        // Template case: parse final FileName was set to merged name; try FullPath again
                        // and if still missing, pick newest file in SaveFolder matching title pattern.
                        try
                        {
                            var dir = task.SaveFolder;
                            if (Directory.Exists(dir))
                            {
                                // Only real media outputs: never .wdmstate/.part
                                // sidecars or a sibling download's file (BUG-027).
                                // Exclude files owned by other active sessions so
                                // two concurrent template downloads to the same
                                // folder can never claim each other's output.
                                HashSet<string> owned;
                                lock (_lock)
                                {
                                    owned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                                    foreach (var kv in _sessions)
                                    {
                                        if (kv.Key == task.Id)
                                            continue;
                                        try
                                        {
                                            if (!string.IsNullOrWhiteSpace(kv.Value.Task.FullPath))
                                                owned.Add(Path.GetFileName(kv.Value.Task.FullPath));
                                        }
                                        catch { }
                                    }
                                }
                                var newest = new DirectoryInfo(dir).GetFiles()
                                    .Where(f => IsYtDlpResultCandidate(f.Name) && !owned.Contains(f.Name) && f.Length > 0)
                                    .OrderByDescending(f => f.LastWriteTimeUtc)
                                    .FirstOrDefault();
                                if (newest != null && (DateTime.UtcNow - newest.LastWriteTimeUtc).TotalMinutes < 5)
                                {
                                    fileLen = newest.Length;
                                    task.FileName = ReserveRenamedFile(task, newest.Name);
                                }
                            }
                        }
                        catch { }
                    }
                }
                catch { }

                task.DownloadedBytes = task.TotalBytes > 0 ? task.TotalBytes : fileLen;
                if (task.TotalBytes <= 0) task.TotalBytes = task.DownloadedBytes;
                task.Progress = 100;
                task.Status = TaskStatus.Completed;
                task.CompletedAt = DateTime.Now;
                task.SpeedBps = 0;
                task.Eta = "";
                task.IsPreparing = false;
                task.PhaseText = "";
                TaskCompleted?.Invoke(task);
            }
            else
            {
                task.Status = TaskStatus.Failed;
                task.Error = "yt-dlp exited with error code " + proc.ExitCode;
                task.SpeedBps = 0;
                task.Eta = "";
                task.IsPreparing = false;
                task.PhaseText = "";
            }
        }
        catch (OperationCanceledException)
        {
            task.Status = TaskStatus.Paused;
            task.SpeedBps = 0;
            task.Eta = "";
            task.IsPreparing = false;
            task.PhaseText = "";
        }
        catch (Exception ex)
        {
            task.Status = TaskStatus.Failed;
            task.Error = ex.Message;
            task.SpeedBps = 0;
            task.Eta = "";
            task.IsPreparing = false;
            task.PhaseText = "";
        }
        finally
        {
            session.Finish();
            lock (_lock)
            {
                if (_sessions.TryGetValue(task.Id, out var current) &&
                    ReferenceEquals(current, session))
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

    private static void ParseYtDlpOutputLine(string line, DownloadTask task)
    {
        // Any yt-dlp output means the process is alive and working — leave the
        // preparing state so the dialog swaps the status line for live stats.
        if (task.IsPreparing && (line.StartsWith("[download]", StringComparison.OrdinalIgnoreCase)
            || line.StartsWith("[Merger]", StringComparison.OrdinalIgnoreCase)
            || line.StartsWith("[ExtractAudio]", StringComparison.OrdinalIgnoreCase)))
        {
            task.IsPreparing = false;
            task.PhaseText = "";
        }
        if (line.StartsWith("[download] Destination: "))
        {
            var dest = line.Substring("[download] Destination: ".Length).Trim();
            var name = Path.GetFileName(dest);
            if (!string.IsNullOrWhiteSpace(name))
            {
                // yt-dlp creates temp files like "Title.mp4.f616.mp4" or "Title.f140.m4a"
                // during separate stream downloads. Don't overwrite the user-visible
                // FileName with the temp format suffix; wait for the Merger line which
                // carries the final merged name. Single-stream downloads have no
                // intermediate suffix and will update here correctly.
                bool isTempFormat = System.Text.RegularExpressions.Regex.IsMatch(name, @"\.f\d+\.", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (!isTempFormat)
                {
                    task.FileName = name;
                }
                else if (string.IsNullOrWhiteSpace(task.FileName) || task.FileName == "YouTube Video" || task.FileName.StartsWith("download_", StringComparison.OrdinalIgnoreCase))
                {
                    // Fallback if we have no meaningful name yet: strip the temp part.
                    var cleaned = System.Text.RegularExpressions.Regex.Replace(name, @"\.f\d+(\.[a-z0-9]+)$", "$1", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    if (!string.IsNullOrWhiteSpace(cleaned))
                        task.FileName = cleaned;
                }
            }
        }
        else if (line.StartsWith("[Merger] Merging formats into \""))
        {
            var end = line.LastIndexOf('"');
            if (end > 31)
            {
                var dest = line.Substring(31, end - 31);
                var name = Path.GetFileName(dest);
                if (!string.IsNullOrWhiteSpace(name))
                {
                    task.FileName = name;
                }
            }
        }
        else if (line.StartsWith("[ExtractAudio] Destination: "))
        {
            var dest = line.Substring("[ExtractAudio] Destination: ".Length).Trim();
            var name = Path.GetFileName(dest);
            if (!string.IsNullOrWhiteSpace(name))
                task.FileName = name;
        }

        // yt-dlp progress lines: "[download]   5.1% of ~  5.20MiB at  123.45KiB/s ETA 00:36"
        // Tolerant regex handles the optional "~" (approximate size) and "Unknown" placeholders.
        if (line.StartsWith("[download]") && line.Contains('%'))
        {
            try
            {
                var pctMatch = System.Text.RegularExpressions.Regex.Match(line, @"(\d+(?:\.\d+)?)\s*%");
                if (pctMatch.Success && double.TryParse(pctMatch.Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double pct))
                {
                    pct = Math.Clamp(pct, 0, 100);
                    task.Progress = (int)Math.Round(pct);
                }

                var sizeMatch = System.Text.RegularExpressions.Regex.Match(line, @"of\s+~?\s*(\d+(?:\.\d+)?\s*[KMGT]?i?B)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (sizeMatch.Success)
                {
                    long bytes = ParseSizeToBytes(sizeMatch.Groups[1].Value.Replace(" ", ""));
                    if (bytes > 0)
                        task.TotalBytes = bytes;
                }

                var speedMatch = System.Text.RegularExpressions.Regex.Match(line, @"at\s+(\d+(?:\.\d+)?\s*[KMGT]?i?B/s)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (speedMatch.Success)
                {
                    long bps = ParseSpeedToBps(speedMatch.Groups[1].Value.Replace(" ", ""));
                    if (bps > 0)
                        task.SpeedBps = bps;
                    else if (line.Contains("Unknown speed", StringComparison.OrdinalIgnoreCase))
                        task.SpeedBps = 0;
                }
                else if (line.Contains("Unknown speed", StringComparison.OrdinalIgnoreCase))
                {
                    task.SpeedBps = 0;
                }

                var etaMatch = System.Text.RegularExpressions.Regex.Match(line, @"ETA\s+(\S+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (etaMatch.Success)
                {
                    var etaRaw = etaMatch.Groups[1].Value.Trim();
                    if (etaRaw.Equals("Unknown", StringComparison.OrdinalIgnoreCase) || etaRaw.Equals("UnknownETA", StringComparison.OrdinalIgnoreCase) || etaRaw.Equals("--", StringComparison.OrdinalIgnoreCase))
                        task.Eta = "";
                    else
                        task.Eta = etaRaw;
                }

                // Keep DownloadedBytes in sync when total is known; progress bar itself
                // is driven by task.Progress above so it moves even when size is "~".
                if (task.TotalBytes > 0 && pctMatch.Success && double.TryParse(pctMatch.Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double p2))
                {
                    task.DownloadedBytes = (long)(task.TotalBytes * (p2 / 100.0));
                }
                else if (pctMatch.Success && task.TotalBytes <= 0)
                {
                    // Best-effort: derive DownloadedBytes from file on disk if total unknown.
                    try
                    {
                        var full = task.FullPath;
                        if (!string.IsNullOrWhiteSpace(full) && File.Exists(full))
                            task.DownloadedBytes = new FileInfo(full).Length;
                    }
                    catch { }
                }
            }
            catch
            {
                // ignore parsing errors
            }
        }

        // Merge / post-processing phase markers keep the UI from stalling at 100% immediately.
        if (line.StartsWith("[Merger]") || line.StartsWith("[ExtractAudio]") || line.Contains("has already been downloaded", StringComparison.OrdinalIgnoreCase))
        {
            if (task.Progress < 100)
            {
                // Don't jump to 100 here; RunYouTubeSessionAsync sets Completed on exit.
                // Nudge to 99 so the user sees "Merging..." feedback.
                if (task.Progress < 99)
                    task.Progress = 99;
            }
            task.Eta = "";
        }
        if (line.Contains("[download] 100%"))
        {
            task.Eta = "";
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
