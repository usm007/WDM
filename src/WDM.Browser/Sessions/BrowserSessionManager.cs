using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using WDM.Browser.Contracts;
using WDM.Browser.Ipc;
using WDM.Browser.Sessions;

namespace WDM.Browser.Sessions;

/// <summary>WDM-side lifecycle owner for one WDM.BrowserHost.exe process:
/// launch, handshake, session factory, graceful stop, crash detection, no
/// orphans. One host serves one session at a time (conservative default).</summary>
public sealed class BrowserSessionManager : IBrowserHost
{
    private readonly object _gate = new();
    private Process? _process;
    private BrowserPipeClient? _client;
    private string? _pipeName;
    private BrowserSessionOptions? _options;
    private BrowserHostState _state = BrowserHostState.Stopped;
    private readonly StringBuilder _stderr = new();
    private const int StderrCap = 100 * 1024;

    private int? _lastProcessId;

    public int? ProcessId
    {
        get
        {
            lock (_gate)
            {
                try { return _process?.Id ?? _lastProcessId; }
                catch { return _lastProcessId; }
            }
        }
    }

    public BrowserHostState State
    {
        get { lock (_gate) { return _state; } }
    }

    public event Action<BrowserHostState>? StateChanged;

    public string? ErrorLog
    {
        get { lock (_gate) { return _stderr.Length == 0 ? null : _stderr.ToString(); } }
    }

    public static string LocateHostExe(string? overridePath)
    {
        if (!string.IsNullOrWhiteSpace(overridePath))
            return overridePath;
        return Path.Combine(AppContext.BaseDirectory, "WDM.BrowserHost.exe");
    }

    public async Task StartAsync(BrowserSessionOptions options, CancellationToken ct)
    {
        lock (_gate)
        {
            if (_state != BrowserHostState.Stopped)
                throw new InvalidOperationException("Browser host is already running.");
            SetState(BrowserHostState.Starting);
        }
        _options = options;
        string exe = LocateHostExe(options.HostExePath);
        if (!File.Exists(exe))
            throw new FileNotFoundException("Browser runtime is not installed next to WDM.", exe);

        string profileDir = options.ProfileDir ?? Path.Combine(Path.GetTempPath(), "wdm-bh-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(profileDir);

        _pipeName = BrowserProtocol.PipeNamePrefix + Guid.NewGuid().ToString("N");
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            Arguments = $"--pipe \"{_pipeName}\" --profile \"{profileDir}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
        };
        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null)
                return;
            lock (_gate)
            {
                if (_stderr.Length < StderrCap)
                    _stderr.AppendLine(e.Data);
            }
        };
        process.Exited += (_, _) =>
        {
            lock (_gate)
            {
                if (_state != BrowserHostState.Stopping && _state != BrowserHostState.Stopped)
                    SetState(BrowserHostState.Crashed);
            }
        };
        if (!process.Start())
            throw new InvalidOperationException("Could not launch the browser runtime.");
        process.BeginErrorReadLine();
        lock (_gate)
        {
            _process = process;
            try { _lastProcessId = process.Id; } catch { }
        }

        var client = new BrowserPipeClient();
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(options.StartupTimeout);
            await client.ConnectAsync(_pipeName, cts.Token).ConfigureAwait(false);
            await client.SendAsync(BrowserProtocol.Hello, null, new { version = BrowserProtocol.Version }, cts.Token).ConfigureAwait(false);
            var ready = await client.ReceiveAsync(cts.Token).ConfigureAwait(false);
            if (ready.Type != BrowserProtocol.Ready)
                throw new InvalidOperationException("Browser runtime handshake failed: " + ready.Type);
            lock (_gate)
            {
                _client = client;
            }
            SetState(BrowserHostState.Ready);
        }
        catch
        {
            await client.DisposeAsync().ConfigureAwait(false);
            await KillProcessAsync().ConfigureAwait(false);
            lock (_gate)
            {
                _process = null;
                SetState(BrowserHostState.Stopped);
            }
            throw;
        }
    }

    public Task<IBrowserSession> CreateSessionAsync(BrowserSessionOptions options, CancellationToken ct)
    {
        BrowserPipeClient client;
        lock (_gate)
        {
            if (_state != BrowserHostState.Ready || _client is null)
                throw new InvalidOperationException("Browser host is not ready.");
            client = _client;
            SetState(BrowserHostState.Busy);
        }
        string sessionId = Guid.NewGuid().ToString("N");
        IBrowserSession session = new BrowserSession(client, sessionId, options, Release);
        return Task.FromResult(session);
    }

    private void Release()
    {
        lock (_gate)
        {
            if (_state == BrowserHostState.Busy)
                SetState(BrowserHostState.Ready);
        }
    }

    public async Task StopAsync()
    {
        BrowserPipeClient? client;
        lock (_gate)
        {
            if (_state == BrowserHostState.Stopped)
                return;
            SetState(BrowserHostState.Stopping);
            client = _client;
            _client = null;
        }
        if (client is not null)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await client.SendAsync(BrowserProtocol.Shutdown, null, new { }, cts.Token).ConfigureAwait(false);
                // Drain any pending messages (e.g. PageState from CancelNavigate) and wait for Bye
                try
                {
                    while (!cts.IsCancellationRequested)
                    {
                        var msg = await client.ReceiveAsync(cts.Token).ConfigureAwait(false);
                        if (msg.Type == BrowserProtocol.Bye)
                            break;
                    }
                }
                catch { }
            }
            catch { }
            await client.DisposeAsync().ConfigureAwait(false);
        }
        await KillProcessAsync().ConfigureAwait(false);
        lock (_gate)
        {
            _process = null;
            SetState(BrowserHostState.Stopped);
        }
    }

    public async Task RestartAsync(BrowserSessionOptions options, CancellationToken ct)
    {
        await StopAsync().ConfigureAwait(false);
        await StartAsync(options, ct).ConfigureAwait(false);
    }

    private async Task KillProcessAsync()
    {
        Process? process;
        lock (_gate)
        {
            process = _process;
        }
        if (process is null)
            return;
        try
        {
            bool exited = false;
            try { exited = process.HasExited; } catch { exited = true; }

            // Grace window for CEF teardown and clean exit
            if (!exited)
            {
                exited = await Task.Run(() => process.WaitForExit(3000)).ConfigureAwait(false);
            }

            // If not exited within grace period, kill root first without tree enumeration,
            // then kill tree, with retry loop until dead.
            var sw = Stopwatch.StartNew();
            while (!exited && sw.ElapsedMilliseconds < 5000)
            {
                try { process.Kill(); } catch { }
                try { process.Kill(entireProcessTree: true); } catch { }
                exited = await Task.Run(() => process.WaitForExit(250)).ConfigureAwait(false);
                if (!exited)
                {
                    try { exited = process.HasExited; } catch { exited = true; }
                }
            }
        }
        catch { }
        finally
        {
            try { process.Dispose(); } catch { }
        }
    }

    private void SetState(BrowserHostState state)
    {
        _state = state;
        try { StateChanged?.Invoke(state); } catch { }
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);
}
