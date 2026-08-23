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
        if (kbps <= 0 || bytes <= 0)
            return;

        double rate = kbps * 1024.0; // bytes per second
        while (true)
        {
            double waitMs;
            lock (_lock)
            {
                Refill(rate);
                if (_tokens >= bytes)
                {
                    _tokens -= bytes;
                    return;
                }
                waitMs = (bytes - _tokens) * 1000.0 / rate;
            }
            if (waitMs > 0.5)
                await Task.Delay((int)waitMs, ct);
            else
                await Task.Delay(1, ct); // never busy-spin the refill loop
            ct.ThrowIfCancellationRequested();
        }
    }

    private void Refill(double rate)
    {
        long now = Stopwatch.GetTimestamp();
        double seconds = (now - _lastTick) / (double)Stopwatch.Frequency;
        _lastTick = now;
        _tokens = Math.Min(_tokens + seconds * rate, rate); // cap burst to ~1 second
    }
}
