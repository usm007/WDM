// BrowserHostTests — WDM.BrowserHost.exe driven through BrowserSessionManager
// against a local deterministic fixture server. Needs the host binary built
// (same configuration as the tests). Category=Browser: runs in CI.
using System.Diagnostics;
using System.Net;
using System.Text;
using WDM.Browser.Models;
using WDM.Browser.Sessions;
using WDM.Tests.TestInfrastructure;
using Xunit;

namespace WDM.Tests;

public sealed class BrowserHostTests : IAsyncLifetime
{
    private readonly Xunit.Abstractions.ITestOutputHelper _out;
    private HttpListener? _server;
    private string _base = "";
    private string _profile = "";
    private static readonly byte[] Mp4Bytes = new byte[64 * 1024];

    public async Task InitializeAsync()
    {
        try
        {
            foreach (var p in Process.GetProcessesByName("WDM.BrowserHost"))
            {
                try { p.Kill(); } catch { }
            }
        }
        catch { }

        new Random(7).NextBytes(Mp4Bytes);
        int port = FreePort();
        _server = new HttpListener();
        _server.Prefixes.Add($"http://127.0.0.1:{port}/");
        _server.Start();
        _base = $"http://127.0.0.1:{port}";
        _server.BeginGetContext(OnContext, null);
        _profile = Path.Combine(Path.GetTempPath(), "wdm_bh_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_profile);
        await Task.CompletedTask;
    }

    public BrowserHostTests(Xunit.Abstractions.ITestOutputHelper output) => _out = output;

    private static int FreePort()
    {
        using var tcp = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        tcp.Start();
        int port = ((IPEndPoint)tcp.LocalEndpoint).Port;
        tcp.Stop();
        return port;
    }

    private void OnContext(IAsyncResult ar)
    {
        HttpListener? server = _server;
        if (server is null || !server.IsListening)
            return;
        try { server.BeginGetContext(OnContext, null); } catch { return; }
        HttpListenerContext ctx;
        try { ctx = server.EndGetContext(ar); }
        catch { return; }
        try
        {
            string path = ctx.Request.Url?.AbsolutePath ?? "/";
            byte[] body;
            string contentType;
            switch (path)
            {
                case "/simple-video.html":
                    body = Encoding.UTF8.GetBytes(
                        "<html><head><title>Host Fixture</title></head><body>" +
                        "<video src=\"/media/sample.mp4\"></video>" +
                        "<script>fetch('/api/meta.json').then(r=>r.text()).then(t=>{document.title='meta:'+t.length;});</script>" +
                        "</body></html>");
                    contentType = "text/html";
                    break;
                case "/media/sample.mp4":
                    body = Mp4Bytes;
                    contentType = "video/mp4";
                    break;
                case "/api/meta.json":
                    body = Encoding.UTF8.GetBytes("{\"title\":\"x\"}");
                    contentType = "application/json";
                    break;
                default:
                    ctx.Response.StatusCode = 404;
                    ctx.Response.Close();
                    return;
            }
            ctx.Response.ContentType = contentType;
            ctx.Response.ContentLength64 = body.Length;
            ctx.Response.AddHeader("Accept-Ranges", "bytes");
            ctx.Response.OutputStream.Write(body, 0, body.Length);
            ctx.Response.Close();
        }
        catch { }
    }

    public async Task DisposeAsync()
    {
        try { _server?.Stop(); } catch { }
        _server = null;
        try { Directory.Delete(_profile, recursive: true); } catch { }
        await Task.CompletedTask;
    }

    private static string HostExe()
    {
#if DEBUG
        const string config = "Debug";
#else
        const string config = "Release";
#endif
        string path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "..", "..", "..", "..", "WDM.BrowserHost", "bin", config,
            "net8.0-windows", "win-x64", "WDM.BrowserHost.exe"));
        return path;
    }

    [Trait("Category", Cats.Browser)][Fact]
    public async Task Manager_Navigate_StreamsEvents_AndScript()
    {
        string exe = HostExe();
        Assert.True(File.Exists(exe), "build WDM.BrowserHost first: " + exe);

        var options = new BrowserSessionOptions
        {
            HostExePath = exe,
            ProfileDir = _profile,
            StartupTimeout = TimeSpan.FromSeconds(30),
            NavigationTimeout = TimeSpan.FromSeconds(20),
            DiscoveryTimeout = TimeSpan.FromSeconds(8),
            OverallTimeout = TimeSpan.FromSeconds(90),
        };
        await using var host = new BrowserSessionManager();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        int? pid = null;
        try
        {
            await host.StartAsync(options, cts.Token);
            pid = host.ProcessId;
        }
        catch (Exception ex)
        {
            _out.WriteLine("START FAILED: " + ex.GetType().Name + " " + ex.Message);
            _out.WriteLine("HOST STDERR: " + (host.ErrorLog ?? "<empty>"));
            _out.WriteLine("PROCS: " + string.Join(",", Process.GetProcessesByName("WDM.BrowserHost").Select(p => p.Id)));
            throw;
        }
        try
        {
            await using var session = await host.CreateSessionAsync(options, cts.Token);
            var events = new List<BrowserNetworkEvent>();
            await foreach (var evt in session.NavigateAsync(_base + "/simple-video.html", cts.Token))
                events.Add(evt);

            Assert.Contains(events, e => e.ResourceType == "MainFrame" && e.Url.EndsWith("/simple-video.html"));
            Assert.Contains(events, e => e.Url.EndsWith("/media/sample.mp4") && e.ContentType == "video/mp4");
            Assert.Contains(events, e => e.Url.EndsWith("/api/meta.json"));
            var mp4 = events.First(e => e.Url.EndsWith("/media/sample.mp4"));
            Assert.True(mp4.RequestHeaders.Count > 0);

            string? title = await session.ExecuteScriptAsync("document.title", cts.Token);
            Assert.False(string.IsNullOrWhiteSpace(title));
        }
        finally
        {
            var swStop = Stopwatch.StartNew();
            await host.StopAsync();
            _out.WriteLine($"StopAsync took {swStop.ElapsedMilliseconds} ms");
        }
        Assert.Equal(WDM.Browser.Contracts.BrowserHostState.Stopped, host.State);
        Assert.NotNull(pid);

        // Assert manager's own host process has terminated
        bool pidAlive = true;
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                using var p = Process.GetProcessById(pid.Value);
                pidAlive = !p.HasExited;
            }
            catch (ArgumentException)
            {
                pidAlive = false;
                break;
            }
            catch (InvalidOperationException)
            {
                pidAlive = false;
                break;
            }
            if (!pidAlive)
                break;
            await Task.Delay(50);
        }
        Assert.False(pidAlive, $"Host process {pid.Value} is still alive after StopAsync.");

        // Poll with deadline to ensure global process table reflects teardown (filter out already-exited processes)
        var deadlineTotal = DateTimeOffset.UtcNow.AddSeconds(3);
        Process[] leftovers = Array.Empty<Process>();
        while (DateTimeOffset.UtcNow < deadlineTotal)
        {
            leftovers = Process.GetProcessesByName("WDM.BrowserHost")
                .Where(p => { try { return !p.HasExited; } catch { return false; } })
                .ToArray();
            if (leftovers.Length == 0)
                break;
            await Task.Delay(100);
        }
        if (leftovers.Length > 0)
        {
            foreach (var lp in leftovers)
            {
                string start;
                try { start = lp.StartTime.ToString("HH:mm:ss.fff"); } catch { start = "?"; }
                _out.WriteLine($"LEFTOVER pid={lp.Id} start={start}");
            }
            _out.WriteLine("HOST STDERR: " + (host.ErrorLog ?? "<empty>"));
        }
        Assert.Empty(leftovers);
    }
}




