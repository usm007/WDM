// WDM.BrowserHost — WDM-owned browser runtime (resolution engine, not a
// consumer browser). One page at a time: navigate, stream normalized network
// events, run page-context scripts, exit. All media decisions happen in WDM.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CefSharp;
using CefSharp.OffScreen;
using Nito.AsyncEx;
using WDM.Browser.Ipc;
using WDM.Browser.Models;

namespace WDM.BrowserHost;

internal static class Program
{
    private static int Main(string[] args)
    {
        string pipeName = Arg(args, "--pipe");
        string profileDir = Arg(args, "--profile");
        if (string.IsNullOrWhiteSpace(pipeName) || string.IsNullOrWhiteSpace(profileDir))
        {
            Console.Error.WriteLine("usage: WDM.BrowserHost --pipe <name> --profile <dir>");
            return 2;
        }

        int exit = 0;
        try
        {
            AsyncContext.Run(async delegate
            {
                exit = await RunAsync(pipeName, profileDir);
            });
        }
        catch (Exception ex)
        {
            Log($"FATAL: Main unhandled exception: {ex}");
            exit = 3;
        }
        finally
        {
            Environment.Exit(exit);
        }
        return exit;
    }

    private static string Arg(string[] args, string name)
    {
        for (int i = 0; i + 1 < args.Length; i++)
        {
            if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        }
        return "";
    }

    private static void Log(string msg) =>
        Console.Error.WriteLine($"[browserhost {DateTimeOffset.UtcNow:HH:mm:ss}] {msg}");

    private static async Task<int> RunAsync(string pipeName, string profileDir)
    {
        var settings = new CefSettings
        {
            CachePath = profileDir,
            LogSeverity = LogSeverity.Error,
            WindowlessRenderingEnabled = true,
        };
        settings.CefCommandLineArgs.Add("disable-gpu");
        settings.CefCommandLineArgs.Add("disable-gpu-compositing");

        Log("initializing CEF");
        var sw = Stopwatch.StartNew();
        // NOTE: no ConfigureAwait(false) anywhere on this path — Cef.Initialize
        // and Cef.Shutdown must run on the same (AsyncContext main) thread.
        if (!await Cef.InitializeAsync(settings))
        {
            Log("FATAL: Cef.InitializeAsync failed");
            return 2;
        }
        Log($"CEF ready in {sw.Elapsed}");

        using var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.None);
        Log($"waiting for WDM on pipe {pipeName}");
        try
        {
            // Direct await (no WhenAny): matches the proven pipetest pattern.
            await Task.Run(() => server.WaitForConnection());
        }
        catch (Exception ex)
        {
            Log($"FATAL: no WDM connection: {ex.GetType().Name}");
            try { Cef.Shutdown(); } catch { }
            Environment.Exit(3);
            return 3;
        }

        var host = new HostSession(server, profileDir);
        int exitCode = 0;
        try
        {
            exitCode = await host.ServeAsync();
            return exitCode;
        }
        catch (Exception ex)
        {
            Log($"FATAL: serve loop died: {ex}");
            exitCode = 3;
            return exitCode;
        }
        finally
        {
            try { host.Dispose(); } catch { }
            // Backstop: if Cef.Shutdown hangs or deadlocks, forcefully exit after 5s.
            var watchdog = new Thread(() =>
            {
                Thread.Sleep(5000);
                Log("watchdog forcing exit after Cef.Shutdown timeout");
                try { Process.GetCurrentProcess().Kill(); } catch { }
                Environment.Exit(exitCode);
            })
            {
                IsBackground = true,
                Name = "ShutdownWatchdog"
            };
            watchdog.Start();

            try { Cef.Shutdown(); } catch { }
            Log("shutdown complete");
            try { Process.GetCurrentProcess().Kill(); } catch { }
            Environment.Exit(exitCode);
        }
    }

    private sealed class HostSession : IDisposable
    {
        private readonly NamedPipeServerStream _pipe;
        private readonly string _profileDir;
        private ChromiumWebBrowser? _browser;
        private RequestContext? _requestContext;
        private EventCollector? _collector;
        private string? _sessionId;
        private bool _crashed;

        internal HostSession(NamedPipeServerStream pipe, string profileDir)
        {
            _pipe = pipe;
            _profileDir = profileDir;
        }

        internal async Task<int> ServeAsync()
        {
            // Handshake: WDM speaks first.
            var hello = await ReceiveAsync(TimeSpan.FromSeconds(10));
            if (hello is null || hello.Type != BrowserProtocol.Hello)
            {
                Log($"handshake failed (got {(hello is null ? "<undecodable>" : hello.Type)})");
                await SendAsync(BrowserProtocol.Error, null, new { message = "expected Hello" });
                return 3;
            }
            await SendAsync(BrowserProtocol.Ready, null, new { version = BrowserProtocol.Version });
            Log("handshake complete");

            while (_pipe.IsConnected)
            {
                var msg = await ReceiveAsync(TimeSpan.FromMilliseconds(-1));
                if (msg is null)
                    break;
                try
                {
                    if (await DispatchAsync(msg))
                        return 0;
                }
                catch (Exception ex)
                {
                    Log($"command {msg.Type} failed: {ex.Message}");
                    await SendAsync(BrowserProtocol.Error, msg.SessionId, new { message = ex.Message });
                }
            }
            return 0;
        }

        /// <summary>Returns true when the host should exit.</summary>
        private async Task<bool> DispatchAsync(BrowserMessage.Envelope msg)
        {
            switch (msg.Type)
            {
                case BrowserProtocol.Navigate:
                    await HandleNavigateAsync(msg);
                    return false;
                case BrowserProtocol.CancelNavigate:
                    CloseBrowser();
                    await SendAsync(BrowserProtocol.PageState, msg.SessionId,
                        new { state = "Cancelled" });
                    return false;
                case BrowserProtocol.ExecuteScript:
                    await HandleScriptAsync(msg);
                    return false;
                case BrowserProtocol.FetchBody:
                    await HandleFetchBodyAsync(msg);
                    return false;
                case BrowserProtocol.GetPageState:
                    await SendAsync(BrowserProtocol.PageState, msg.SessionId,
                        new { state = _browser is null ? "Idle" : _crashed ? "Crashed" : "Open" });
                    return false;
                case BrowserProtocol.Shutdown:
                    await SendAsync(BrowserProtocol.Bye, null, new { reason = "shutdown" });
                    CloseBrowser();
                    return true;
                default:
                    await SendAsync(BrowserProtocol.Error, msg.SessionId,
                        new { message = "unknown command: " + msg.Type });
                    return false;
            }
        }

        private async Task HandleNavigateAsync(BrowserMessage.Envelope msg)
        {
            if (_browser is not null)
            {
                await SendAsync(BrowserProtocol.Error, msg.SessionId,
                    new { message = "host is busy with another session" });
                return;
            }
            string url = PayloadString(msg, "url") ?? "";
            if (!Uri.TryCreate(url, UriKind.Absolute, out _))
            {
                await SendAsync(BrowserProtocol.Error, msg.SessionId,
                    new { message = "bad url" });
                return;
            }
            _sessionId = msg.SessionId;
            int navTimeoutSec = PayloadInt(msg, "navigationTimeoutSec", 20);
            int discoverySec = PayloadInt(msg, "discoveryTimeoutSec", 25);
            int maxEvents = PayloadInt(msg, "maxEvents", 10_000);
            bool triggerAutoplay = PayloadBool(msg, "triggerAutoplay", true);

            await SendAsync(BrowserProtocol.PageState, _sessionId, new { state = "Navigating", url });

            _collector = new EventCollector(maxEvents);
            // One request context per host run, rooted at the caller-owned
            // profile dir (temporary per analysis, or persistent for auth
            // workflows). The caller creates and cleans up that directory.
            _requestContext ??= new RequestContext(new RequestContextSettings { CachePath = _profileDir });
            // about:blank first: the handler must be attached before any real
            // navigation starts, otherwise the MainFrame request wins the race
            // and is never observed. Wait for init, then Load the target URL.
            _browser = new ChromiumWebBrowser("about:blank",
                new BrowserSettings { WindowlessFrameRate = 1 }, _requestContext);
            _browser.RequestHandler = _collector;
            _collector.RenderTerminated += status =>
            {
                _crashed = true;
                Log($"renderer terminated: {status}");
            };
            _browser.LoadError += (_, e) =>
            {
                Log($"load error: {e.ErrorCode} {e.ErrorText} url={e.FailedUrl}");
            };

            var navCts = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Clamp(navTimeoutSec, 5, 120)));
            try
            {
                var initDeadline = DateTimeOffset.UtcNow.AddSeconds(10);
                while (!_browser.IsBrowserInitialized && DateTimeOffset.UtcNow < initDeadline)
                    await Task.Delay(100);
                if (!_browser.IsBrowserInitialized)
                {
                    await SendAsync(BrowserProtocol.PageState, _sessionId,
                        new { state = "Failed", url, error = "browser init timed out" });
                    CloseBrowser();
                    return;
                }
                _browser.Load(url);
                Log($"loading {url}");
                await _browser.WaitForInitialLoadAsync().WaitAsync(navCts.Token);
                Log($"initial load done, IsLoading={_browser.IsLoading}, address={_browser.Address}");
            }
            catch (OperationCanceledException)
            {
                await SendAsync(BrowserProtocol.PageState, _sessionId,
                    new { state = "TimedOut", url });
                CloseBrowser();
                return;
            }

            string title = "";
            try
            {
                var titleResp = await _browser.EvaluateScriptAsync("document.title");
                title = titleResp.Success ? titleResp.Result?.ToString() ?? "" : "";
            }
            catch { }

            await SendAsync(BrowserProtocol.PageState, _sessionId,
                new { state = "Loading", url, title });

            // Discovery window: stream event batches until the budget expires.
            var deadline = DateTimeOffset.UtcNow.AddSeconds(Math.Clamp(discoverySec, 5, 120));
            int tick = 0;
            while (DateTimeOffset.UtcNow < deadline && !_crashed)
            {
                await Task.Delay(500);
                if (triggerAutoplay && (tick == 2 || tick == 6 || tick == 12))
                {
                    TriggerAutoplayAcrossFrames();
                }
                tick++;
                await FlushEventsAsync();
            }
            await FlushEventsAsync();

            await SendAsync(BrowserProtocol.PageState, _sessionId,
                new
                {
                    state = _crashed ? "Crashed" : "Completed",
                    url,
                    title,
                    dropped = _collector.Dropped,
                });
            if (_crashed)
                await SendAsync(BrowserProtocol.BrowserCrashed, _sessionId, new { reason = "renderer terminated" });
        }

        private async Task FlushEventsAsync()
        {
            if (_collector is null || _sessionId is null)
                return;
            List<BrowserNetworkEvent> batch = _collector.Drain();
            if (batch.Count == 0)
                return;
            var events = new List<object>(batch.Count);
            foreach (var e in batch)
            {
                events.Add(new
                {
                    requestId = e.RequestId,
                    timestamp = e.Timestamp,
                    url = e.Url,
                    method = e.Method,
                    requestHeaders = e.RequestHeaders,
                    responseHeaders = e.ResponseHeaders,
                    statusCode = e.StatusCode,
                    contentType = e.ContentType,
                    contentLength = e.ContentLength,
                    resourceType = e.ResourceType,
                    frameId = e.FrameId,
                    initiator = e.Initiator,
                });
            }
            await SendAsync(BrowserProtocol.NetworkEvents, _sessionId, new { events });
        }

        private async Task HandleScriptAsync(BrowserMessage.Envelope msg)
        {
            if (_browser is null || _crashed)
            {
                await SendAsync(BrowserProtocol.ScriptResult, msg.SessionId,
                    new { ok = false, error = "no open page" });
                return;
            }
            string script = PayloadString(msg, "script") ?? "";
            if (script.Length > 64 * 1024)
            {
                await SendAsync(BrowserProtocol.ScriptResult, msg.SessionId,
                    new { ok = false, error = "script too long" });
                return;
            }
            try
            {
                var resp = await _browser.EvaluateScriptAsync(script);
                await SendAsync(BrowserProtocol.ScriptResult, msg.SessionId,
                    new { ok = resp.Success, result = resp.Success ? resp.Result?.ToString() : null, error = resp.Success ? null : resp.Message });
            }
            catch (Exception ex)
            {
                await SendAsync(BrowserProtocol.ScriptResult, msg.SessionId,
                    new { ok = false, error = ex.Message });
            }
        }

        private async Task HandleFetchBodyAsync(BrowserMessage.Envelope msg)
        {
            if (_browser is null || _crashed)
            {
                await SendAsync(BrowserProtocol.BodyResult, msg.SessionId,
                    new { ok = false, error = "no open page" });
                return;
            }
            string url = PayloadString(msg, "url") ?? "";
            int maxBytes = PayloadInt(msg, "maxBytes", 1024 * 1024);
            if (!Uri.TryCreate(url, UriKind.Absolute, out _))
            {
                await SendAsync(BrowserProtocol.BodyResult, msg.SessionId,
                    new { ok = false, error = "bad url" });
                return;
            }
            // Page-context fetch: same origin session (cookies) as the player.
            // Binary-safe via base64; the page enforces the cap before encoding.
            string script = "(async () => { const r = await fetch(" + JsonSerializer.Serialize(url) + ");"
                + " const b = await r.arrayBuffer();"
                + " if (b.byteLength > " + Math.Clamp(maxBytes, 1024, 4 * 1024 * 1024) + ") throw new Error('body too large');"
                + " const u = new Uint8Array(b); let s = '';"
                + " for (let i = 0; i < u.length; i += 4096) s += String.fromCharCode.apply(null, u.subarray(i, i + 4096));"
                + " return btoa(s); })()";
            try
            {
                var resp = await _browser.EvaluateScriptAsync(script);
                if (!resp.Success)
                {
                    await SendAsync(BrowserProtocol.BodyResult, msg.SessionId,
                        new { ok = false, error = resp.Message ?? "fetch failed" });
                    return;
                }
                await SendAsync(BrowserProtocol.BodyResult, msg.SessionId,
                    new { ok = true, base64 = resp.Result?.ToString() });
            }
            catch (Exception ex)
            {
                await SendAsync(BrowserProtocol.BodyResult, msg.SessionId,
                    new { ok = false, error = ex.Message });
            }
        }

        private void CloseBrowser()
        {
            try { _browser?.Dispose(); } catch { }
            _browser = null;
            _collector = null;
            _sessionId = null;
            _crashed = false;
        }

        public void Dispose()
        {
            CloseBrowser();
            try { _requestContext?.Dispose(); } catch { }
            _requestContext = null;
        }

        private async Task<BrowserMessage.Envelope?> ReceiveAsync(TimeSpan timeout)
        {
            // Fully synchronous pipe IO: async PipeStream reads proved
            // unreliable here (bytes waiting, reads never completing).
            // CEF runs its own threads, so blocking this thread is safe.
            var reader = Task.Run(() =>
            {
                try
                {
                    var lenBuf = new byte[4];
                    int r = 0;
                    while (r < 4) { int n = _pipe.Read(lenBuf, r, 4 - r); if (n == 0) throw new EndOfStreamException(); r += n; }
                    int len = lenBuf[0] | (lenBuf[1] << 8) | (lenBuf[2] << 16) | (lenBuf[3] << 24);
                    if (len < 2 || len > BrowserMessage.MaxMessageBytes)
                        return (frame: (byte[]?)null, error: "over/under-sized frame");
                    var f = new byte[4 + len];
                    Buffer.BlockCopy(lenBuf, 0, f, 0, 4);
                    r = 4;
                    while (r < f.Length) { int n = _pipe.Read(f, r, f.Length - r); if (n == 0) throw new EndOfStreamException(); r += n; }
                    return (frame: f, error: (string?)null);
                }
                catch (Exception ex)
                {
                    return (frame: (byte[]?)null, error: ex.GetType().Name + " " + ex.Message);
                }
            });
            bool inTime;
            if (timeout.TotalMilliseconds < 0)
            {
                await reader;
                inTime = true;
            }
            else
            {
                inTime = await Task.WhenAny(reader, Task.Delay(timeout)) == reader;
            }
            if (!inTime)
            {
                Log("receive failed (timeout)");
                return null;
            }
            var (frame, error) = await reader;
            if (frame is null)
            {
                Log($"receive failed ({error})");
                return null;
            }
            if (!BrowserMessage.TryDecode(frame, out var msg, out string? derr))
            {
                string preview;
                try { preview = System.Text.Encoding.UTF8.GetString(frame, 4, Math.Min(frame.Length - 4, 200)); }
                catch { preview = "<binary>"; }
                Log($"decode failed ({derr}) len={frame.Length - 4} preview={preview}");
                return null;
            }
            return msg;
        }

        private Task SendAsync(string type, string? sessionId, object payload)
        {
            byte[] frame;
            try
            {
                frame = BrowserMessage.Encode(type, sessionId, payload);
            }
            catch (Exception ex)
            {
                Log($"encode failed for {type}: {ex.Message}");
                return Task.CompletedTask;
            }
            // Sync write on the sync-opened handle (matches read side).
            try
            {
                _pipe.Write(frame, 0, frame.Length);
                _pipe.Flush();
            }
            catch (Exception ex)
            {
                Log($"send {type} failed: {ex.GetType().Name}");
                throw;
            }
            return Task.CompletedTask;
        }

        private static string? PayloadString(BrowserMessage.Envelope msg, string name)
        {
            try
            {
                if (msg.Payload is JsonElement p && p.TryGetProperty(name, out var v)
                    && v.ValueKind == JsonValueKind.String)
                    return v.GetString();
            }
            catch { }
            return null;
        }

        private static int PayloadInt(BrowserMessage.Envelope msg, string name, int fallback)
        {
            try
            {
                if (msg.Payload is JsonElement p && p.TryGetProperty(name, out var v)
                    && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int n))
                    return n;
            }
            catch { }
            return fallback;
        }

        private static bool PayloadBool(BrowserMessage.Envelope msg, string name, bool fallback)
        {
            try
            {
                if (msg.Payload is JsonElement p && p.TryGetProperty(name, out var v))
                {
                    if (v.ValueKind == JsonValueKind.True) return true;
                    if (v.ValueKind == JsonValueKind.False) return false;
                }
            }
            catch { }
            return fallback;
        }

        private const string AutoplayScript = @"
(function() {
    try {
        // 1. Unmute and play all <video> elements
        var videos = document.querySelectorAll('video');
        for (var i = 0; i < videos.length; i++) {
            try {
                videos[i].muted = true;
                var p = videos[i].play();
                if (p && typeof p.catch === 'function') p.catch(function(){});
            } catch(e) {}
        }

        // 2. Vidstack & custom elements support
        var players = document.querySelectorAll('media-player, [data-media-player]');
        for (var i = 0; i < players.length; i++) {
            try {
                if (typeof players[i].play === 'function') players[i].play();
                var btn = players[i].querySelector('media-play-button, .vds-play-button');
                if (btn) btn.click();
                if (players[i].shadowRoot) {
                    var sbtn = players[i].shadowRoot.querySelector('media-play-button, .vds-play-button, button');
                    if (sbtn) sbtn.click();
                }
            } catch(e) {}
        }

        // 3. Click common play buttons and overlays
        var selectors = [
            'button[aria-label*=""Play"" i]',
            'button[title*=""Play"" i]',
            '.vds-play-button',
            '.play-button',
            '.play-btn',
            '.jw-display-icon-display',
            '.jw-icon-playback',
            '.vjs-big-play-button',
            '.plyr__control--overlaid',
            '[class*=""play-button"" i]',
            '[class*=""big-play"" i]',
            '[class*=""play_icon"" i]',
            '[class*=""playBtn"" i]'
        ];
        for (var i = 0; i < selectors.length; i++) {
            var el = document.querySelector(selectors[i]);
            if (el && el.offsetParent !== null) {
                try { el.click(); } catch(e) {}
            }
        }

        // 4. Click video player container only if it contains a player or video
        var containers = document.querySelectorAll('#player, [data-player], .player-container');
        for (var i = 0; i < containers.length; i++) {
            if (containers[i].querySelector('video, media-player, iframe')) {
                try { containers[i].click(); } catch(e) {}
            }
        }
    } catch(e) {}
})();";

        private void TriggerAutoplayAcrossFrames()
        {
            if (_browser is null || _crashed)
                return;
            try
            {
                var browser = _browser.GetBrowser();
                if (browser is null)
                    return;
                var frameIds = browser.GetFrameIdentifiers();
                foreach (string id in frameIds)
                {
                    try
                    {
                        var frame = browser.GetFrameByIdentifier(id);
                        if (frame != null && frame.IsValid)
                        {
                            frame.ExecuteJavaScriptAsync(AutoplayScript);
                        }
                    }
                    catch { }
                }
            }
            catch { }
        }
    }
}
