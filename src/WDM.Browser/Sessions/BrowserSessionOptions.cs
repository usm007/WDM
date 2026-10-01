using System;

namespace WDM.Browser.Sessions;

/// <summary>Bounds for one browser-assisted resolution. Every stage has an
/// independent timeout; cancellation propagates UI → resolver → host (§20).
/// Caps keep a hostile page from farming Chromium processes (§22).</summary>
public sealed record BrowserSessionOptions
{
    public TimeSpan StartupTimeout { get; init; } = TimeSpan.FromSeconds(15);
    public TimeSpan NavigationTimeout { get; init; } = TimeSpan.FromSeconds(20);
    public TimeSpan PageLoadTimeout { get; init; } = TimeSpan.FromSeconds(20);
    public TimeSpan DiscoveryTimeout { get; init; } = TimeSpan.FromSeconds(25);
    public TimeSpan ManifestTimeout { get; init; } = TimeSpan.FromSeconds(15);
    public TimeSpan OverallTimeout { get; init; } = TimeSpan.FromSeconds(90);
    /// <summary>Isolated profile per analysis; persistent only for
    /// authenticated workflows. Never the user's own browser profile.</summary>
    public bool PersistentProfile { get; init; } = false;
    /// <summary>Profile directory for this session. Null lets the manager use
    /// a temp directory (caller-owned cleanup still applies).</summary>
    public string? ProfileDir { get; init; }
    /// <summary>BrowserHost executable. Null auto-locates next to the app;
    /// tests point at the built host explicitly.</summary>
    public string? HostExePath { get; init; }
    public int MaxNetworkEvents { get; init; } = 10_000;
    public int MaxCandidates { get; init; } = 200;
    public int MaxBodyBytes { get; init; } = 1024 * 1024;
    public int MaxRedirects { get; init; } = 10;
    /// <summary>Whether to inject synthetic play clicks and unpause video
    /// elements across all frames during discovery.</summary>
    public bool TriggerAutoplay { get; init; } = true;
}
