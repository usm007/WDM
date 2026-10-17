using System.IO.Pipes;
using System.Text.Json;

namespace Redm.Core;

/// <summary>Download-queue handoff over a local named pipe.
/// Extension -> Redm.MsgHost (stdio) -> Redm.App (pipe) -> DlQueue.
/// No drivers, no admin, no network services.</summary>
public enum RouteKind { Http, Ftp, Hls, Reject }

public record RouteResult(RouteKind Kind, string Reason);

public static class SchemeRouter
{
    public const int MaxUrlLength = 4096;

    public static RouteResult Route(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || url.Length > MaxUrlLength)
            return new(RouteKind.Reject, "empty-or-too-long");
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var u))
            return new(RouteKind.Reject, "not-absolute-url");
        return u.Scheme.ToLowerInvariant() switch
        {
            "http" or "https" => (u.AbsolutePath.Contains(".m3u8", StringComparison.OrdinalIgnoreCase)
                    || u.AbsolutePath.Contains(".mpd", StringComparison.OrdinalIgnoreCase))
                ? new(RouteKind.Hls, "playlist")
                : new(RouteKind.Http, "ok"),
            "ftp" => new(RouteKind.Ftp, "ok"),
            _ => new(RouteKind.Reject, "unsupported-scheme:" + u.Scheme.ToLowerInvariant()),
        };
    }
}

public record HandoffReply(bool Accepted, string Reason, bool Duplicate);

public static class Handoff
{
    public const string PipeName = "ReDM.DownloadQueue";
    /// <summary>Per-user pipe name so fast-user-switched sessions never share
    /// a queue. Falls back to the base name when the user cannot be read.</summary>
    public static string PipeNameForUser(string? user = null)
    {
        string u = user ?? Environment.UserName;
        var safe = new string(u.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray());
        if (safe == "") safe = "default";
        return $"{PipeName}.{safe}";
    }
    public static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(3);
    public static readonly TimeSpan ReplyTimeout = TimeSpan.FromSeconds(5);

    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static string EncodeRequest(string url) => JsonSerializer.Serialize(new { url }, Json);

    public static HandoffReply? DecodeReply(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var r = doc.RootElement;
            return new(
                r.TryGetProperty("accepted", out var a) && a.GetBoolean(),
                r.TryGetProperty("reason", out var rs) ? rs.GetString() ?? "" : "",
                r.TryGetProperty("duplicate", out var d) && d.GetBoolean());
        }
        catch { return null; }
    }

    public static string EncodeReply(HandoffReply reply) => JsonSerializer.Serialize(reply, Json);
}

/// <summary>App side: accepts one line-delimited JSON {url} per
/// connection, applies <paramref name="accept"/> and replies.</summary>
public sealed class HandoffServer : IDisposable
{
    private readonly string _pipeName;
    private readonly Func<string, HandoffReply> _accept;
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private bool _disposed;

    public HandoffServer(string pipeName, Func<string, HandoffReply> accept)
    {
        _pipeName = pipeName;
        _accept = accept;
    }

    public void Start() { _cts = new(); _loop = LoopAsync(_cts.Token); }

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var server = new NamedPipeServerStream(_pipeName, PipeDirection.InOut, 4,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            try
            {
                await server.WaitForConnectionAsync(ct);
                _ = HandleAsync(server, ct);
            }
            catch (OperationCanceledException) { server.Dispose(); break; }
            catch { server.Dispose(); }
        }
    }

    private async Task HandleAsync(NamedPipeServerStream server, CancellationToken ct)
    {
        using (server)
        {
            using var reader = new StreamReader(server, leaveOpen: true);
            using var writer = new StreamWriter(server, leaveOpen: true) { AutoFlush = true };
            string? line;
            try { line = await reader.ReadLineAsync(ct); }
            catch { return; }
            if (line is null) return;
            string url;
            try
            {
                using var doc = JsonDocument.Parse(line);
                url = doc.RootElement.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "";
            }
            catch { url = ""; }
            var reply = _accept(url);
            try { await writer.WriteLineAsync(Handoff.EncodeReply(reply)); } catch { }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _cts?.Cancel(); } catch { }
    }
}

/// <summary>MsgHost side: forwards one validated URL to the running app.
/// Stream factory is injectable so tests never touch real pipes.</summary>
public sealed class HandoffClient
{
    private readonly string _pipeName;
    private readonly Func<CancellationToken, Task<Stream>> _connect;
    private readonly TimeSpan _replyTimeout;

    public HandoffClient(string pipeName,
        Func<CancellationToken, Task<Stream>>? connect = null,
        TimeSpan? replyTimeout = null)
    {
        _pipeName = pipeName;
        _replyTimeout = replyTimeout ?? Handoff.ReplyTimeout;
        _connect = connect ?? DefaultConnectAsync;
    }

    private async Task<Stream> DefaultConnectAsync(CancellationToken ct)
    {
        var client = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(Handoff.ConnectTimeout);
        try { await client.ConnectAsync(cts.Token); }
        catch (Exception ex) { client.Dispose(); throw new HandoffUnavailableException(ex.Message); }
        return client;
    }

    public async Task<HandoffReply> SendAsync(string url, CancellationToken ct)
    {
        Stream stream;
        try { stream = await _connect(ct); }
        catch (HandoffUnavailableException) { throw; }
        catch (Exception ex) { throw new HandoffUnavailableException(ex.Message); }
        using (stream)
        {
            using var writer = new StreamWriter(stream, leaveOpen: true) { AutoFlush = true };
            using var reader = new StreamReader(stream, leaveOpen: true);
            try { await writer.WriteLineAsync(Handoff.EncodeRequest(url)); }
            catch (Exception ex) { throw new HandoffUnavailableException(ex.Message); }
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(_replyTimeout);
            string? line;
            try { line = await reader.ReadLineAsync(cts.Token); }
            catch (OperationCanceledException) { throw new HandoffTimeoutException(); }
            var reply = line is null ? null : Handoff.DecodeReply(line);
            if (reply is null) throw new HandoffUnavailableException("bad-reply");
            return reply;
        }
    }
}

public sealed class HandoffUnavailableException(string message) : Exception(message);
public sealed class HandoffTimeoutException() : Exception("timeout");
