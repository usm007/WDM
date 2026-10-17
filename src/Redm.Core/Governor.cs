namespace Redm.Core;

/// <summary>Shared per-download speed limiter (token bucket over 1 s windows).</summary>
public sealed class SpeedGovernor
{
    private readonly long _bps;
    private long _windowStart = Environment.TickCount64;
    private long _windowBytes;

    public SpeedGovernor(long bps) { _bps = bps; }
    public bool Enabled => _bps > 0;

    public async Task ThrottleAsync(int n, CancellationToken ct)
    {
        if (_bps <= 0) return;
        long bytes = Interlocked.Add(ref _windowBytes, n);
        long elapsed = Environment.TickCount64 - Interlocked.Read(ref _windowStart);
        long expected = bytes * 1000 / _bps;
        if (expected > elapsed)
            await Task.Delay((int)(expected - elapsed), ct);
        if (elapsed >= 1000)
        {
            Interlocked.Exchange(ref _windowBytes, 0);
            Interlocked.Exchange(ref _windowStart, Environment.TickCount64);
        }
    }
}

/// <summary>Address-exception list ("address exceptions"): wildcard host/path
/// masks, one per line, e.g. <c>*.update.com/*</c>. Matched URLs are never
/// auto-taken-over; manual Add URL still works.</summary>
public static class AddressExceptions
{
    public static bool IsExcluded(IEnumerable<string> masks, string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return false;
        string host = u.Host.ToLowerInvariant();
        string full = (host + u.AbsolutePath).ToLowerInvariant();
        foreach (var raw in masks)
        {
            string m = raw.Trim().ToLowerInvariant();
            if (m == "") continue;
            string rx = "^" + System.Text.RegularExpressions.Regex.Escape(m)
                .Replace("\\*", ".*").Replace("\\?", ".") + "$";
            string target = m.Contains("/") ? full : host;
            if (System.Text.RegularExpressions.Regex.IsMatch(target, rx)) return true;
        }
        return false;
    }
}
