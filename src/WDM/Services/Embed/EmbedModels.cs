using System;
using System.Collections.Generic;

namespace WDM.Services.Embed;

/// <summary>A direct stream URL discovered from an embed/player page.</summary>
public sealed class StreamCandidate
{
    public string Url { get; set; } = "";
    /// <summary>HLS, DASH or Video.</summary>
    public string Kind { get; set; } = "Video";
    /// <summary>Higher wins when several candidates are found.</summary>
    public int Score { get; set; }
    /// <summary>Extra headers required to fetch the stream (Referer/Origin/Cookie).</summary>
    public Dictionary<string, string> Headers { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>Result of resolving an embed page to a directly downloadable stream.</summary>
public sealed class ResolvedEmbed
{
    public string DirectUrl { get; set; } = "";
    /// <summary>The original player page URL (for re-resolving expiring links).</summary>
    public string SourcePageUrl { get; set; } = "";
    public string? Title { get; set; }
    public string? ThumbnailUrl { get; set; }
    public bool IsHls { get; set; }
    public string? Referer { get; set; }
    public Dictionary<string, string> Headers { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>Thrown when the page demands human interaction (captcha, Cloudflare
/// Turnstile/managed challenge, device attestation, login). The caller must notify
/// the user and open the page in the embedded browser instead of retrying.</summary>
public sealed class EmbedInteractionRequiredException : Exception
{
    public string PageUrl { get; }
    public EmbedInteractionRequiredException(string pageUrl, string reason)
        : base(reason)
    {
        PageUrl = pageUrl;
    }
}
