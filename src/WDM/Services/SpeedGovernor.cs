using System.Diagnostics;

namespace WDM.Services;

/// <summary>
/// Shared token-bucket rate limiter. Call ThrottleAsync before each write so the
/// combined rate of all callers stays under the configured limit.
/// </summary>
public sealed class SpeedGovernor
{
    private readonly object _lock = new();
    private long _limitKbps;
    private double _tokens;
    private long _lastTick = Stopwatch.GetTimestamp();

    public long LimitKbps
    {
        get { lock (_lock) return _limitKbps; }
        set
        {
            lock (_lock)
            {
                _limitKbps = Math.Max(0, value);
                _tokens = 0;
                _lastTick = Stopwatch.GetTimestamp();
            }
        }
    }

    public async Task ThrottleAsync(long kbps, long bytes, CancellationToken ct)
    {
        // Single source of truth: an explicit per-call rate wins, otherwise fall
        // back to the configured LimitKbps property (set via ApplySpeedLimit).
        if (kbps <= 0)
            kbps = LimitKbps;
        if (kbps <= 0 || bytes <= 0)
            return;

        double rate = kbps * 1024.0; // bytes per second
        double maxTokens = Math.Max(rate, bytes);
        while (true)
        {
            double waitMs;
            lock (_lock)
            {
                Refill(rate, maxTokens);
                if (_tokens >= bytes)
                {
                    _tokens -= bytes;
                    return;
                }
                waitMs = (bytes - _tokens) * 1000.0 / rate;
            }
            // Wedge guard: never sleep more than a few seconds per slice even if
            // a caller passes an unexpectedly large byte count — the loop re-evaluates
            // afterwards, so throttling behavior is unchanged in normal operation.
            if (waitMs > 0.5)
                await Task.Delay((int)Math.Min(Math.Ceiling(waitMs), 2000), ct);
            else
                await Task.Delay(1, ct); // never busy-spin the refill loop
            ct.ThrowIfCancellationRequested();
        }
    }

    private void Refill(double rate, double maxTokens)
    {
        long now = Stopwatch.GetTimestamp();
        double seconds = (now - _lastTick) / (double)Stopwatch.Frequency;
        _lastTick = now;
        _tokens = Math.Min(_tokens + seconds * rate, maxTokens); // allow buffer size if larger than 1 second rate
    }
}
