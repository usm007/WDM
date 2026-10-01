using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using WDM.Media;
using WDM.Services.Embed;

namespace WDM.Services;

/// <summary>Single media-resolution entry point. Staged cheapest-first:
/// L0 direct probe → L1 static HTML → L3 manifest cue → L2a embed → L2b yt-dlp.
/// First level with a confident hit wins; weaker hits accumulate, merge and
/// rank. Browser-assisted resolution (L4) plugs in here later.</summary>
public static class ResolutionPipeline
{
    /// <summary>Resolve what a page URL offers. Throws only for hard failures
    /// (callers keep their legacy fallbacks); classified misses come back as
    /// statuses. EmbedInteractionRequiredException always bubbles (the engine
    /// turns it into the solve-in-browser flow).</summary>
    public static async Task<MediaResolution> ResolveAsync(
        string url, CancellationToken ct, string? referer = null,
        IDictionary<string, string>? headers = null)
    {
        string pageUrl = (url ?? "").Trim();
        if (!Uri.TryCreate(pageUrl, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return new MediaResolution { SourceUrl = pageUrl, Status = ResolutionStatus.Failed, Message = "Not a web address." };
        }

        var settings = MediaSettings.Current;
        bool isYoutube = YouTubeResolver.IsYoutubeUrl(pageUrl);
        if (!settings.EnableMediaFetching && !isYoutube)
        {
            return new MediaResolution
            {
                SourceUrl = pageUrl,
                Status = ResolutionStatus.Disabled,
                Message = "Media fetching is disabled (except YouTube) — enable it in Options.",
            };
        }

        var acc = new List<MediaVariant>();
        string? title = null;
        var mergedHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (headers is not null)
        {
            foreach (var kv in headers)
                mergedHeaders[kv.Key] = kv.Value;
        }
        string? resolvedReferer = referer;
        var levelTokens = new List<CancellationTokenSource>();
        CancellationToken Lvl(int seconds) => LevelCt(ct, seconds, levelTokens);

        using var http = MediaHttp.CreateClient();
        try
        {
            // L0 — direct media or manifest serve.
            var direct = await DirectMediaProbe.ProbeAsync(http, pageUrl, Lvl(10)).ConfigureAwait(false);
            if (direct is not null)
            {
                if (direct.ManifestUrl is null)
                    return Resolved(pageUrl, title, mergedHeaders, resolvedReferer, new List<MediaVariant> { direct });
                var (expanded, drm) = await ManifestResolver.ExpandAsync(
                    http, direct.ManifestUrl, direct.RequiresDash, null, null, Lvl(15)).ConfigureAwait(false);
                if (drm)
                    return Drm(pageUrl);
                if (expanded.Count > 0)
                    return Resolved(pageUrl, title, mergedHeaders, resolvedReferer, expanded);
                return Resolved(pageUrl, title, mergedHeaders, resolvedReferer, new List<MediaVariant> { direct });
            }

            // L1 — static page inspection (skip for YouTube: yt-dlp owns it).
            if (!isYoutube)
            {
                var hit = await StaticPageDetector.DetectAsync(http, pageUrl, Lvl(15)).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(hit.Title))
                    title = hit.Title;
                acc.AddRange(hit.Candidates);
            }

            // L3 cue — manifest-shaped URL jumps the queue.
            if (IsManifestUrl(pageUrl))
            {
                bool dash = pageUrl.EndsWith(".mpd", StringComparison.OrdinalIgnoreCase);
                var (expanded, drm) = await ManifestResolver.ExpandAsync(
                    http, pageUrl, dash, null, null, Lvl(15)).ConfigureAwait(false);
                if (drm)
                    return Drm(pageUrl);
                if (expanded.Count > 0)
                    return Resolved(pageUrl, title, mergedHeaders, resolvedReferer, expanded);
            }

            // L2a — embed/player extractor.
            if (!isYoutube && EmbedResolver.IsEmbedCandidate(pageUrl))
            {
                var embed = await EmbedResolver.TryResolveAsync(pageUrl, referer, headers, Lvl(30)).ConfigureAwait(false);
                if (embed is not null)
                {
                    foreach (var kv in embed.Headers)
                        mergedHeaders[kv.Key] = kv.Value;
                    if (!string.IsNullOrWhiteSpace(embed.Referer))
                        resolvedReferer = embed.Referer;
                    if (string.IsNullOrWhiteSpace(title))
                        title = embed.Title;
                    if (embed.IsHls)
                    {
                        var (expanded, drm) = await ManifestResolver.ExpandAsync(
                            http, embed.DirectUrl, false, resolvedReferer, mergedHeaders, Lvl(15)).ConfigureAwait(false);
                        if (drm)
                            return Drm(pageUrl);
                        foreach (var v in expanded)
                            acc.Add(v);
                    }
                    if (acc.Count == 0 || !embed.IsHls)
                    {
                        acc.Add(new MediaVariant
                        {
                            Label = !string.IsNullOrWhiteSpace(embed.Title) ? embed.Title
                                : FileNameFromUrl(embed.DirectUrl) ?? "Best quality (direct)",
                            MediaUrl = embed.DirectUrl,
                            ManifestUrl = embed.IsHls ? embed.DirectUrl : null,
                            RequiresHls = embed.IsHls,
                            Container = embed.IsHls ? "ts" : null,
                            Confidence = 0.8,
                            Evidence = new List<string> { "embed player" },
                        });
                    }
                    return Resolved(pageUrl, title, mergedHeaders, resolvedReferer, acc);
                }
            }

            // L2b — yt-dlp (YouTube and cooperating sites).
            if (isYoutube)
            {
                var q = await YouTubeResolver.ResolveAsync(pageUrl, ct).ConfigureAwait(false);
                return MapYouTube(pageUrl, q);
            }

            // L4 — browser-assisted resolution (misses only, never YouTube).
            // Runs the page in WDM's isolated runtime and observes traffic.
            // Positive detections (media, DRM, login) win; soft misses fall
            // through to earlier candidates.
            if (!isYoutube)
            {
                MediaResolution? browser = null;
                try
                {
                    browser = await BrowserResolver.TryResolveAsync(pageUrl, referer, headers, ct)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch
                {
                    browser = null;
                }
                if (browser is not null
                    && (browser.Status == ResolutionStatus.DrmProtected
                        || browser.Status == ResolutionStatus.LoginRequired))
                    return browser;
                if (browser is not null && browser.Status == ResolutionStatus.Resolved
                    && browser.Variants.Count > 0)
                {
                    if (string.IsNullOrWhiteSpace(title))
                        title = browser.Title;
                    acc.AddRange(browser.Variants);
                    return Resolved(pageUrl, title, mergedHeaders, resolvedReferer, acc);
                }
            }

            if (acc.Count > 0)
                return Resolved(pageUrl, title, mergedHeaders, resolvedReferer, acc);

            return new MediaResolution
            {
                SourceUrl = pageUrl,
                Status = ResolutionStatus.NotFound,
                Title = title,
                Message = "No downloadable media found on this page.",
            };
        }
        catch (EmbedInteractionRequiredException)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (YtDlpException ex)
        {
            if (IsSignInError(ex.Message))
            {
                return new MediaResolution
                {
                    SourceUrl = pageUrl,
                    Status = ResolutionStatus.LoginRequired,
                    Title = title,
                    Message = "This page needs a login before WDM can list its media.",
                };
            }
            throw;
        }
        catch (Exception ex)
        {
            return new MediaResolution
            {
                SourceUrl = pageUrl,
                Status = ResolutionStatus.Failed,
                Title = title,
                Message = ex.Message,
            };
        }
        finally
        {
            foreach (var t in levelTokens)
            {
                try { t.Dispose(); } catch { }
            }
        }
    }

    /// <summary>Best variant (or the given one) as an engine handoff.</summary>
    public static MediaDownloadRequest ToDownloadRequest(MediaResolution res, MediaVariant? variant = null)
    {
        var v = variant ?? res.Variants.FirstOrDefault();
        var req = new MediaDownloadRequest
        {
            SourcePage = res.SourceUrl,
            Title = res.Title,
            Headers = new Dictionary<string, string>(res.Headers, StringComparer.OrdinalIgnoreCase),
            Referer = res.Referer,
        };
        if (v is null)
            return req;
        bool audio = v.Label.Contains("Audio Only", StringComparison.OrdinalIgnoreCase)
            || v.FormatArg?.Contains("audio", StringComparison.OrdinalIgnoreCase) == true;
        return req with
        {
            MediaUrl = v.MediaUrl,
            MediaKind = res.IsPlaylist ? MediaKind.Playlist : audio ? MediaKind.Audio : MediaKind.Video,
            Container = v.Container,
            RequiresHls = v.RequiresHls,
            RequiresDash = v.RequiresDash,
            ManifestUrl = v.ManifestUrl,
            YouTubeFormatArg = v.FormatArg,
        };
    }

    internal static bool IsSignInError(string msg) =>
        msg.IndexOf("Sign in to confirm", StringComparison.OrdinalIgnoreCase) >= 0
        || msg.IndexOf("not a bot", StringComparison.OrdinalIgnoreCase) >= 0
        || msg.IndexOf("cookies-from-browser", StringComparison.OrdinalIgnoreCase) >= 0;

    private static bool IsManifestUrl(string url)
    {
        try
        {
            string path = new Uri(url).AbsolutePath;
            return path.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(".mpd", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static MediaResolution Resolved(string pageUrl, string? title,
        Dictionary<string, string> headers, string? referer, List<MediaVariant> variants) =>
        new()
        {
            SourceUrl = pageUrl,
            Status = ResolutionStatus.Resolved,
            Title = title,
            Headers = headers,
            Referer = referer,
            Variants = Rank(variants),
        };

    private static MediaResolution Drm(string pageUrl) =>
        new()
        {
            SourceUrl = pageUrl,
            Status = ResolutionStatus.DrmProtected,
            Message = "This media looks DRM-protected — WDM can't download protected streams.",
        };

    internal static List<MediaVariant> Rank(List<MediaVariant> variants)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var deduped = new List<MediaVariant>();
        foreach (var v in variants
            .OrderByDescending(v => v.Confidence)
            .ThenByDescending(v => v.Height ?? 0)
            .ThenByDescending(v => v.EstimatedBytes ?? 0))
        {
            if (string.IsNullOrWhiteSpace(v.MediaUrl) || !seen.Add(v.MediaUrl))
                continue;
            deduped.Add(v);
        }
        return deduped;
    }

    private static MediaResolution MapYouTube(string pageUrl, ResolvedQuery q)
    {
        var first = q.Items.FirstOrDefault();
        var variants = new List<MediaVariant>();
        if (!q.IsPlaylist && first is not null)
        {
            foreach (var opt in q.QualityOptions)
            {
                variants.Add(new MediaVariant
                {
                    Label = opt.Label,
                    MediaUrl = first.Url,
                    FormatArg = opt.FormatArg,
                    EstimatedBytes = opt.EstimatedBytes,
                    Confidence = 0.85,
                    Evidence = new List<string> { "yt-dlp formats" },
                });
            }
        }
        return new MediaResolution
        {
            SourceUrl = pageUrl,
            Status = variants.Count > 0 || q.IsPlaylist ? ResolutionStatus.Resolved : ResolutionStatus.NotFound,
            Title = q.IsPlaylist ? q.PlaylistTitle : first?.Title,
            ThumbnailUrl = first?.ThumbnailUrl,
            Duration = first?.Duration,
            IsPlaylist = q.IsPlaylist,
            Variants = Rank(variants),
            Message = q.IsPlaylist ? null : variants.Count == 0 ? "No playable formats found." : null,
        };
    }
    private static CancellationToken LevelCt(CancellationToken ct, int seconds, List<CancellationTokenSource> owned)
    {
        // Best-effort per-level budget; the caller's token still cancels everything.
        var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linked.CancelAfter(TimeSpan.FromSeconds(seconds));
        owned.Add(linked);
        return linked.Token;
    }

    private static string? FileNameFromUrl(string url)
    {
        try
        {
            string name = System.IO.Path.GetFileName(new Uri(url).AbsolutePath);
            try { name = Uri.UnescapeDataString(name); } catch { }
            return string.IsNullOrWhiteSpace(name) ? null : name;
        }
        catch
        {
            return null;
        }
    }
}
