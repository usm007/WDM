using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using WDM.Browser.Contracts;
using WDM.Browser.Models;
using WDM.Browser.Sessions;
using WDM.Media;
using WDM.Services;

namespace WDM.Services;

/// <summary>LEVEL 4 — browser-assisted resolution. Last resort for pages whose
/// media only appears after JavaScript runs: load the URL in WDM's isolated
/// browser runtime, observe network traffic, expand discovered manifests.
/// Returns null when L4 is unavailable or adds nothing (caller falls back).
/// A renderer crash retries exactly once; cancellation never orphans hosts.</summary>
public static class BrowserResolver
{
    /// <summary>True when the browser runtime ships next to the app.</summary>
    public static bool IsAvailable() =>
        File.Exists(BrowserSessionManager.LocateHostExe(null));

    public static async Task<MediaResolution?> TryResolveAsync(
        string pageUrl, string? referer, IDictionary<string, string>? headers, CancellationToken ct)
    {
        // No availability gate here: the host factory may be a test fake.
        // A missing binary surfaces as FileNotFoundException from StartAsync
        // and maps to null below (caller falls back).
        string profileDir = Path.Combine(AppPaths.DataDir, "Browser", "Profiles", "temp", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(profileDir);
        var options = new BrowserSessionOptions
        {
            ProfileDir = profileDir,
            DiscoveryTimeout = TimeSpan.FromSeconds(20),
            OverallTimeout = TimeSpan.FromSeconds(90),
        };

        IBrowserHost host = MediaEnvironment.BrowserHostFactory();
        try
        {
            return await AttemptAsync(host, options, pageUrl, referer, ct).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex) when (IsCrash(ex))
        {
            // Renderer/host crash: restart once, then give up (§21).
            try { await host.DisposeAsync().ConfigureAwait(false); } catch { }
            IBrowserHost retry = MediaEnvironment.BrowserHostFactory();
            try
            {
                return await AttemptAsync(retry, options, pageUrl, referer, ct).ConfigureAwait(false);
            }
            catch (Exception retryEx) when (retryEx is not OperationCanceledException)
            {
                return null;
            }
            finally
            {
                try { await retry.DisposeAsync().ConfigureAwait(false); } catch { }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
        finally
        {
            try { await host.DisposeAsync().ConfigureAwait(false); } catch { }
            try { Directory.Delete(profileDir, recursive: true); } catch { }
        }
    }

    private static bool IsCrash(Exception ex) =>
        ex.Message.Contains("terminated", StringComparison.OrdinalIgnoreCase)
        || ex.Message.Contains("crash", StringComparison.OrdinalIgnoreCase);

    private static async Task<MediaResolution?> AttemptAsync(IBrowserHost host, BrowserSessionOptions options,
        string pageUrl, string? referer, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linked.CancelAfter(options.OverallTimeout);
        CancellationToken lct = linked.Token;

        await host.StartAsync(options, lct).ConfigureAwait(false);
        await using var session = await host.CreateSessionAsync(options, lct).ConfigureAwait(false);

        var events = new List<BrowserNetworkEvent>();
        await foreach (var evt in session.NavigateAsync(pageUrl, lct).ConfigureAwait(false))
        {
            events.Add(evt);
            if (events.Count >= options.MaxNetworkEvents)
                break;
        }

        string? title = null;
        try { title = await session.ExecuteScriptAsync("document.title", lct).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch { }
        if (string.IsNullOrWhiteSpace(title))
            title = null;

        var variants = new List<MediaVariant>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Direct media observed on the wire (content-type evidence, not extension).
        foreach (var evt in events)
        {
            if (IsManifest(evt) || !IsDirectMedia(evt) || !seen.Add(evt.Url))
                continue;
            var evidence = new List<string>
            {
                "browser network: " + evt.ResourceType + " " + (evt.ContentType ?? ""),
            };
            if (evt.ContentLength is long len && len > 0)
                evidence.Add("content-length: " + len);
            variants.Add(new MediaVariant
            {
                Label = FileNameFromUrl(evt.Url) ?? "Browser media",
                MediaUrl = evt.Url,
                Confidence = 0.85,
                Evidence = evidence,
                EstimatedBytes = evt.ContentLength is long l && l > 0 ? l : null,
            });
        }

        // Manifests: fetch in page context (cookies preserved), expand offline.
        foreach (var manifest in events.Where(IsManifest).Take(3))
        {
            if (!seen.Add("manifest:" + manifest.Url))
                continue;
            byte[]? body = null;
            try { body = await session.FetchBodyAsync(manifest.Url, lct).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
            catch { }
            if (body is null || body.Length == 0)
                continue;
            string text;
            try { text = Encoding.UTF8.GetString(body); } catch { continue; }
            bool dash = manifest.Url.EndsWith(".mpd", StringComparison.OrdinalIgnoreCase)
                || (manifest.ContentType?.Contains("dash", StringComparison.OrdinalIgnoreCase) == true);
            var (expanded, drm) = ManifestResolver.ExpandText(manifest.Url, text, dash);
            if (drm)
            {
                return new MediaResolution
                {
                    SourceUrl = pageUrl,
                    Status = ResolutionStatus.DrmProtected,
                    Title = title,
                    Message = "This media looks DRM-protected — WDM can't download protected streams.",
                };
            }
            foreach (var v in expanded)
            {
                v.Evidence.Add("browser network: manifest");
                if (seen.Add(v.MediaUrl))
                    variants.Add(v);
            }
        }

        if (variants.Count == 0)
            return null;

        var resolutionHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? cookie = events
            .Select(e => e.RequestHeaders.TryGetValue("Cookie", out string? c) ? c : null)
            .FirstOrDefault(c => !string.IsNullOrWhiteSpace(c));
        if (!string.IsNullOrWhiteSpace(cookie))
            resolutionHeaders["Cookie"] = cookie;

        return new MediaResolution
        {
            SourceUrl = pageUrl,
            Status = ResolutionStatus.Resolved,
            Title = title,
            Headers = resolutionHeaders,
            Referer = referer ?? pageUrl,
            Variants = ResolutionPipeline.Rank(variants),
        };
    }

    private static bool IsManifest(BrowserNetworkEvent e)
    {
        if (e.Url.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase)
            || e.Url.EndsWith(".mpd", StringComparison.OrdinalIgnoreCase))
            return true;
        string ct = e.ContentType ?? "";
        return ct.Contains("mpegurl") || ct.Contains("m3u8") || ct.Contains("x-mpegurl")
            || ct.Contains("dash+xml") || ct.Contains("ms-sstr");
    }

    private static bool IsDirectMedia(BrowserNetworkEvent e)
    {
        string ct = e.ContentType ?? "";
        if (IsManifest(e))
            return false;
        return ct.StartsWith("video/", StringComparison.OrdinalIgnoreCase)
            || ct.StartsWith("audio/", StringComparison.OrdinalIgnoreCase);
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
