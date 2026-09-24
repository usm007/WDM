namespace WDM.Browser.Models;

/// <summary>Normalized network/resource observation from the browser runtime.
/// The Media layer consumes these — never CEF types. Bodies are NOT included;
/// the host fetches a body only on explicit FetchBody request (§18).</summary>
public sealed record BrowserNetworkEvent
{
    public string RequestId { get; init; } = "";
    public DateTimeOffset Timestamp { get; init; }
    public string Url { get; init; } = "";
    public string Method { get; init; } = "GET";
    public Dictionary<string, string> RequestHeaders { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> ResponseHeaders { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public int StatusCode { get; init; }
    public string? ContentType { get; init; }
    public long? ContentLength { get; init; }
    /// <summary>CEF resource type name (MainFrame, Script, Xhr, Media, ...).</summary>
    public string ResourceType { get; init; } = "";
    public string? FrameId { get; init; }
    public string? Initiator { get; init; }
}
