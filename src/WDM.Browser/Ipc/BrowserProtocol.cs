namespace WDM.Browser.Ipc;

/// <summary>Wire protocol between WDM.exe and WDM.BrowserHost.exe (Named
/// Pipes, length-prefixed JSON). Versioned: a mismatch fails the handshake,
/// never half-talks.</summary>
public static class BrowserProtocol
{
    public const int Version = 1;
    public const string PipeNamePrefix = "WDM.BrowserHost.";

    // WDM → host.
    public const string Hello = "Hello";
    public const string Shutdown = "Shutdown";
    public const string Navigate = "Navigate";
    public const string CancelNavigate = "CancelNavigate";
    public const string ExecuteScript = "ExecuteScript";
    public const string GetPageState = "GetPageState";
    public const string FetchBody = "FetchBody";

    // Host → WDM.
    public const string Ready = "Ready";
    public const string Bye = "Bye";
    public const string PageState = "PageState";
    public const string ScriptResult = "ScriptResult";
    public const string BodyResult = "BodyResult";
    public const string NetworkEvents = "NetworkEvents";
    public const string ResolutionProgress = "ResolutionProgress";
    public const string BrowserCrashed = "BrowserCrashed";
    public const string Error = "Error";
}
