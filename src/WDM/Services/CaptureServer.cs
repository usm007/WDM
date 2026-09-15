using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WDM.Services;

public sealed class CaptureServer : IDisposable
{
    public const int Port = 17530;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };
    private static readonly JsonSerializerOptions JsonWriteOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
    private readonly TcpListener _listener;
    private readonly Action<string, string?, string?, Dictionary<string, string>, string?> _onCapture;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _throttle = new(20, 20);
    private bool _running;

    public bool IsConnected { get; private set; }
    public event Action? ExtensionConnected;

    public CaptureServer(Action<string, string?, string?, Dictionary<string, string>, string?> onCapture)
    {
        _onCapture = onCapture;
        _listener = new TcpListener(IPAddress.Loopback, Port);
    }

    public void Start()
    {
        try
        {
            _listener.Start();
            _running = true;
            _ = Task.Run(AcceptLoopAsync);
        }
        catch (Exception)
        {
            // Port unavailable or already bound by another instance
            _running = false;
        }
    }

    private async Task AcceptLoopAsync()
    {
        while (_running)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_cts.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                break;
            }
            _ = Task.Run(() => HandleClientAsync(client));
        }
    }

    private async Task HandleClientAsync(TcpClient client)
    {
        bool acquired = false;
        try
        {
            await _throttle.WaitAsync(_cts.Token).ConfigureAwait(false);
            acquired = true;
        }
        catch
        {
            client.Dispose();
            return;
        }
        using (client)
        {
            try
            {
                // Slow-loris guard: loopback only, but 20 wedged slots would
                // still lock the extension out. Time out idle reads/writes.
                try
                {
                    client.ReceiveTimeout = 15000;
                    client.SendTimeout = 15000;
                }
                catch { }
                using var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);

                string? line = await reader.ReadLineAsync();
                if (line is null)
                    return;
                // Uncapped request line: reject absurdly long targets before parsing.
                if (line.Length > 8192)
                {
                    await WriteResponseAsync(stream, HttpStatusCode.RequestUriTooLong, "{\"error\":\"request line too long\"}", null);
                    return;
                }
                var parts = line.Split(' ');
                if (parts.Length < 2)
                    return;
                string method = parts[0];
                string fullPath = parts[1];

                // Split path from query string
                string path = fullPath;
                string queryString = "";
                int qIdx = fullPath.IndexOf('?');
                if (qIdx >= 0)
                {
                    path = fullPath[..qIdx];
                    queryString = fullPath[(qIdx + 1)..];
                }

                long contentLength = 0;
                bool expectContinue = false;
                string? origin = null;
                string? authToken = null;
                int headerCount = 0;
                while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync()))
                {
                    if (++headerCount > 100)
                    {
                        await WriteResponseAsync(stream, HttpStatusCode.BadRequest, "{\"error\":\"too many headers\"}", origin);
                        return;
                    }
                    var colon = line.IndexOf(':');
                    if (colon <= 0)
                        continue;
                    string name = line[..colon].Trim();
                    string value = line[(colon + 1)..].Trim();
                    if (value.Length > 4096)
                        value = value[..4096];
                    if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                    {
                        long.TryParse(value, out contentLength);
                        if (contentLength < 0)
                            contentLength = 0;
                    }
                    else if (name.Equals("Expect", StringComparison.OrdinalIgnoreCase))
                        expectContinue = value.Contains("100-continue", StringComparison.OrdinalIgnoreCase);
                    else if (name.Equals("Origin", StringComparison.OrdinalIgnoreCase))
                        origin = value;
                    else if (name.Equals(CaptureAuth.HeaderName, StringComparison.OrdinalIgnoreCase) ||
                             name.Equals("Authorization", StringComparison.OrdinalIgnoreCase))
                    {
                        // First non-empty wins: an empty header must not mask a
                        // valid token sent under the other name (BUG-019).
                        // A wrong token is never overridden — validated strictly.
                        if (string.IsNullOrWhiteSpace(authToken) && !string.IsNullOrWhiteSpace(value))
                            authToken = value;
                    }
                }

                if (contentLength > 10 * 1024 * 1024)
                {
                    await WriteResponseAsync(stream, HttpStatusCode.RequestEntityTooLarge, "Payload Too Large", origin);
                    return;
                }

                if (expectContinue)
                {
                    await WriteRawAsync(stream, "HTTP/1.1 100 Continue\r\n\r\n");
                }

                string body = "";
                if (contentLength > 0)
                {
                    int len = (int)Math.Min(contentLength, 10 * 1024 * 1024);
                    var buffer = new char[len];
                    int read = 0;
                    while (read < buffer.Length)
                    {
                        int n = await reader.ReadBlockAsync(buffer, read, buffer.Length - read);
                        if (n == 0)
                            break;
                        read += n;
                    }
                    body = new string(buffer, 0, read);
                }

                if (method == "OPTIONS")
                {
                    await WriteResponseAsync(stream, HttpStatusCode.NoContent, "", origin);
                    return;
                }

                if (method == "GET" && path == "/ping")
                {
                    // Presence probe: do not trust it for security decisions.
                    string ver = typeof(CaptureServer).Assembly.GetName().Version?.ToString(3) ?? "2.7.2";
                    await WriteResponseAsync(stream, HttpStatusCode.OK, $"{{\"status\":\"ok\",\"version\":\"{ver}\"}}", origin);
                    return;
                }

                if (method == "POST" && path == "/download")
                {
                    // Reject CSRF from arbitrary websites: only the extension
                    // (chrome-/moz-extension origin or no Origin like background
                    // fetch) may drive downloads. Web pages send http(s) Origin.
                    if (IsBrowserWebOrigin(origin))
                    {
                        await WriteResponseAsync(stream, HttpStatusCode.Forbidden, "{\"error\":\"forbidden origin\"}", origin);
                        return;
                    }
                    if (!IsAuthorized(origin, authToken))
                    {
                        await WriteResponseAsync(stream, HttpStatusCode.Unauthorized, "{\"error\":\"unauthorized — update the WDM browser extension (Settings > Extension)\"}", origin);
                        return;
                    }
                    // A valid install token proves the paired extension is driving
                    // (user intent), so LAN/NAS/dev-server URLs stay legal there.
                    // Token-less grace callers keep the private-target block (BUG-020).
                    bool authed = CaptureAuth.Validate(authToken);
                    try
                    {
                        var payload = JsonSerializer.Deserialize<CapturePayload>(body, JsonOptions);
                        if (payload is null || string.IsNullOrWhiteSpace(payload.Url))
                            throw new InvalidOperationException("Empty url");
                        string url = payload.Url.Trim();
                        if (url.Length > 2048 || !IsAllowedCaptureUrl(url) || (!authed && IsBlockedResolveTarget(url)))
                            throw new InvalidOperationException("Bad url");
                        string? fileName = SanitizeCaptureFileName(payload.FileName);
                        string? referer = SanitizeCaptureUrl(payload.Referer, 2048, authed);
                        string? pageTitle = payload.PageTitle is null ? null :
                            payload.PageTitle.Trim().Length > 500 ? payload.PageTitle.Trim()[..500] : payload.PageTitle.Trim();
                        if (string.IsNullOrWhiteSpace(pageTitle))
                            pageTitle = null;
                        IsConnected = true;
                        ExtensionConnected?.Invoke();
                        var headers = SanitizeCaptureHeaders(payload.Headers);
                        // Preserve the extension's stream classification so the engine can
                        // force HLS/DASH routing even for tokenized manifests without a
                        // literal .m3u8/.mpd in the URL. "page" means the URL is a player
                        // page that still needs embed resolution, not a direct download.
                        if (!string.IsNullOrWhiteSpace(payload.StreamType) &&
                            (payload.StreamType.Equals("HLS", StringComparison.OrdinalIgnoreCase) ||
                             payload.StreamType.Equals("DASH", StringComparison.OrdinalIgnoreCase) ||
                             payload.StreamType.Equals("Stream", StringComparison.OrdinalIgnoreCase) ||
                             payload.StreamType.Equals("page", StringComparison.OrdinalIgnoreCase)) &&
                            !headers.ContainsKey("X-WDM-StreamType"))
                        {
                            headers["X-WDM-StreamType"] = payload.StreamType;
                        }
                        // Player CDNs often gate on Origin; derive it from the Referer
                        // when the content script didn't supply one.
                        if (!headers.ContainsKey("Origin") &&
                            !string.IsNullOrWhiteSpace(referer) &&
                            Uri.TryCreate(referer, UriKind.Absolute, out var refererUri) &&
                            (refererUri.Scheme == Uri.UriSchemeHttp || refererUri.Scheme == Uri.UriSchemeHttps))
                        {
                            headers["Origin"] = refererUri.GetLeftPart(UriPartial.Authority);
                        }
                        // Keep explicit VideoUrl/AudioUrl hints (used by refresh flows)
                        // reachable downstream via headers when the payload URL is a page.
                        string? videoHint = SanitizeCaptureUrl(payload.VideoUrl, 2048, authed);
                        string? audioHint = SanitizeCaptureUrl(payload.AudioUrl, 2048, authed);
                        if (!string.IsNullOrWhiteSpace(videoHint) && !headers.ContainsKey("X-WDM-VideoUrl"))
                            headers["X-WDM-VideoUrl"] = videoHint;
                        if (!string.IsNullOrWhiteSpace(audioHint) && !headers.ContainsKey("X-WDM-AudioUrl"))
                            headers["X-WDM-AudioUrl"] = audioHint;
                        // Carry the page title for filename recovery: manifest URLs
                        // ("master.m3u8") carry no title, so the engine falls back to
                        // this when the prefill name is still generic.
                        if (!string.IsNullOrWhiteSpace(pageTitle) && !headers.ContainsKey("X-WDM-PageTitle"))
                            headers["X-WDM-PageTitle"] = pageTitle;
                        _onCapture(url, fileName, referer, headers, pageTitle);
                        await WriteResponseAsync(stream, HttpStatusCode.OK, "{\"accepted\":true}", origin);
                    }
                    catch
                    {
                        await WriteResponseAsync(stream, HttpStatusCode.BadRequest, "{\"error\":\"invalid request\"}", origin);
                    }
                    return;
                }

                // GET /resolve?url=<encoded-url>
                // Returns available quality tiers for a YouTube (or any yt-dlp-supported) URL.
                if (method == "GET" && path == "/resolve")
                {
                    if (IsBrowserWebOrigin(origin))
                    {
                        await WriteResponseAsync(stream, HttpStatusCode.Forbidden, "{\"error\":\"forbidden origin\"}", origin);
                        return;
                    }
                    if (!IsAuthorized(origin, authToken))
                    {
                        await WriteResponseAsync(stream, HttpStatusCode.Unauthorized, "{\"error\":\"unauthorized — update the WDM browser extension (Settings > Extension)\"}", origin);
                        return;
                    }
                    string? videoUrl = null;
                    if (queryString.Length <= 4096)
                    {
                        foreach (var pair in queryString.Split('&'))
                        {
                            var kv = pair.Split('=', 2);
                            if (kv.Length == 2 && kv[0].Equals("url", StringComparison.OrdinalIgnoreCase))
                            {
                                try { videoUrl = Uri.UnescapeDataString(kv[1]); }
                                catch { videoUrl = null; }
                                break;
                            }
                        }
                    }

                    if (string.IsNullOrWhiteSpace(videoUrl) || videoUrl.Length > 2048 ||
                        !IsAllowedCaptureUrl(videoUrl.Trim()) || IsBlockedResolveTarget(videoUrl.Trim()))
                    {
                        await WriteResponseAsync(stream, HttpStatusCode.BadRequest, "{\"error\":\"missing url param\"}", origin);
                        return;
                    }

                    try
                    {
                        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                        // Embed/player pages resolve via the generic embed pipeline;
                        // everything else keeps the yt-dlp path.
                        if (Embed.EmbedResolver.IsEmbedCandidate(videoUrl) && !MediaResolver.IsYoutubeUrl(videoUrl))
                        {
                            var embed = await Embed.EmbedResolver.TryResolveAsync(videoUrl, null, null, cts.Token);
                            if (embed is null)
                                throw new InvalidOperationException("Could not resolve an embed stream from this page.");
                            var embedObj = new ResolveResponse
                            {
                                Title = embed.Title ?? DownloadEngine.DeriveName(embed.DirectUrl),
                                Channel = "",
                                ThumbnailUrl = "",
                                IsPlaylist = false,
                                ItemCount = 1,
                                DirectUrl = embed.DirectUrl,
                                StreamType = embed.IsHls ? "HLS" : "Video",
                                Qualities = new List<QualityResponse>
                                {
                                    new() { Label = embed.IsHls ? "HLS stream" : "Best quality (direct)", FormatArg = "direct" },
                                },
                            };
                            string embedJson = JsonSerializer.Serialize(embedObj, JsonWriteOptions);
                            await WriteResponseAsync(stream, HttpStatusCode.OK, embedJson, origin);
                            return;
                        }
                        var resolved = await MediaResolver.ResolveAsync(videoUrl, cts.Token);

                        var responseObj = new ResolveResponse
                        {
                            Title = resolved.Items.FirstOrDefault()?.Title ?? "",
                            Channel = resolved.Items.FirstOrDefault()?.Channel ?? "",
                            ThumbnailUrl = resolved.Items.FirstOrDefault()?.ThumbnailUrl ?? "",
                            IsPlaylist = resolved.IsPlaylist,
                            PlaylistTitle = resolved.PlaylistTitle,
                            ItemCount = resolved.Items.Count,
                            Qualities = resolved.QualityOptions.Select(q => new QualityResponse
                            {
                                Label = q.Label,
                                FormatArg = q.FormatArg,
                                EstimatedBytes = q.EstimatedBytes,
                                EstimatedSizeText = q.EstimatedBytes.HasValue
                                    ? FormatBytes(q.EstimatedBytes.Value)
                                    : null,
                            }).ToList(),
                        };

                        string json = JsonSerializer.Serialize(responseObj, JsonWriteOptions);
                        await WriteResponseAsync(stream, HttpStatusCode.OK, json, origin);
                    }
                    catch (Exception)
                    {
                        // Fallback response with default quality options when yt-dlp analysis fails/times out
                        var fallbackObj = new ResolveResponse
                        {
                            Title = "YouTube Video",
                            Qualities = new List<QualityResponse>
                            {
                                new() { Label = "1080p (Full HD)", FormatArg = "bestvideo[height<=1080]+bestaudio/best" },
                                new() { Label = "720p (HD)", FormatArg = "bestvideo[height<=720]+bestaudio/best" },
                                new() { Label = "480p", FormatArg = "bestvideo[height<=480]+bestaudio/best" },
                                new() { Label = "360p", FormatArg = "bestvideo[height<=360]+bestaudio/best" },
                                new() { Label = "Audio Only (MP3)", FormatArg = "bestaudio/best" },
                            }
                        };
                        string fallbackJson = JsonSerializer.Serialize(fallbackObj, JsonWriteOptions);
                        await WriteResponseAsync(stream, HttpStatusCode.OK, fallbackJson, origin);
                    }
                    return;
                }

                await WriteResponseAsync(stream, HttpStatusCode.NotFound, "", origin);
            }
            catch
            {
                // Client hung up mid-request; nothing to do.
            }
            finally
            {
                if (acquired)
                {
                    try { _throttle.Release(); } catch (ObjectDisposedException) { }
                }
            }
        }
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB" };
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return unit == 0 ? $"{value:0} {units[unit]}" : $"{value:0.0} {units[unit]}";
    }

    private static async Task WriteResponseAsync(Stream stream, HttpStatusCode status, string body, string? requestOrigin = null)
    {
        byte[] bodyBytes = Encoding.UTF8.GetBytes(body);
        // Never blanket-trust the web: only echo extension origins.
        // Strip control characters so a crafted Origin can never split the
        // response (response-splitting hygiene; SanitizeCaptureHeaders already
        // drops CR/LF in header *values*, this covers the echoed origin).
        string? safeOrigin = requestOrigin?.Trim();
        if (!string.IsNullOrEmpty(safeOrigin))
            safeOrigin = new string(safeOrigin.Where(c => !char.IsControl(c)).ToArray());
        string allowOrigin = IsAllowedExtensionOrigin(safeOrigin) ? safeOrigin! : "null";
        string headers =
            $"HTTP/1.1 {(int)status} {status}\r\n" +
            "Content-Type: application/json\r\n" +
            $"Access-Control-Allow-Origin: {allowOrigin}\r\n" +
            "Access-Control-Allow-Methods: POST, GET, OPTIONS\r\n" +
            $"Access-Control-Allow-Headers: Content-Type, {CaptureAuth.HeaderName}, Authorization\r\n" +
            "Vary: Origin\r\n" +
            $"Content-Length: {bodyBytes.Length}\r\n" +
            "Connection: close\r\n\r\n";
        byte[] headerBytes = Encoding.UTF8.GetBytes(headers);
        await stream.WriteAsync(headerBytes);
        await stream.WriteAsync(bodyBytes);
        await stream.FlushAsync();
    }

    private static async Task WriteRawAsync(Stream stream, string text)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        await stream.WriteAsync(bytes);
        await stream.FlushAsync();
    }

    public void Dispose()
    {
        _running = false;
        _cts.Cancel();
        try
        {
            _listener.Stop();
        }
        catch
        {
            // Ignore.
        }
        _cts.Dispose();
        _throttle.Dispose();
    }

    private static bool IsBrowserWebOrigin(string? origin)
    {
        if (string.IsNullOrWhiteSpace(origin))
            return false;
        origin = origin.Trim();
        return origin.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
               origin.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsAllowedExtensionOrigin(string? origin)
    {
        if (string.IsNullOrWhiteSpace(origin))
            return false;
        origin = origin.Trim();
        return origin.StartsWith("chrome-extension://", StringComparison.OrdinalIgnoreCase) ||
               origin.StartsWith("moz-extension://", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Loopback authorization for state-changing capture endpoints.
    /// A valid install token always passes; a wrong token never does. A missing
    /// token is accepted only from extension Origins (migration grace for
    /// extension copies deployed before the token existed). Bare loopback
    /// clients (curl, scripts — no token, no extension Origin) are rejected.</summary>
    private static bool IsAuthorized(string? origin, string? authToken)
    {
        if (!string.IsNullOrWhiteSpace(authToken))
            return CaptureAuth.Validate(authToken);
        return IsAllowedExtensionOrigin(origin);
    }

    private static bool IsAllowedCaptureUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return false;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            return false;
        if (string.IsNullOrWhiteSpace(uri.Host))
            return false;
        return true;
    }

    private static string? SanitizeCaptureUrl(string? url, int maxLen, bool allowPrivate = false)
    {
        if (string.IsNullOrWhiteSpace(url))
            return null;
        url = url.Trim();
        if (url.Length > maxLen)
            return null;
        if (!IsAllowedCaptureUrl(url) || (!allowPrivate && IsBlockedResolveTarget(url)))
            return null;
        return url;
    }

    private static string? SanitizeCaptureFileName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;
        name = name.Trim();
        if (name.Length > 255)
            name = name[..255];
        // Strip any path: traversal, absolute paths, separators.
        name = name.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
        try { name = Path.GetFileName(name) ?? ""; }
        catch { return null; }
        name = name.Trim().Trim('.');
        if (string.IsNullOrWhiteSpace(name))
            return null;
        foreach (char c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return string.IsNullOrWhiteSpace(name) ? null : name;
    }

    private static Dictionary<string, string> SanitizeCaptureHeaders(Dictionary<string, string>? input)
    {
        var output = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (input is null)
            return output;
        // Allow-list: downstream engine only needs these. Cookies are replayed
        // for authenticated downloads, but cap count/size to bound abuse.
        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "User-Agent", "Referer", "Origin", "Cookie",
            "X-WDM-StreamType", "X-WDM-VideoUrl", "X-WDM-AudioUrl", "X-WDM-PageTitle",
        };
        int count = 0;
        foreach (var kv in input)
        {
            if (count >= 20)
                break;
            if (string.IsNullOrWhiteSpace(kv.Key) || kv.Value is null)
                continue;
            string key = kv.Key.Trim();
            if (!allowed.Contains(key))
                continue;
            string val = kv.Value.Trim();
            if (val.Length > 8192)
                val = val[..8192];
            if (val.Contains('\r') || val.Contains('\n'))
                continue;
            output[key] = val;
            count++;
        }
        return output;
    }

    internal static bool IsBlockedResolveTarget(string url)
    {
        try
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || string.IsNullOrWhiteSpace(uri.Host))
                return true;
            string host = uri.Host.Trim().Trim('.').ToLowerInvariant();
            if (host == "localhost" || host.EndsWith(".local", StringComparison.Ordinal) ||
                host.EndsWith(".localhost", StringComparison.Ordinal) || host == "metadata.google.internal")
                return true;
            if (IPAddress.TryParse(host.Trim('[', ']'), out var ip))
            {
                // IPv4-mapped IPv6 (::ffff:127.0.0.1) must be judged as the
                // IPv4 it routes to — otherwise the v6 branch misses it.
                if (ip.IsIPv4MappedToIPv6)
                {
                    try { ip = ip.MapToIPv4(); }
                    catch { return true; }
                }
                if (IPAddress.IsLoopback(ip))
                    return true;
                if (ip.AddressFamily == AddressFamily.InterNetwork)
                {
                    byte[] b = ip.GetAddressBytes();
                    if (b[0] == 10) return true;
                    if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return true;
                    if (b[0] == 192 && b[1] == 168) return true;
                    if (b[0] == 169 && b[1] == 254) return true;
                    if (b[0] == 0 || b[0] >= 224) return true;
                }
                else
                {
                    byte[] b = ip.GetAddressBytes();
                    // Unspecified :: (all zeros) is not publicly routable — block.
                    if (b.Length == 16 && b.All(x => x == 0)) return true;
                    // Unique-local fc00::/7 (not covered by the obsolete
                    // SiteLocal flag), link-local fe80::/10, multicast ff00::/8.
                    if (b.Length == 16 && (b[0] & 0xFE) == 0xFC) return true;
                    if (ip.IsIPv6SiteLocal || ip.IsIPv6LinkLocal || ip.IsIPv6Multicast)
                        return true;
                }
            }
            else if (LooksLikeNumericIp(host))
            {
                // Non-canonical IPv4 the parser rejects but stacks still route
                // (0x7f.1, 2130706433, 0177.0.0.1): fail closed. Plain hostnames
                // contain letters/hyphens and never match this shape.
                return true;
            }
            else if (ResolvesToBlockedAddress(host))
            {
                // DNS-rebinding guard: a public hostname that resolves to
                // loopback/LAN/link-local space is blocked even though the
                // literal string looks innocent. DNS failures fail open here
                // (the downstream fetch will fail on its own).
                return true;
            }
            return false;
        }
        catch
        {
            return true;
        }
    }

    /// <summary>True when a hostname resolves to a blocked (non-public) address.
    /// Best-effort, fails open on DNS errors. Results are cached 5 minutes so
    /// per-redirect/per-hop checks don't pay a lookup each time.</summary>
    private static readonly object _dnsCacheLock = new();
    private static readonly Dictionary<string, (bool blocked, long tick)> _dnsCache = new(StringComparer.OrdinalIgnoreCase);
    private static bool ResolvesToBlockedAddress(string host)
    {
        lock (_dnsCacheLock)
        {
            if (_dnsCache.TryGetValue(host, out var e) && Environment.TickCount64 - e.tick < 5 * 60 * 1000)
                return e.blocked;
        }
        bool blocked = ResolvesToBlockedAddressSlow(host);
        lock (_dnsCacheLock)
        {
            if (_dnsCache.Count > 512)
                _dnsCache.Clear();
            _dnsCache[host] = (blocked, Environment.TickCount64);
        }
        return blocked;
    }

    private static bool ResolvesToBlockedAddressSlow(string host)
    {
        try
        {
            var addrs = Dns.GetHostAddresses(host);
            foreach (var a in addrs)
            {
                var ip = a;
                if (ip.IsIPv4MappedToIPv6)
                {
                    try { ip = ip.MapToIPv4(); }
                    catch { return true; }
                }
                if (IPAddress.IsLoopback(ip))
                    return true;
                if (ip.AddressFamily == AddressFamily.InterNetwork)
                {
                    byte[] b = ip.GetAddressBytes();
                    if (b[0] == 10) return true;
                    if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return true;
                    if (b[0] == 192 && b[1] == 168) return true;
                    if (b[0] == 169 && b[1] == 254) return true;
                    if (b[0] == 0 || b[0] >= 224) return true;
                }
                else if (ip.AddressFamily == AddressFamily.InterNetworkV6)
                {
                    byte[] b = ip.GetAddressBytes();
                    if (b.Length == 16 && b.All(x => x == 0)) return true;
                    if (b.Length == 16 && (b[0] & 0xFE) == 0xFC) return true;
                    if (ip.IsIPv6SiteLocal || ip.IsIPv6LinkLocal || ip.IsIPv6Multicast)
                        return true;
                }
            }
            return false;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>True for all-digit/dotted/hex IP spellings: dotted quads (incl.
    /// leading-zero octal), short forms Windows still routes (127.1, 10.1),
    /// bare decimal integers, and 0x-hex forms (incl. per-part 0x).</summary>
    private static bool LooksLikeNumericIp(string host)
    {
        string h = host.Trim('[', ']').ToLowerInvariant();
        if (string.IsNullOrEmpty(h))
            return false;
        if (h.StartsWith("0x", StringComparison.Ordinal))
            return h.Skip(2).All(c => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || c == '.' || c == ':' || c == 'x');
        if (h.All(char.IsDigit))
            return h.Length > 0;
        string[] parts = h.Split('.');
        // 1-4 dot-separated numeric-ish parts (decimal, leading-zero octal,
        // or 0x-hex): stacks route "127.1" and "0xc0.0xa8.1.1" to addresses.
        // Real hostnames contain letters (beyond a-f-only hex lookalikes with
        // an explicit 0x prefix) or hyphens and never match this shape.
        if (parts.Length >= 1 && parts.Length <= 4 && parts.All(IsNumericIpPart))
            return true;
        return false;
    }

    private static bool IsNumericIpPart(string p)
    {
        if (string.IsNullOrEmpty(p) || p.Length > 10)
            return false;
        if (p.StartsWith("0x", StringComparison.Ordinal))
            return p.Length > 2 && p.Skip(2).All(c => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'));
        return p.All(char.IsDigit);
    }

    private sealed class CapturePayload
    {
        public string? Url { get; set; }
        public string? FileName { get; set; }
        public string? Referer { get; set; }
        public string? PageTitle { get; set; }
        public Dictionary<string, string> Headers { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public bool DirectDownload { get; set; }
        public string? YoutubeFormatArg { get; set; }
        public string? StreamType { get; set; }
        public string? VideoUrl { get; set; }
        public string? AudioUrl { get; set; }
        public string? PageUrl { get; set; }
    }

    private sealed class ResolveResponse
    {
        public string Title { get; set; } = "";
        public string Channel { get; set; } = "";
        public string ThumbnailUrl { get; set; } = "";
        public bool IsPlaylist { get; set; }
        public string? PlaylistTitle { get; set; }
        public int ItemCount { get; set; }
        public List<QualityResponse> Qualities { get; set; } = new();
        // Embed-pipeline extras (absent for yt-dlp responses; ignored by old clients).
        public string? DirectUrl { get; set; }
        public string? StreamType { get; set; }
    }

    private sealed class QualityResponse
    {
        public string Label { get; set; } = "";
        public string FormatArg { get; set; } = "";
        public long? EstimatedBytes { get; set; }
        public string? EstimatedSizeText { get; set; }
    }
}
