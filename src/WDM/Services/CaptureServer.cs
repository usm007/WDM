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
        using (client)
        {
            try
            {
                using var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);

                string? line = await reader.ReadLineAsync();
                if (line is null)
                    return;
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
                while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync()))
                {
                    var colon = line.IndexOf(':');
                    if (colon <= 0)
                        continue;
                    string name = line[..colon].Trim();
                    string value = line[(colon + 1)..].Trim();
                    if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                        long.TryParse(value, out contentLength);
                    else if (name.Equals("Expect", StringComparison.OrdinalIgnoreCase))
                        expectContinue = value.Contains("100-continue", StringComparison.OrdinalIgnoreCase);
                }

                if (expectContinue)
                {
                    await WriteRawAsync(stream, "HTTP/1.1 100 Continue\r\n\r\n");
                }

                string body = "";
                if (contentLength > 0)
                {
                    var buffer = new char[contentLength];
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
                    await WriteResponseAsync(stream, HttpStatusCode.NoContent, "");
                    return;
                }

                if (method == "GET" && path == "/ping")
                {
                    IsConnected = true;
                    ExtensionConnected?.Invoke();
                    string ver = typeof(CaptureServer).Assembly.GetName().Version?.ToString(3) ?? "2.5.2";
                    await WriteResponseAsync(stream, HttpStatusCode.OK, $"{{\"status\":\"ok\",\"version\":\"{ver}\"}}");
                    return;
                }

                if (method == "POST" && path == "/download")
                {
                    try
                    {
                        var payload = JsonSerializer.Deserialize<CapturePayload>(body, JsonOptions);
                        if (payload is null || string.IsNullOrWhiteSpace(payload.Url))
                            throw new InvalidOperationException("Empty url");
                        IsConnected = true;
                        ExtensionConnected?.Invoke();
                        _onCapture(payload.Url, payload.FileName, payload.Referer, payload.Headers, payload.PageTitle);
                        await WriteResponseAsync(stream, HttpStatusCode.OK, "{\"accepted\":true}");
                    }
                    catch
                    {
                        await WriteResponseAsync(stream, HttpStatusCode.BadRequest, "{\"error\":\"invalid request\"}");
                    }
                    return;
                }

                // GET /resolve?url=<encoded-url>
                // Returns available quality tiers for a YouTube (or any yt-dlp-supported) URL.
                if (method == "GET" && path == "/resolve")
                {
                    string? videoUrl = null;
                    foreach (var pair in queryString.Split('&'))
                    {
                        var kv = pair.Split('=', 2);
                        if (kv.Length == 2 && kv[0].Equals("url", StringComparison.OrdinalIgnoreCase))
                        {
                            videoUrl = Uri.UnescapeDataString(kv[1]);
                            break;
                        }
                    }

                    if (string.IsNullOrWhiteSpace(videoUrl))
                    {
                        await WriteResponseAsync(stream, HttpStatusCode.BadRequest, "{\"error\":\"missing url param\"}");
                        return;
                    }

                    try
                    {
                        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
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
                        await WriteResponseAsync(stream, HttpStatusCode.OK, json);
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
                        await WriteResponseAsync(stream, HttpStatusCode.OK, fallbackJson);
                    }
                    return;
                }

                await WriteResponseAsync(stream, HttpStatusCode.NotFound, "");
            }
            catch
            {
                // Client hung up mid-request; nothing to do.
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

    private static async Task WriteResponseAsync(Stream stream, HttpStatusCode status, string body)
    {
        byte[] bodyBytes = Encoding.UTF8.GetBytes(body);
        string headers =
            $"HTTP/1.1 {(int)status} {status}\r\n" +
            "Content-Type: application/json\r\n" +
            "Access-Control-Allow-Origin: *\r\n" +
            "Access-Control-Allow-Methods: POST, GET, OPTIONS\r\n" +
            "Access-Control-Allow-Headers: Content-Type\r\n" +
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
    }

    private sealed class QualityResponse
    {
        public string Label { get; set; } = "";
        public string FormatArg { get; set; } = "";
        public long? EstimatedBytes { get; set; }
        public string? EstimatedSizeText { get; set; }
    }
}
