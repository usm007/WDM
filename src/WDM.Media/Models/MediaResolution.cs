using System;
using System.Collections.Generic;

namespace WDM.Media;

/// <summary>Outcome of a resolution attempt. Never a bare ERROR — every state
/// maps to user-facing guidance.</summary>
public enum ResolutionStatus
{
    Resolved,
    NotFound,
    LoginRequired,
    DrmProtected,
    Disabled,
    Failed,
}

public enum MediaKind
{
    Unknown,
    Video,
    Audio,
    Playlist,
}

/// <summary>One downloadable option. Confidence is explainable: every bump
/// appends to Evidence (e.g. "video/* content-type", "og:video tag").</summary>
public sealed record MediaVariant
{
    public string Label { get; init; } = "";
    public string MediaUrl { get; init; } = "";
    /// <summary>yt-dlp format selector, when this variant came from yt-dlp.</summary>
    public string? FormatArg { get; init; }
    public long? EstimatedBytes { get; init; }
    public int? Width { get; init; }
    public int? Height { get; init; }
    public string? VideoCodec { get; init; }
    public string? AudioCodec { get; init; }
    public string? Container { get; init; }
    public double Confidence { get; init; }
    public List<string> Evidence { get; init; } = new();
    public bool RequiresHls { get; init; }
    public bool RequiresDash { get; init; }
    public string? ManifestUrl { get; init; }
}

/// <summary>What the page offers: ranked variants plus page-level context
/// (title, auth headers) shared by all of them.</summary>
public sealed record MediaResolution
{
    public string SourceUrl { get; init; } = "";
    public ResolutionStatus Status { get; init; } = ResolutionStatus.NotFound;
    public string? Title { get; init; }
    public string? ThumbnailUrl { get; init; }
    public TimeSpan? Duration { get; init; }
    public bool IsPlaylist { get; init; }
    public string? Message { get; init; }
    public Dictionary<string, string> Headers { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public string? Referer { get; init; }
    public List<MediaVariant> Variants { get; init; } = new();
}

/// <summary>Engine-facing handoff. The Download Engine consumes this object —
/// never resolver-specific dictionaries.</summary>
public sealed record MediaDownloadRequest
{
    public string SourcePage { get; init; } = "";
    public string MediaUrl { get; init; } = "";
    public MediaKind MediaKind { get; init; } = MediaKind.Unknown;
    public string? Container { get; init; }
    public string? Title { get; init; }
    public Dictionary<string, string> Headers { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public string? Referer { get; init; }
    public bool RequiresHls { get; init; }
    public bool RequiresDash { get; init; }
    public string? ManifestUrl { get; init; }
    public string? YouTubeFormatArg { get; init; }
    public string? YouTubeExtraArgs { get; init; }
}
