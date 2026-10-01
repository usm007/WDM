using System;
using System.Text.Json;
using System.Threading;
using WDM.Browser.Contracts;
using WDM.Browser.Ipc;
using WDM.Browser.Models;
using WDM.Browser.Sessions;

namespace WDM.Browser.Sessions;

/// <summary>One isolated page analysis over a shared host connection.
/// Sequential protocol: Navigate streams events, then script/body calls.
/// Cancellation aborts host work and releases the session.</summary>
internal sealed class BrowserSession : IBrowserSession
{
    private readonly BrowserPipeClient _client;
    private readonly BrowserSessionOptions _options;
    private readonly Action _release;
    private BrowserSessionState _state = BrowserSessionState.Created;
    private bool _disposed;

    public string SessionId { get; }

    public BrowserSessionState State => _state;

    public event Action<BrowserSessionState>? StateChanged;

    internal BrowserSession(BrowserPipeClient client, string sessionId,
        BrowserSessionOptions options, Action release)
    {
        _client = client;
        SessionId = sessionId;
        _options = options;
        _release = release;
    }

    public async IAsyncEnumerable<BrowserNetworkEvent> NavigateAsync(string url,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        MoveTo(BrowserSessionState.Navigating);
        await _client.SendAsync(BrowserProtocol.Navigate, SessionId, new
        {
            url,
            navigationTimeoutSec = (int)_options.NavigationTimeout.TotalSeconds,
            discoveryTimeoutSec = (int)_options.DiscoveryTimeout.TotalSeconds,
            maxEvents = _options.MaxNetworkEvents,
            triggerAutoplay = _options.TriggerAutoplay,
        }, ct).ConfigureAwait(false);

        int yielded = 0;
        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var msg = await _client.ReceiveAsync(ct).ConfigureAwait(false);
                if (msg.SessionId is not null && !msg.SessionId.Equals(SessionId, StringComparison.Ordinal))
                    continue;
                switch (msg.Type)
                {
                    case BrowserProtocol.NetworkEvents:
                        foreach (var evt in ParseEvents(msg))
                        {
                            if (yielded >= _options.MaxNetworkEvents)
                                break;
                            yielded++;
                            yield return evt;
                        }
                        break;
                    case BrowserProtocol.PageState:
                        {
                            var state = MapState(PayloadString(msg, "state"));
                            if (state is not null)
                                MoveTo(state.Value);
                            if (state is BrowserSessionState.Completed or BrowserSessionState.TimedOut
                                or BrowserSessionState.Cancelled or BrowserSessionState.Failed
                                or BrowserSessionState.Crashed)
                                yield break;
                            break;
                        }
                    case BrowserProtocol.BrowserCrashed:
                        MoveTo(BrowserSessionState.Crashed);
                        throw new InvalidOperationException("Browser renderer terminated during analysis.");
                    case BrowserProtocol.Error:
                        MoveTo(BrowserSessionState.Failed);
                        throw new InvalidOperationException("Browser host error: " + (PayloadString(msg, "message") ?? "unknown"));
                }
            }
        }
        finally
        {
            if (_state == BrowserSessionState.Navigating || _state == BrowserSessionState.Loading)
                MoveTo(BrowserSessionState.Cancelled);
        }
    }

    public async Task<string?> ExecuteScriptAsync(string script, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _client.SendAsync(BrowserProtocol.ExecuteScript, SessionId, new { script }, ct).ConfigureAwait(false);
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var msg = await _client.ReceiveAsync(ct).ConfigureAwait(false);
            if (msg.Type != BrowserProtocol.ScriptResult)
                continue;
            if (msg.SessionId is not null && !msg.SessionId.Equals(SessionId, StringComparison.Ordinal))
                continue;
            if (msg.Payload is not JsonElement p)
                return null;
            bool ok = p.TryGetProperty("ok", out var okEl) && okEl.ValueKind == JsonValueKind.True;
            if (!ok)
                return null;
            if (p.TryGetProperty("result", out var r) && r.ValueKind == JsonValueKind.String)
                return r.GetString();
            return null;
        }
    }

    public async Task<byte[]?> FetchBodyAsync(string url, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _client.SendAsync(BrowserProtocol.FetchBody, SessionId,
            new { url, maxBytes = _options.MaxBodyBytes }, ct).ConfigureAwait(false);
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var msg = await _client.ReceiveAsync(ct).ConfigureAwait(false);
            if (msg.Type != BrowserProtocol.BodyResult)
                continue;
            if (msg.SessionId is not null && !msg.SessionId.Equals(SessionId, StringComparison.Ordinal))
                continue;
            if (msg.Payload is not JsonElement p)
                return null;
            bool ok = p.TryGetProperty("ok", out var okEl) && okEl.ValueKind == JsonValueKind.True;
            if (!ok)
                return null;
            if (p.TryGetProperty("base64", out var b) && b.ValueKind == JsonValueKind.String)
            {
                string s = b.GetString() ?? "";
                if (s.Length > _options.MaxBodyBytes * 2)
                    return null;
                try { return Convert.FromBase64String(s); }
                catch { return null; }
            }
            return null;
        }
    }

    public async Task CloseAsync()
    {
        if (_disposed)
            return;
        try
        {
            if (_state is BrowserSessionState.Navigating or BrowserSessionState.Loading)
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await _client.SendAsync(BrowserProtocol.CancelNavigate, SessionId, new { }, cts.Token).ConfigureAwait(false);
            }
        }
        catch { }
        MoveTo(BrowserSessionState.Disposed);
        _disposed = true;
        try { _release(); } catch { }
    }

    public async ValueTask DisposeAsync() => await CloseAsync().ConfigureAwait(false);

    private void MoveTo(BrowserSessionState next)
    {
        if (_state == next)
            return;
        if (_state.CanTransitionTo(next))
        {
            _state = next;
            try { StateChanged?.Invoke(next); } catch { }
        }
    }

    private static BrowserSessionState? MapState(string? s) => s switch
    {
        "Navigating" => BrowserSessionState.Navigating,
        "Loading" => BrowserSessionState.Loading,
        "Analyzing" => BrowserSessionState.Analyzing,
        "Resolving" => BrowserSessionState.Resolving,
        "Completed" => BrowserSessionState.Completed,
        "TimedOut" => BrowserSessionState.TimedOut,
        "Cancelled" => BrowserSessionState.Cancelled,
        "Failed" => BrowserSessionState.Failed,
        "Crashed" => BrowserSessionState.Crashed,
        _ => null,
    };

    private static string? PayloadString(BrowserMessage.Envelope msg, string name)
    {
        try
        {
            if (msg.Payload is JsonElement p && p.TryGetProperty(name, out var v)
                && v.ValueKind == JsonValueKind.String)
                return v.GetString();
        }
        catch { }
        return null;
    }

    internal static List<BrowserNetworkEvent> ParseEvents(BrowserMessage.Envelope msg)
    {
        var list = new List<BrowserNetworkEvent>();
        try
        {
            if (msg.Payload is not JsonElement p || !p.TryGetProperty("events", out var arr)
                || arr.ValueKind != JsonValueKind.Array)
                return list;
            foreach (var e in arr.EnumerateArray())
            {
                if (list.Count >= BrowserMessage.MaxEventsPerMessage)
                    break;
                var evt = ParseEvent(e);
                if (evt is not null)
                    list.Add(evt);
            }
        }
        catch { }
        return list;
    }

    private static BrowserNetworkEvent? ParseEvent(JsonElement e)
    {
        try
        {
            string url = e.TryGetProperty("url", out var u) && u.ValueKind == JsonValueKind.String
                ? u.GetString() ?? "" : "";
            if (!BrowserMessage.IsSafeUrl(url))
                return null;
            return new BrowserNetworkEvent
            {
                RequestId = Str(e, "requestId") ?? "",
                Timestamp = DateTimeOffset.UtcNow,
                Url = url,
                Method = Str(e, "method") ?? "GET",
                RequestHeaders = Dict(e, "requestHeaders"),
                ResponseHeaders = Dict(e, "responseHeaders"),
                StatusCode = e.TryGetProperty("statusCode", out var s) && s.ValueKind == JsonValueKind.Number && s.TryGetInt32(out int sc) ? sc : 0,
                ContentType = Str(e, "contentType"),
                ResourceType = Str(e, "resourceType") ?? "",
                FrameId = Str(e, "frameId"),
                Initiator = Str(e, "initiator"),
            };
        }
        catch
        {
            return null;
        }
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static Dictionary<string, string> Dict(JsonElement e, string name)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (e.TryGetProperty(name, out var o) && o.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in o.EnumerateObject())
                {
                    if (prop.Value.ValueKind == JsonValueKind.String)
                        dict[prop.Name] = prop.Value.GetString() ?? "";
                }
            }
        }
        catch { }
        return BrowserMessage.SanitizeHeaders(dict);
    }
}
