using System.IO.Pipes;

namespace WDM.Services;

/// <summary>Second-instance handoff: when a second WDM copy starts while one is
/// already running (BUG-018), it forwards its command line over a session-local
/// named pipe instead of silently exiting — so a hidden-to-tray instance is
/// restored and any URL argument is opened as a new download.</summary>
public static class SingleInstancePipe
{
    public static string PipeNameForCurrentSession()
    {
        string user = Environment.UserName.Replace('\\', '_').Replace('/', '_');
        int session = System.Diagnostics.Process.GetCurrentProcess().SessionId;
        return $"WDM.SingleInstance.{user}.{session}";
    }

    /// <summary>First-instance listener. Fire-and-forget; stops when <paramref name="ct"/> cancels.</summary>
    public static void Start(string pipeName, Action<string[]> onArgs, CancellationToken ct)
    {
        _ = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                using var server = new NamedPipeServerStream(
                    pipeName, PipeDirection.In, maxNumberOfServerInstances: 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                try
                {
                    await server.WaitForConnectionAsync(ct).ConfigureAwait(false);
                    var args = new List<string>();
                    using var reader = new StreamReader(server, System.Text.Encoding.UTF8, leaveOpen: true);
                    string? line;
                    int drained = 0;
                    while ((line = await reader.ReadLineAsync().ConfigureAwait(false)) is not null)
                    {
                        // Drain everything (BUG-023): stopping early while the
                        // client still writes can deadlock it on a full pipe
                        // buffer. Keep the first 64 sane-length args.
                        if (args.Count < 64 && line.Length <= 8192)
                            args.Add(line);
                        // …but bound the drain: a sender streaming endless lines
                        // would otherwise hold this single server instance
                        // forever and block legitimate handoffs. Legit senders
                        // emit ≤64 lines, so 1024 is generous.
                        if (++drained >= 1024)
                            break;
                    }
                    try { server.Disconnect(); } catch { }
                    try { onArgs(args.ToArray()); } catch { }
                }
                catch (OperationCanceledException) { break; }
                catch { await Task.Delay(250, ct).ConfigureAwait(false); }
            }
        }, ct);
    }

    /// <summary>Second-instance sender. Best-effort, never throws.</summary>
    public static bool TrySendArgs(string pipeName, string[] args, int timeoutMs = 800)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.Out);
            client.Connect(timeoutMs);
            using var writer = new StreamWriter(client, System.Text.Encoding.UTF8, leaveOpen: true)
            {
                AutoFlush = true
            };
            foreach (string a in (args ?? Array.Empty<string>()).Take(64))
                writer.WriteLine(a.Length > 8192 ? a[..8192] : a);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
