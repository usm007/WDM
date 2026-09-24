using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace WDM.Services.Chunking;

/// <summary>
/// Per-origin concurrency budget shared by every download session targeting the
/// same scheme+host+port. Range parallelism is per-session; origin pressure is
/// global so three tasks against one CDN cannot run 3× the workers independently.
/// Disk failures never touch origin state.
/// </summary>
public static class OriginController
{
    private const int DefaultBudget = 16;
    private const int MaxBudget = 32;
    private static readonly ConcurrentDictionary<string, OriginState> _states = new(StringComparer.OrdinalIgnoreCase);

    private sealed class OriginState
    {
        public readonly object Lock = new();
        public int Budget = DefaultBudget;
        public int Active;
        public DateTimeOffset BackoffUntil;
        public TimeSpan Backoff = TimeSpan.FromMilliseconds(500);
    }

    public static string KeyOf(string url)
    {
        try
        {
            var u = new Uri(url);
            int port = u.IsDefaultPort ? (u.Scheme == "https" ? 443 : 80) : u.Port;
            return $"{u.Scheme}://{u.Host.ToLowerInvariant()}:{port}";
        }
        catch { return url; }
    }

    private static OriginState Get(string origin) => _states.GetOrAdd(origin, _ => new OriginState());

    /// <summary>Test hook: clears all origin state.</summary>
    internal static void ResetForTests() => _states.Clear();

    public static async Task WaitForSlotAsync(string origin, CancellationToken ct)
    {
        var st = Get(origin);
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            TimeSpan wait = TimeSpan.Zero;
            bool acquired = false;
            lock (st.Lock)
            {
                var now = DateTimeOffset.UtcNow;
                if (now < st.BackoffUntil)
                {
                    wait = st.BackoffUntil - now;
                }
                else if (st.Active < st.Budget)
                {
                    st.Active++;
                    acquired = true;
                }
                else
                {
                    wait = TimeSpan.FromMilliseconds(25);
                }
            }
            if (acquired)
                return;
            await Task.Delay(wait > TimeSpan.Zero ? wait : TimeSpan.FromMilliseconds(25), ct);
        }
    }

    public static void Release(string origin)
    {
        if (_states.TryGetValue(origin, out var st))
        {
            lock (st.Lock)
                st.Active = Math.Max(0, st.Active - 1);
        }
    }

    public static void ReportResult(string origin, HttpOutcome outcome)
    {
        var st = Get(origin);
        lock (st.Lock)
        {
            switch (outcome)
            {
                case HttpOutcome.Success:
                    st.Backoff = TimeSpan.FromMilliseconds(500);
                    // Slow additive increase on sustained success only.
                    if (st.Budget < MaxBudget)
                        st.Budget++;
                    break;
                case HttpOutcome.TooManyRequests:
                case HttpOutcome.ServerError:
                case HttpOutcome.Timeout:
                case HttpOutcome.ConnectionReset:
                    st.Budget = Math.Max(1, st.Budget / 2);
                    var now = DateTimeOffset.UtcNow;
                    st.BackoffUntil = now + st.Backoff;
                    st.Backoff = TimeSpan.FromMilliseconds(Math.Min(st.Backoff.TotalMilliseconds * 2, 30_000));
                    break;
                default:
                    break; // client errors / integrity faults are not server pressure
            }
        }
    }

    public static (int Active, int Budget, TimeSpan BackoffRemaining) GetPressure(string origin)
    {
        var st = Get(origin);
        lock (st.Lock)
        {
            var rem = st.BackoffUntil - DateTimeOffset.UtcNow;
            return (st.Active, st.Budget, rem > TimeSpan.Zero ? rem : TimeSpan.Zero);
        }
    }
}
