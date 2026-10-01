using System;
using System.IO.Pipes;
using System.Threading;

namespace WDM.Browser.Ipc;

/// <summary>WDM-side pipe client for one BrowserHost connection. One frame at
/// a time, full duplex via sequential send/receive; the session manager owns
/// concurrency, not this client.</summary>
public sealed class BrowserPipeClient : IAsyncDisposable
{
    private NamedPipeClientStream? _pipe;
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    public bool IsConnected => _pipe?.IsConnected == true;

    /// <summary>Timeouts race OUTSIDE the pipe calls: timer-armed tokens
    /// passed to PipeStream async IO hung indefinitely in testing.</summary>
    public async Task ConnectAsync(string pipeName, CancellationToken ct)
    {
        var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            var connect = pipe.ConnectAsync();
            if (await Task.WhenAny(connect, Task.Delay(Timeout.InfiniteTimeSpan, ct)).ConfigureAwait(false) != connect)
                throw new OperationCanceledException(ct);
            await connect.ConfigureAwait(false);
            _pipe = pipe;
        }
        catch
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task SendAsync(string type, string? sessionId, object? payload, CancellationToken ct)
    {
        var pipe = _pipe ?? throw new InvalidOperationException("Pipe is not connected.");
        byte[] frame = BrowserMessage.Encode(type, sessionId, payload);
        await _writeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var send = SendFrameAsync(pipe, frame);
            if (await Task.WhenAny(send, Task.Delay(Timeout.InfiniteTimeSpan, ct)).ConfigureAwait(false) != send)
                throw new OperationCanceledException(ct);
            await send.ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private static async Task SendFrameAsync(NamedPipeClientStream pipe, byte[] frame)
    {
        await pipe.WriteAsync(frame).ConfigureAwait(false);
        await pipe.FlushAsync().ConfigureAwait(false);
    }

    public async Task<BrowserMessage.Envelope> ReceiveAsync(CancellationToken ct)
    {
        var pipe = _pipe ?? throw new InvalidOperationException("Pipe is not connected.");
        // Async reads on the overlapped handle (proven); sync reads on
        // overlapped handles hang here. Timeouts race outside the IO call.
        var read = BrowserMessage.ReadFrameAsync(pipe);
        if (await Task.WhenAny(read, Task.Delay(Timeout.InfiniteTimeSpan, ct)).ConfigureAwait(false) != read)
            throw new OperationCanceledException(ct);
        byte[] frame = await read.ConfigureAwait(false);
        if (!BrowserMessage.TryDecode(frame, out var msg, out string? error) || msg is null)
            throw new InvalidOperationException("Browser host sent an invalid message: " + error);
        return msg;
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_pipe is not null)
                await _pipe.DisposeAsync().ConfigureAwait(false);
        }
        catch { }
        _writeGate.Dispose();
    }
}
