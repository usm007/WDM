namespace Redm.Core;

public record ProbeResult(long? Length, string? ETag, string? LastModified);

public class TransferEngine
{
    private readonly HttpClient _http;
    public const int MaxAttempts = 3;
    public static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(200);

    public TransferEngine(HttpClient? http = null)
    {
        _http = http ?? new HttpClient();
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("ReDM/0.1");
    }

    public async Task<long?> ProbeSizeAsync(string url, Dictionary<string, string>? headers = null, CancellationToken ct = default)
        => (await ProbeAsync(url, headers, ct)).Length;

    public async Task<ProbeResult> ProbeAsync(string url, Dictionary<string, string>? headers = null, CancellationToken ct = default)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Head, url);
                if (headers != null) foreach (var kv in headers) req.Headers.TryAddWithoutValidation(kv.Key, kv.Value);
                using var res = await _http.SendAsync(req, ct);
                if (IsTransient(res.StatusCode) && attempt < MaxAttempts)
                {
                    await Task.Delay(RetryDelay, ct);
                    continue;
                }
                res.EnsureSuccessStatusCode();
                return new ProbeResult(
                    res.Content.Headers.ContentLength,
                    res.Headers.ETag?.Tag,
                    res.Content.Headers.LastModified?.UtcDateTime.ToString("R"));
            }
            catch (OperationCanceledException) { throw; }
            catch (HttpRequestException) when (attempt < MaxAttempts)
            {
                await Task.Delay(RetryDelay, ct);
            }
        }
    }

    internal static bool IsTransient(System.Net.HttpStatusCode code)
        => code is System.Net.HttpStatusCode.RequestTimeout or System.Net.HttpStatusCode.TooManyRequests
            or System.Net.HttpStatusCode.InternalServerError or System.Net.HttpStatusCode.BadGateway
            or System.Net.HttpStatusCode.ServiceUnavailable or System.Net.HttpStatusCode.GatewayTimeout;

    public async Task DownloadAsync(string url, string dest, int connections, long speedLimitBps,
        IProgress<double>? progress, CancellationToken ct, string? expectedSha256 = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(dest) ?? ".");
        ProbeResult probe;
        try { probe = await ProbeAsync(url, null, ct); }
        catch { probe = new ProbeResult(null, null, null); }
        long? size = probe.Length;
        // Fallback to single stream when size unknown
        if (size is null || size <= 0)
        {
            var gov0 = new SpeedGovernor(speedLimitBps);
            using var out0 = new FileStream(dest + ".part", FileMode.Append, FileAccess.Write, FileShare.None);
            long resumeFrom = out0.Length;
            using var res = await SendWithRetryAsync(url, resumeFrom > 0 ? resumeFrom : null, null, probe.ETag, ct);
            res.EnsureSuccessStatusCode();
            using var src = await res.Content.ReadAsStreamAsync(ct);
            var buf0 = new byte[64 * 1024];
            int c0;
            while ((c0 = await src.ReadAsync(buf0, ct)) > 0)
            {
                await out0.WriteAsync(buf0.AsMemory(0, c0), ct);
                await gov0.ThrottleAsync(c0, ct);
            }
            out0.Close();
            File.Move(dest + ".part", dest, true);
            progress?.Report(1);
            VerifyIntegrity(dest, expectedSha256);
            return;
        }
        var segs = Engine.Plan(size, connections);
        var parts = segs.Select(s => dest + $".part{s.Index}").ToList();
        long total = size.Value, done0 = parts.Where(File.Exists).Select(p => new FileInfo(p).Length).Sum();
        long done = done0;
        var gov = new SpeedGovernor(speedLimitBps);
        progress?.Report(total == 0 ? 0 : (double)done / total);
        var tasks = segs.Select(async s =>
        {
            string part = dest + $".part{s.Index}";
            long need = s.End - s.Start + 1;
            long existing = File.Exists(part) ? new FileInfo(part).Length : 0;
            if (existing > need)
            {
                // Stale over-long part (e.g. source shrank): restart clean.
                // The 416 path below is the server-side backstop for the same case.
                try { File.Delete(part); } catch { }
            }
            else if (existing == need) return;
            bool restarted = false;
            for (int attempt = 1; ; attempt++)
            {
                long have = File.Exists(part) ? new FileInfo(part).Length : 0;
                try
                {
                    using var outS = new FileStream(part, FileMode.Append, FileAccess.Write, FileShare.None);
                    using var res = await SendWithRetryAsync(url, s.Start + have, s.End, have > 0 ? probe.ETag : null, ct);
                    if (res.StatusCode == System.Net.HttpStatusCode.OK && have > 0 && !restarted)
                    {
                        // Validator changed mid-resume: server ignored Range. Restart clean.
                        restarted = true;
                        outS.Close();
                        File.WriteAllBytes(part, Array.Empty<byte>());
                        res.Dispose();
                        continue;
                    }
                    if (res.StatusCode == System.Net.HttpStatusCode.RequestedRangeNotSatisfiable && !restarted)
                    {
                        restarted = true;
                        outS.Close();
                        File.WriteAllBytes(part, Array.Empty<byte>());
                        res.Dispose();
                        continue;
                    }
                    res.EnsureSuccessStatusCode();
                    using var src = await res.Content.ReadAsStreamAsync(ct);
                    var buf = new byte[64 * 1024];
                    int n;
                    while ((n = await src.ReadAsync(buf, ct)) > 0)
                    {
                        await outS.WriteAsync(buf.AsMemory(0, n), ct);
                        await gov.ThrottleAsync(n, ct);
                        Interlocked.Add(ref done, n);
                        progress?.Report((double)done / total);
                    }
                    return;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) when (ex is HttpRequestException or IOException && attempt < MaxAttempts)
                {
                    // Dropped connection or reset mid-body: part file kept,
                    // next attempt resumes from its grown length.
                    await Task.Delay(RetryDelay, ct);
                }
            }
        });
        await Task.WhenAll(tasks);
        using (var outF = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            foreach (var p in parts)
            {
                using var f = File.OpenRead(p);
                await f.CopyToAsync(outF, ct);
            }
        }
        foreach (var p in parts) File.Delete(p);
        VerifyIntegrity(dest, expectedSha256);
    }

    private async Task<HttpResponseMessage> SendWithRetryAsync(string url, long? rangeStart, long? rangeEnd,
        string? etag, CancellationToken ct)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                var req = new HttpRequestMessage(HttpMethod.Get, url);
                if (rangeStart.HasValue)
                    req.Headers.Range = rangeEnd.HasValue
                        ? new System.Net.Http.Headers.RangeHeaderValue(rangeStart.Value, rangeEnd.Value)
                        : new System.Net.Http.Headers.RangeHeaderValue(rangeStart.Value, null);
                if (etag is not null && rangeStart.HasValue)
                    req.Headers.TryAddWithoutValidation("If-Range", etag);
                var res = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
                if (IsTransient(res.StatusCode) && attempt < MaxAttempts)
                {
                    res.Dispose();
                    await Task.Delay(RetryDelay, ct);
                    continue;
                }
                return res;
            }
            catch (OperationCanceledException) { throw; }
            catch (HttpRequestException) when (attempt < MaxAttempts)
            {
                await Task.Delay(RetryDelay, ct);
            }
            catch (IOException) when (attempt < MaxAttempts)
            {
                // Dropped connection mid-body: resume from grown part file.
                await Task.Delay(RetryDelay, ct);
            }
        }
    }

    private static void VerifyIntegrity(string dest, string? expectedSha256)
    {
        if (expectedSha256 is null) return;
        string actual;
        using (var sha = System.Security.Cryptography.SHA256.Create())
        using (var f = File.OpenRead(dest))
            actual = Convert.ToHexString(sha.ComputeHash(f));
        if (!actual.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            try { File.Delete(dest); } catch { }
            throw new InvalidDataException($"SHA-256 mismatch: expected {expectedSha256}, got {actual}");
        }
    }
}
