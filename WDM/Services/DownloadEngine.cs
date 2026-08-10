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
    private readonly List<DownloadTask> _scheduled = new();
    private readonly HashSet<DownloadTask> _windowHeld = new();
    private readonly System.Timers.Timer _meter;
    private readonly System.Timers.Timer _scheduleTimer;
    private readonly SpeedGovernor _governor = new();
    private long _totalSpeedBps;
    private int _maxConcurrent = 3;
    private int _maxRetries = 3;
    private long _baseLimitKbps;
    private bool _throttleEnabled;
    private string _throttleStart = "09:00";
    private string _throttleEnd = "17:00";
    private long _throttleLimitKbps;
    private bool _windowEnabled;
    private string _windowStart = "01:00";
    private string _windowEnd = "06:00";
    private bool _windowOpen = true;

    public event Action? TaskChanged;
    public event Action<DownloadTask>? TaskCompleted;

    public DownloadEngine()
    {
        _http = CreateClient();
        _meter = new System.Timers.Timer(500);
        _meter.AutoReset = true;
        _meter.Elapsed += (_, _) => RefreshSpeeds();
        _scheduleTimer = new System.Timers.Timer(30_000);
        _scheduleTimer.AutoReset = true;
        _scheduleTimer.Elapsed += (_, _) => EvaluateSchedule();
        _scheduleTimer.Start();
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

    public bool ThrottleEnabled
    {
        get { lock (_lock) return _throttleEnabled; }
        set { lock (_lock) _throttleEnabled = value; ApplySpeedLimit(); }
    }
    public string ThrottleStart { get { lock (_lock) return _throttleStart; } set { lock (_lock) _throttleStart = value; ApplySpeedLimit(); } }
    public string ThrottleEnd { get { lock (_lock) return _throttleEnd; } set { lock (_lock) _throttleEnd = value; ApplySpeedLimit(); } }
    public long ThrottleLimitKbps
    {
        get { lock (_lock) return _throttleLimitKbps; }
        set { lock (_lock) _throttleLimitKbps = Math.Max(0, value); ApplySpeedLimit(); }
    }

    public bool DownloadWindowEnabled
    {
        get { lock (_lock) return _windowEnabled; }
        set
        {
            lock (_lock) _windowEnabled = value;
            EvaluateSchedule();
        }
    }
    public string WindowStart { get { lock (_lock) return _windowStart; } set { lock (_lock) _windowStart = value; EvaluateSchedule(); } }
    public string WindowEnd { get { lock (_lock) return _windowEnd; } set { lock (_lock) _windowEnd = value; EvaluateSchedule(); } }

    public int ActiveCount
    {
        get { lock (_lock) return _sessions.Count; }
    }

    public int QueuedCount
    {
        get { lock (_lock) return _queue.Count; }
    }

    public long TotalSpeedBps => Interlocked.Read(ref _totalSpeedBps);

    public bool IsDownloadWindowOpen => IsWindowOpen();

    private static HttpClient CreateClient()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            MaxConnectionsPerServer = 64,
            AutomaticDecompression = DecompressionMethods.None,
        };
        var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(60),
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/125.0 Safari/537.36");
        return client;
    }

    public void Start(DownloadTask task)
    {
        bool startNow;
        lock (_lock)
        {
            if (_sessions.ContainsKey(task.Id) || _queue.Contains(task) || _scheduled.Contains(task))
                return;

            if (task.ScheduledStart is DateTime s && s > DateTime.Now)
            {
                task.Status = TaskStatus.Scheduled;
                _scheduled.Add(task);
                TaskChanged?.Invoke();
                return;
            }

            if (!IsWindowOpen())
            {
                task.Status = TaskStatus.Paused;
                _windowHeld.Add(task);
                TaskChanged?.Invoke();
                return;
            }

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

    public void Pause(DownloadTask task)
    {
        Session? session;
        lock (_lock) _sessions.TryGetValue(task.Id, out session);
        session?.Cancel();
    }

    public void PauseAll()
    {
        Session[] snapshot;
        lock (_lock) snapshot = _sessions.Values.ToArray();
        foreach (var session in snapshot)
            session.Cancel();
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
            _scheduled.Remove(task);
            _windowHeld.Remove(task);
            task.ScheduledStart = null;
            task.Status = TaskStatus.Paused;
            task.Error = "Stopped";
            TaskChanged?.Invoke();
            return;
        }

        session.Cancel();
        session.Removed = true;
        lock (_lock) _sessions.Remove(task.Id);
        TaskChanged?.Invoke();

        _ = Task.Run(async () =>
        {
            await Task.WhenAll(session.Chunks);
            TryDelete(session.StatePath);
            TryDelete(task.FullPath);
            task.ScheduledStart = null;
            task.Status = TaskStatus.Paused;
            task.Error = "Stopped";
            TaskChanged?.Invoke();
            session.Dispose();
        });
    }

    public void Remove(DownloadTask task, bool deleteFiles = false)
    {
        RemoveQueued(task);
        _scheduled.Remove(task);
        _windowHeld.Remove(task);

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
            await Task.WhenAll(session.Chunks);
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

    public void Schedule(DownloadTask task, DateTime when)
    {
        bool startNow;
        lock (_lock)
        {
            if (_sessions.ContainsKey(task.Id))
                return;
            _queue.Remove(task);
            _windowHeld.Remove(task);
            task.ScheduledStart = when;
            startNow = when <= DateTime.Now;
            if (startNow)
            {
                _scheduled.Remove(task);
            }
            else
            {
                task.Status = TaskStatus.Scheduled;
                if (!_scheduled.Contains(task))
                    _scheduled.Add(task);
            }
        }
        if (startNow)
        {
            Start(task);
            return;
        }
        PumpQueue();
        TaskChanged?.Invoke();
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
        _ = RunSessionAsync(session);
    }

    private void PumpQueue()
    {
        List<DownloadTask> toStart = new();
        lock (_lock)
        {
            if (!IsWindowOpen())
                return;
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
        try
        {
            var meta = await ProbeAsync(task, session.Token);
            task.TotalBytes = meta.TotalBytes;
            task.FileName = string.IsNullOrWhiteSpace(task.FileName)
                ? DeriveName(task.Url)
                : SanitizeFileName(task.FileName);
            task.FileName = EnsureUniqueName(task.SaveFolder, task.FileName);
            Directory.CreateDirectory(task.SaveFolder);

            if (meta.TotalBytes > 0 && meta.SupportsRanges)
                await RunChunkedAsync(session, meta.TotalBytes);
            else
                await RunSingleStreamAsync(session);

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
            task.Status = TaskStatus.Paused;
        }
        catch (Exception ex)
        {
            if (session.Removed)
                return;
            task.Status = TaskStatus.Failed;
            task.Error = ex.Message;
        }
        finally
        {
            session.Finish();
            lock (_lock)
            {
                if (_sessions.ContainsKey(task.Id) && session.Done)
                    _sessions.Remove(task.Id);
            }
            session.Dispose();
            TaskChanged?.Invoke();
            PumpQueue();
            lock (_lock)
            {
                if (ActiveCount == 0 && QueuedCount == 0)
                    _meter.Stop();
            }
        }
    }

    private async Task<(long TotalBytes, bool SupportsRanges)> ProbeAsync(DownloadTask task, CancellationToken ct)
    {
        var head = await SendWithRetryAsync(() => BuildRequest(HttpMethod.Head, task, null), ct);
        using (head)
        {
            if (head.IsSuccessStatusCode)
            {
                bool ranges = head.Headers.AcceptRanges.Any(r => r.Equals("bytes", StringComparison.OrdinalIgnoreCase));
                long total = head.Content.Headers.ContentLength ?? -1;
                if (total > 0)
                    return (total, ranges);
            }
        }

        // Fall back to a ranged GET which is the most reliable probe.
        var get = await SendWithRetryAsync(() => BuildRequest(HttpMethod.Get, task, new RangeHeaderValue(0, 0)), ct);
        using (get)
        {
            get.EnsureSuccessStatusCode();
            long totalBytes = get.Content.Headers.ContentRange?.Length ?? -1;
            bool supportsRanges = get.StatusCode == HttpStatusCode.PartialContent;
            return (totalBytes, supportsRanges);
        }
    }

    private async Task RunChunkedAsync(Session session, long totalBytes)
    {
        var task = session.Task;
        int count = Math.Max(1, task.ChunkCount);

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
        int attempt = 0;
        while (true)
        {
            long chunkBytes;
            try
            {
                chunkBytes = await DownloadChunkAsync(session, output, from, to);
            }
            catch (Exception ex) when (IsTransient(ex) && !session.Token.IsCancellationRequested && attempt < MaxRetries)
            {
                await BackoffAsync(attempt, session.Token);
                attempt++;
                continue;
            }
            catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException && attempt < MaxRetries &&
                                       !session.Token.IsCancellationRequested)
            {
                await BackoffAsync(attempt, session.Token);
                attempt++;
                continue;
            }

            if (chunkBytes < 0)
                return;
            state.SetCompleted(index);
            Interlocked.Add(ref session.BytesDownloaded, chunkBytes);
            state.SaveIfDirty(session.StatePath);
            return;
        }
    }

    private async Task<long> DownloadChunkAsync(Session session, FileStream output, long from, long to)
    {
        var task = session.Task;
        var response = await SendWithRetryAsync(() => BuildRequest(HttpMethod.Get, task, new RangeHeaderValue(from, to)), session.Token);
        using (response)
        {
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
            if (written != to - from + 1)
                throw new HttpRequestException($"Chunk incomplete: got {written} of {to - from + 1} bytes.");
            return written;
        }
    }

    private async Task RunSingleStreamAsync(Session session)
    {
        var task = session.Task;
        int attempt = 0;
        while (true)
        {
            try
            {
                await RunSingleStreamAttemptAsync(session);
                return;
            }
            catch (Exception ex) when (IsTransient(ex) && !session.Token.IsCancellationRequested && attempt < MaxRetries)
            {
                await BackoffAsync(attempt, session.Token);
                attempt++;
            }
        }
    }

    private async Task RunSingleStreamAttemptAsync(Session session)
    {
        var task = session.Task;
        var response = await SendWithRetryAsync(() => BuildRequest(HttpMethod.Get, task, null), session.Token);
        using (response)
        {
            response.EnsureSuccessStatusCode();

            if (response.Content.Headers.ContentLength is long length && length > 0 && task.TotalBytes < 0)
                task.TotalBytes = length;

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
            total += (long)speed;

            if (session.Task.TotalBytes > 0)
            {
                int percent = (int)(now * 100 / session.Task.TotalBytes);
                session.Task.Progress = Math.Clamp(percent, 0, 100);
                double remaining = session.Task.TotalBytes - now;
                session.Task.Eta = speed > 1 ? FormatEta(remaining / speed) : "";
            }
        }
        Interlocked.Exchange(ref _totalSpeedBps, total);
    }

    private void EvaluateSchedule()
    {
        // Start due scheduled tasks.
        DownloadTask[] due;
        lock (_lock)
        {
            due = _scheduled.Where(t => t.ScheduledStart is DateTime s && s <= DateTime.Now).ToArray();
            foreach (var t in due)
                _scheduled.Remove(t);
        }
        foreach (var task in due)
            Start(task);

        ApplySpeedLimit();

        bool open = IsWindowOpen();
        bool wasOpen;
        DownloadTask[] held;
        lock (_lock)
        {
            wasOpen = _windowOpen;
            if (open == wasOpen)
                return;
            _windowOpen = open;
            if (!open)
            {
                // Window just closed: pause everything and hold tasks so they auto-resume.
                Session[] active = _sessions.Values.ToArray();
                foreach (var s in active)
                {
                    if (s.Removed)
                        continue;
                    _windowHeld.Add(s.Task);
                    s.Cancel();
                }
                TaskChanged?.Invoke();
                return;
            }
            held = _windowHeld.ToArray();
            _windowHeld.Clear();
        }

        if (open && !wasOpen)
        {
            foreach (var task in held)
                Start(task);
            PumpQueue();
        }
    }

    private bool IsWindowOpen()
    {
        lock (_lock)
        {
            if (!_windowEnabled)
                return true;
            return InTimeRange(DateTime.Now.TimeOfDay, _windowStart, _windowEnd);
        }
    }

    private long EffectiveLimitKbps()
    {
        lock (_lock)
        {
            if (_throttleEnabled && InTimeRange(DateTime.Now.TimeOfDay, _throttleStart, _throttleEnd) && _throttleLimitKbps > 0)
                return _throttleLimitKbps;
            return _baseLimitKbps;
        }
    }

    private void ApplySpeedLimit()
    {
        _governor.LimitKbps = EffectiveLimitKbps();
    }

    private static bool InTimeRange(TimeSpan now, string startText, string endText)
    {
        if (!TimeSpan.TryParse(startText, out var start) || !TimeSpan.TryParse(endText, out var end))
            return true;
        if (start == end)
            return true;
        if (start < end)
            return now >= start && now < end;
        // Overnight window, e.g. 22:00–06:00.
        return now >= start || now < end;
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

    private static HttpRequestMessage BuildRequest(HttpMethod method, DownloadTask task, RangeHeaderValue? range)
    {
        var request = new HttpRequestMessage(method, task.Url);
        if (range is not null)
            request.Headers.Range = range;
        if (!string.IsNullOrWhiteSpace(task.Referer) && Uri.TryCreate(task.Referer, UriKind.Absolute, out var referer))
            request.Headers.Referrer = referer;
        return request;
    }

    private static string FormatEta(double seconds)
    {
        if (seconds <= 0 || double.IsInfinity(seconds) || double.IsNaN(seconds))
            return "";
        if (seconds < 60)
            return $"{Math.Ceiling(seconds)}s";
        if (seconds < 3600)
            return $"{Math.Ceiling(seconds / 60)}m";
        return $"{Math.Round(seconds / 3600, 1)}h";
    }

    public static string DeriveName(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            string name = Path.GetFileName(uri.AbsolutePath);
            if (!string.IsNullOrWhiteSpace(name))
                return SanitizeFileName(name);
        }
        return $"download_{DateTime.Now:yyyyMMddHHmmss}.bin";
    }

    public static string SanitizeFileName(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        name = name.Trim();
        return string.IsNullOrWhiteSpace(name) ? $"download_{DateTime.Now:yyyyMMddHHmmss}.bin" : name;
    }

    private static string EnsureUniqueName(string folder, string name)
    {
        string full = Path.Combine(folder, name);
        if (!File.Exists(full))
            return name;

        string baseName = Path.GetFileNameWithoutExtension(name);
        string ext = Path.GetExtension(name);
        for (int i = 1; ; i++)
        {
            string candidate = $"{baseName} ({i}){ext}";
            if (!File.Exists(Path.Combine(folder, candidate)))
                return candidate;
        }
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

    private sealed class Session : IDisposable
    {
        public Session(DownloadTask task)
        {
            Task = task;
        }

        public DownloadTask Task { get; }
        public CancellationTokenSource Cts { get; } = new();
        public CancellationToken Token => Cts.Token;
        public List<Task> Chunks { get; } = new();
        public ChunkState? State { get; set; }
        public long ChunkSize;
        public int NextChunk;
        public long BytesDownloaded;
        public long LastBytes;
        public bool Removed;
        public bool Done;
        public SpeedGovernor Governor { get; } = new();

        public string StatePath => $"{Task.FullPath}.wdmstate";

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
        private long _completed;
        private long _lastSaveTick;
        private readonly object _lock = new();

        private ChunkState(long totalBytes, long chunkSize, int chunkCount)
        {
            _totalBytes = totalBytes;
            _chunkSize = chunkSize;
            _bits = new byte[(chunkCount + 7) / 8];
        }

        public int ChunkCount => _bits.Length * 8;
        public long CompletedBytes => Interlocked.Read(ref _completed) * _chunkSize;
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
                if ((_bits[index >> 3] & (1 << (index & 7))) == 0)
                {
                    _bits[index >> 3] |= (byte)(1 << (index & 7));
                    Interlocked.Increment(ref _completed);
                }
            }
        }

        public void SaveIfDirty(string path)
        {
            long now = Environment.TickCount64;
            if (Interlocked.Read(ref _lastSaveTick) == 0)
                return;
            if (now - Interlocked.Read(ref _lastSaveTick) < 1000)
                return;
            Save(path);
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
                    ChunkCount = _bits.Length * 8,
                    Bits = Convert.ToBase64String(_bits),
                };
                File.WriteAllText(path, JsonSerializer.Serialize(record));
                Interlocked.Exchange(ref _lastSaveTick, Environment.TickCount64);
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
            foreach (byte b in _bits)
            {
                for (int i = 0; i < 8; i++)
                    if ((b & (1 << i)) != 0)
                        count++;
            }
            return count;
        }

        private void CountCompleted(ref long completed)
        {
            Interlocked.Exchange(ref completed, CountBits());
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
}
