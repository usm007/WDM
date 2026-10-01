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
        public int IntegrityFaults;
    }

    // Persisted per-host policy (source of truth: AppSettings, applied at
    // startup and on settings save). Survives restarts via settings.json.
    private static Dictionary<string, int> _overrides = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object _policyLock = new();

    /// <summary>Raised when a host earns a persisted cooldown (pressure or
    /// auto-degrade): (origin, untilUnixSeconds, cause). The app merges it
    /// into settings and saves (throttled by the subscriber).</summary>
    public static Action<string, long, string>? CooldownPersisted;

    /// <summary>Applies user caps + learned cooldowns (prunes expired).
    /// Unexpired cooldowns re-arm the in-memory backoff; user caps clamp
    /// the dynamic budget.</summary>
    public static void ApplyPolicy(
        Dictionary<string, int>? overrides,
        Dictionary<string, AppSettings.HostCooldown>? cooldowns)
    {
        var clean = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (overrides is not null)
        {
            foreach (var kv in overrides)
            {
                if (string.IsNullOrWhiteSpace(kv.Key))
                    continue;
                clean[kv.Key.Trim()] = Math.Clamp(kv.Value, 1, MaxBudget);
            }
        }
        lock (_policyLock)
            _overrides = clean;
        if (cooldowns is null)
            return;
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        foreach (var kv in cooldowns)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(kv.Key) || kv.Value is null || kv.Value.UntilUnix <= now)
                    continue;
                var st = Get(kv.Key.Trim());
                lock (st.Lock)
                {
                    var until = DateTimeOffset.FromUnixTimeSeconds(Math.Min(kv.Value.UntilUnix, now + 86400));
                    if (until > st.BackoffUntil)
                        st.BackoffUntil = until;
                }
            }
            catch { }
        }
    }

    private static int EffectiveBudget(OriginState st, string origin)
    {
        int b = st.Budget;
        lock (_policyLock)
        {
            if (_overrides.TryGetValue(origin, out int cap))
                b = Math.Min(b, cap);
        }
        return Math.Max(1, b);
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

    /// <summary>Test hook: clears all origin state (including user overrides).</summary>
    internal static void ResetForTests()
    {
        _states.Clear();
        lock (_policyLock)
            _overrides = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
    }

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
                else if (st.Active < EffectiveBudget(st, origin))
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
                    st.IntegrityFaults = 0;
                    // Slow additive increase on sustained success only.
                    if (st.Budget < MaxBudget)
                        st.Budget++;
                    break;
                case HttpOutcome.TooManyRequests:
                case HttpOutcome.ServerError:
                case HttpOutcome.Timeout:
                case HttpOutcome.ConnectionReset:
                    st.Budget = Math.Max(1, st.Budget / 2);
                    st.IntegrityFaults = 0;
                    var now = DateTimeOffset.UtcNow;
                    st.BackoffUntil = now + st.Backoff;
                    st.Backoff = TimeSpan.FromMilliseconds(Math.Min(st.Backoff.TotalMilliseconds * 2, 30_000));
                    PersistCooldown(origin, st.BackoffUntil,
                        outcome == HttpOutcome.TooManyRequests ? "429" :
                        outcome == HttpOutcome.ServerError ? "5xx" : "timeout");
                    break;
                default:
                    break; // client errors / integrity faults are not server pressure
            }
        }
    }

    /// <summary>Integrity faults (wrong ranges, content mismatches) are not
    /// server pressure — but three in a row from one host means a fragile
    /// endpoint: pin it to 1 connection and persist that verdict.</summary>
    public static void ReportIntegrityFault(string origin, string kind)
    {
        try
        {
            var st = Get(origin);
            lock (st.Lock)
            {
                if (++st.IntegrityFaults < 3)
                    return;
                st.IntegrityFaults = 0;
                st.Budget = 1;
                st.BackoffUntil = DateTimeOffset.UtcNow + TimeSpan.FromMinutes(5);
                PersistCooldown(origin, st.BackoffUntil, kind);
            }
        }
        catch { }
    }

    private static void PersistCooldown(string origin, DateTimeOffset until, string cause)
    {
        try { CooldownPersisted?.Invoke(origin, until.ToUnixTimeSeconds(), cause); }
        catch { }
    }

    public static (int Active, int Budget, TimeSpan BackoffRemaining) GetPressure(string origin)
    {
        var st = Get(origin);
        lock (st.Lock)
        {
            var rem = st.BackoffUntil - DateTimeOffset.UtcNow;
            return (st.Active, EffectiveBudget(st, origin), rem > TimeSpan.Zero ? rem : TimeSpan.Zero);
        }
    }
}
