using System;
using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace WDM.Browser.Ipc;

/// <summary>Length-prefixed JSON envelope. Decode validates everything the
/// webpage side could influence: version, type, sizes, URL shapes, header
/// counts — a hostile page must never crash or confuse WDM via IPC (§24).
/// Framing: 4-byte little-endian length + UTF-8 JSON.</summary>
public static class BrowserMessage
{
    public const int MaxMessageBytes = 4 * 1024 * 1024;
    public const int MaxUrlLength = 2048;
    public const int MaxHeaders = 100;
    public const int MaxHeaderBytes = 8192;
    public const int MaxEventsPerMessage = 1000;

    public sealed record Envelope(int V, string Type, string? SessionId, JsonElement? Payload);

    public static byte[] Encode(string type, string? sessionId, object? payload)
    {
        string json = JsonSerializer.Serialize(new
        {
            v = BrowserProtocol.Version,
            type,
            sessionId,
            payload,
        });
        byte[] body = Encoding.UTF8.GetBytes(json);
        if (body.Length > MaxMessageBytes)
            throw new InvalidOperationException("Browser IPC message exceeds 4MB cap.");
        var frame = new byte[4 + body.Length];
        BinaryPrimitives.WriteInt32LittleEndian(frame, body.Length);
        body.CopyTo(frame, 4);
        return frame;
    }

    public static bool TryDecode(byte[] frame, out Envelope? message, out string? error)    {
        message = null;
        error = null;
        try
        {
            if (frame.Length < 4)
            {
                error = "frame too short";
                return false;
            }
            int len = BinaryPrimitives.ReadInt32LittleEndian(frame);
            if (len < 2 || len > MaxMessageBytes || len != frame.Length - 4)
            {
                error = "bad frame length";
                return false;
            }
            using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(frame, 4, len));
            var root = doc.RootElement;
            if (!root.TryGetProperty("v", out var v) || v.GetInt32() != BrowserProtocol.Version)
            {
                error = "protocol version mismatch";
                return false;
            }
            if (!root.TryGetProperty("type", out var t) || t.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(t.GetString()) || t.GetString()!.Length > 64)
            {
                error = "bad message type";
                return false;
            }
            string? sessionId = null;
            if (root.TryGetProperty("sessionId", out var s) && s.ValueKind == JsonValueKind.String)
            {
                sessionId = s.GetString();
                if (sessionId is not null && sessionId.Length > 128)
                {
                    error = "session id too long";
                    return false;
                }
            }
            JsonElement? payload = null;
            if (root.TryGetProperty("payload", out var p) && p.ValueKind != JsonValueKind.Null)
                payload = p.Clone();
            message = new Envelope(BrowserProtocol.Version, t.GetString()!, sessionId, payload);
            return true;
        }
        catch (Exception ex)
        {
            error = "decode failed: " + ex.GetType().Name;
            return false;
        }
    }

    /// <summary>Reads one length-prefixed frame from a connected pipe.
    /// Shared by the WDM-side client and the host-side server.</summary>
    public static async Task<byte[]> ReadFrameAsync(PipeStream pipe, CancellationToken ct)
    {
        var lenBuf = new byte[4];
        await ReadExactAsync(pipe, lenBuf, ct).ConfigureAwait(false);
        int len = BinaryPrimitives.ReadInt32LittleEndian(lenBuf);
        if (len < 2 || len > MaxMessageBytes)
            throw new InvalidOperationException("IPC peer sent an over/under-sized frame.");
        var frame = new byte[4 + len];
        lenBuf.CopyTo(frame, 0);
        await ReadExactAsync(pipe, frame.AsMemory(4), ct).ConfigureAwait(false);
        return frame;
    }

    public static async Task WriteFrameAsync(PipeStream pipe, byte[] frame, CancellationToken ct)
    {
        await pipe.WriteAsync(frame, ct).ConfigureAwait(false);
        await pipe.FlushAsync(ct).ConfigureAwait(false);
    }

    private static async Task ReadExactAsync(PipeStream pipe, Memory<byte> buf, CancellationToken ct)
    {
        int done = 0;
        while (done < buf.Length)
        {
            int n = await pipe.ReadAsync(buf[done..], ct).ConfigureAwait(false);
            if (n == 0)
                throw new EndOfStreamException("IPC peer disconnected mid-frame.");
        }
    }

    /// <summary>Shared payload hygiene for host→app event batches.</summary>
    public static bool IsSafeUrl(string? url) =>
        !string.IsNullOrWhiteSpace(url) && url.Length <= MaxUrlLength
        && (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            || url.StartsWith("blob:http", StringComparison.OrdinalIgnoreCase)
            || url.StartsWith("data:", StringComparison.OrdinalIgnoreCase));

    public static Dictionary<string, string> SanitizeHeaders(IDictionary<string, string>? headers)
    {
        var clean = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (headers is null)
            return clean;
        foreach (var kv in headers)
        {
            if (clean.Count >= MaxHeaders)
                break;
            if (string.IsNullOrWhiteSpace(kv.Key) || kv.Key.Length > 256)
                continue;
            string value = kv.Value ?? "";
            if (value.Length > MaxHeaderBytes)
                value = value[..MaxHeaderBytes];
            if (value.Contains('\r') || value.Contains('\n'))
                continue;
            clean[kv.Key] = value;
        }
        return clean;
    }
}
