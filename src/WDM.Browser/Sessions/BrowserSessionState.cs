namespace WDM.Browser.Sessions;

/// <summary>Browser resolver page lifecycle. Explicit state machine — never
/// scattered boolean flags. Terminal states: Completed, TimedOut, Cancelled,
/// Failed, Crashed, Disposed.</summary>
public enum BrowserSessionState
{
    Created,
    Starting,
    Navigating,
    Loading,
    Analyzing,
    Resolving,
    CandidatesFound,
    Completed,
    TimedOut,
    Cancelled,
    Failed,
    Crashed,
    Disposed,
}

/// <summary>Legal transitions. Anything else is a bug — the host and the
/// manager both enforce this.</summary>
public static class BrowserSessionTransitions
{
    public static bool CanTransitionTo(this BrowserSessionState from, BrowserSessionState to) =>
        (from, to) switch
        {
            (BrowserSessionState.Created, BrowserSessionState.Starting) => true,
            (BrowserSessionState.Created, BrowserSessionState.Disposed) => true,
            (BrowserSessionState.Starting, BrowserSessionState.Navigating) => true,
            (BrowserSessionState.Navigating, BrowserSessionState.Loading) => true,
            (BrowserSessionState.Loading, BrowserSessionState.Analyzing) => true,
            (BrowserSessionState.Analyzing, BrowserSessionState.Resolving) => true,
            (BrowserSessionState.Resolving, BrowserSessionState.CandidatesFound) => true,
            (BrowserSessionState.CandidatesFound, BrowserSessionState.Completed) => true,
            // Early completion (page had nothing, manifest found statically).
            (BrowserSessionState.Loading, BrowserSessionState.Completed) => true,
            (BrowserSessionState.Analyzing, BrowserSessionState.Completed) => true,
            (BrowserSessionState.Resolving, BrowserSessionState.Completed) => true,
            // Abort paths from any live state.
            (var f, BrowserSessionState.TimedOut) when IsLive(f) => true,
            (var f, BrowserSessionState.Cancelled) when IsLive(f) => true,
            (var f, BrowserSessionState.Failed) when IsLive(f) => true,
            (var f, BrowserSessionState.Crashed) when IsLive(f) => true,
            // Cleanup from anywhere non-terminal.
            (var f, BrowserSessionState.Disposed) when !IsTerminal(f) => true,
            _ => false,
        };

    public static bool IsLive(BrowserSessionState s) => s is BrowserSessionState.Starting
        or BrowserSessionState.Navigating or BrowserSessionState.Loading
        or BrowserSessionState.Analyzing or BrowserSessionState.Resolving
        or BrowserSessionState.CandidatesFound;

    public static bool IsTerminal(BrowserSessionState s) => s is BrowserSessionState.Completed
        or BrowserSessionState.TimedOut or BrowserSessionState.Cancelled
        or BrowserSessionState.Failed or BrowserSessionState.Crashed
        or BrowserSessionState.Disposed;
}
