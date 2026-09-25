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

    public async Task ConnectAsync(string pipeName, CancellationToken ct)
    {
        var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            await pipe.ConnectAsync(ct).ConfigureAwait(false);
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
            await pipe.WriteAsync(frame, ct).ConfigureAwait(false);
            await pipe.FlushAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task<BrowserMessage.Envelope> ReceiveAsync(CancellationToken ct)
    {
        var pipe = _pipe ?? throw new InvalidOperationException("Pipe is not connected.");
        byte[] frame = await BrowserMessage.ReadFrameAsync(pipe, ct).ConfigureAwait(false);
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
