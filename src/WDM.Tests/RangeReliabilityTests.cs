using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using WDM.Models;
using WDM.Services;
using WDM.Tests.TestInfrastructure;
using Xunit;
using TaskStatus = WDM.Models.TaskStatus;

namespace WDM.Tests;

/// <summary>Retry semantics of the IDM range engine: a link that keeps dropping
/// after delivering data must be resumed rather than abandoned, a source that
/// stops accepting ranges must continue over one connection, and progress must
/// always describe durable bytes.</summary>
[Collection(EngineStateCollection.Name)]
public sealed class RangeReliabilityTests
{
    [Trait("Category", Cats.Unit)]
    [Fact(DisplayName = "IDM-RETRY-001 drops that delivered data do not exhaust the retry budget")]
    public async Task ProgressingDrops_ShouldNotExhaustBudget()
    {
        var origin = new FakeProgrammableOrigin();
        byte[] content = TestFiles.Make(4 * 1024 * 1024, seed: 21);
        origin.AddObject("http://primary/file.bin", content);
        // Ten consecutive connection drops, each after real data was delivered.
        // The retry budget counts attempts without progress, so this must still
        // finish even with a small budget (IDM reconnects while bytes flow).
        origin.FaultPicker = (_, i) => i < 10 ? FaultKind.ResetAfterBytes : FaultKind.None;
        string dir = TestFiles.NewTempDir("wdm-retry");
        try
        {
            var ex = await Record.ExceptionAsync(() =>
                EngineDriver.RunAsync(origin, content, dir, "f.bin", workers: 1, maxRetries: 3));
            Assert.True(ex is null, $"requests={origin.RequestCount} error={ex?.GetType().Name}: {ex?.Message}");
            Assert.Equal(content, await File.ReadAllBytesAsync(Path.Combine(dir, "f.bin")));
        }
        finally { TestFiles.DeleteDir(dir); }
    }

    [Trait("Category", Cats.Unit)]
    [Fact(DisplayName = "IDM-RETRY-002 a discarded part stops counting as progress")]
    public async Task DiscardedPart_ShouldNotInflateProgress()
    {
        var origin = new FakeProgrammableOrigin();
        byte[] content = TestFiles.Make(4 * 1024 * 1024, seed: 23);
        origin.AddObject("http://primary/file.bin", content);
        origin.FaultPicker = (_, i) => i == 0 ? FaultKind.ShortRead : FaultKind.None;
        origin.StatusOverride = (_, i) => i == 1 ? HttpStatusCode.RequestedRangeNotSatisfiable : null;
        string dir = TestFiles.NewTempDir("wdm-retry");
        try
        {
            long counted = 0;
            await EngineDriver.RunAsync(origin, content, dir, "f.bin", workers: 1, maxRetries: 4,
                onBytes: n => Interlocked.Add(ref counted, n));
            long onDisk = new FileInfo(Path.Combine(dir, "f.bin")).Length;
            Assert.True(counted == onDisk,
                $"counted {counted} bytes for a {onDisk}-byte file (requests={origin.RequestCount})");
        }
        finally { TestFiles.DeleteDir(dir); }
    }

    [Trait("Category", Cats.Unit)]
    [Fact(DisplayName = "IDM-RETRY-003 a source that drops range support finishes as one stream")]
    public async Task ServerDroppingRanges_ShouldFinishAsSingleStream()
    {
        using var server = new RangeDroppingServer(2 * 1024 * 1024);
        var engine = new DownloadEngine();
        string dir = TestFiles.NewTempDir("wdm-retry");
        try
        {
            var task = new DownloadTask
            {
                Url = server.BaseUrl + "file.bin",
                FileName = "file.bin",
                SaveFolder = dir,
            };
            engine.Start(task);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.Elapsed < TimeSpan.FromSeconds(60) && task.Status is not (TaskStatus.Completed or TaskStatus.Failed))
                await Task.Delay(50);

            Assert.Equal(TaskStatus.Completed, task.Status);
            Assert.Equal(server.Body, await File.ReadAllBytesAsync(Path.Combine(dir, "file.bin")));
        }
        finally { TestFiles.DeleteDir(dir); }
    }

    /// <summary>Advertises byte ranges on HEAD, then answers every GET with the
    /// whole entity (200) - a CDN that silently drops range support.</summary>
    private sealed class RangeDroppingServer : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly byte[] _body;
        public string BaseUrl { get; }

        public RangeDroppingServer(int size)
        {
            _body = new byte[size];
            new Random(7).NextBytes(_body);
            int port;
            using (var tcp = new TcpListener(IPAddress.Loopback, 0))
            {
                tcp.Start();
                port = ((IPEndPoint)tcp.LocalEndpoint).Port;
            }
            BaseUrl = $"http://127.0.0.1:{port}/";
            _listener.Prefixes.Add(BaseUrl);
            _listener.Start();
            _ = Task.Run(AcceptLoop);
        }

        public byte[] Body => _body;

        private async Task AcceptLoop()
        {
            while (_listener.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = await _listener.GetContextAsync(); }
                catch { return; }
                _ = Task.Run(() => Serve(ctx));
            }
        }

        private void Serve(HttpListenerContext ctx)
        {
            var req = ctx.Request;
            var resp = ctx.Response;
            try
            {
                resp.StatusCode = 200;
                resp.ContentType = "application/octet-stream";
                resp.AddHeader("Accept-Ranges", "bytes");
                resp.ContentLength64 = _body.Length;
                if (req.HttpMethod != "HEAD")
                    resp.OutputStream.Write(_body, 0, _body.Length);
                resp.OutputStream.Close();
            }
            catch
            {
                try { resp.Abort(); } catch { }
            }
        }

        public void Dispose()
        {
            try { _listener.Stop(); } catch { }
            try { _listener.Close(); } catch { }
        }
    }
}
