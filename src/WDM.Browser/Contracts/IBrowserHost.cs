using System.Threading;
using WDM.Browser.Models;
using WDM.Browser.Sessions;

namespace WDM.Browser.Contracts;

/// <summary>WDM-side control surface for one BrowserHost process. The host
/// owns all browser mechanics; this side owns lifecycle + policy.</summary>
public interface IBrowserHost : IAsyncDisposable
{
    BrowserHostState State { get; }
    event Action<BrowserHostState>? StateChanged;

    /// <summary>Launch WDM.BrowserHost.exe and complete the Hello handshake
    /// within options.StartupTimeout. Throws on timeout or version mismatch.</summary>
    Task StartAsync(BrowserSessionOptions options, CancellationToken ct);

    /// <summary>Graceful Shutdown message, then process kill on timeout.
    /// Never leaves an orphaned host behind.</summary>
    Task StopAsync();

    /// <summary>Stop + Start. Used at most once per crashed session.</summary>
    Task RestartAsync(BrowserSessionOptions options, CancellationToken ct);

    Task<IBrowserSession> CreateSessionAsync(BrowserSessionOptions options, CancellationToken ct);
}

public enum BrowserHostState
{
    Stopped,
    Starting,
    Ready,
    Busy,
    Crashed,
    Stopping,
}

/// <summary>One isolated page analysis: navigate, observe, script, close.
/// Cancellation aborts the host work and releases the session.</summary>
public interface IBrowserSession : IAsyncDisposable
{
    string SessionId { get; }
    BrowserSessionState State { get; }
    event Action<BrowserSessionState>? StateChanged;

    /// <summary>Load the page and stream normalized network events until the
    /// discovery budget expires, candidates arrive, or ct fires.</summary>
    IAsyncEnumerable<BrowserNetworkEvent> NavigateAsync(string url, CancellationToken ct);

    /// <summary>Run script in the page (title/DOM/media-element reads).
    /// Returns JSON-serializable result or null on failure.</summary>
    Task<string?> ExecuteScriptAsync(string script, CancellationToken ct);

    /// <summary>Fetch one response body by request id (selective, capped).</summary>
    Task<byte[]?> FetchBodyAsync(string requestId, CancellationToken ct);

    Task CloseAsync();
}
