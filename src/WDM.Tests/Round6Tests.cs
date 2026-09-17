using System.Net;
using System.Text;
using System.Text.Json;
using WDM.Services;

namespace WDM.Tests;

/// <summary>Round 6 — B2 batch capture endpoint: policy parity with /download,
/// per-item errors, caps, and the single-post regression after the shared
/// TryBuildCaptureItem refactor. One CaptureServer for the class (single :17530 bind).</summary>
public sealed class Round6Tests : IClassFixture<Round6Tests.BatchFixture>, IDisposable
{
    public sealed class BatchFixture : IDisposable
    {
        public const int TestPort = 17531;
        public CaptureServer Server { get; }
        public List<(string Url, string? Name, string? Referer, Dictionary<string, string> Headers, string? Title)> Singles { get; } = new();
        public List<List<CaptureServer.BatchCaptureItem>> Batches { get; } = new();

        public BatchFixture()
        {
            Server = new CaptureServer((url, name, referer, headers, title) =>
            {
                lock (Singles) Singles.Add((url, name, referer, headers, title));
            }, TestPort);
            Server.OnBatchCapture = items =>
            {
                lock (Batches) Batches.Add(items);
            };
            Server.Start();
        }

        public void Dispose()
        {
            try { Server.Dispose(); } catch { }
        }
    }

    private readonly BatchFixture _fx;

    public Round6Tests(BatchFixture fx)
    {
        _fx = fx;
        Assert.True(_fx.Server.IsRunning, $"Loopback :{BatchFixture.TestPort} is busy — retry the test run.");
        lock (_fx.Singles) _fx.Singles.Clear();
        lock (_fx.Batches) _fx.Batches.Clear();
    }

    public void Dispose() { }

    private static async Task<(HttpStatusCode Status, string Body)> PostAsync(string path, string json)
    {
        using var http = new HttpClient();
        using var req = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{BatchFixture.TestPort}{path}");
        req.Headers.Add("Origin", "chrome-extension://test");
        req.Content = new StringContent(json, Encoding.UTF8, "application/json");
        using var resp = await http.SendAsync(req);
        return (resp.StatusCode, await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task SingleDownload_StillAcceptedAfterRefactor()
    {
        var (status, body) = await PostAsync("/download",
            "{\"url\":\"https://cdn.example.com/v.mp4\",\"fileName\":\"v.mp4\",\"referer\":\"https://site.example.com/watch\",\"headers\":{\"Cookie\":\"a=b\",\"X-Evil\":\"1\"},\"streamType\":\"Video\",\"pageTitle\":\"  Great Video  \"}");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Contains("accepted", body);
        Assert.Single(_fx.Singles);
        var single = _fx.Singles[0];
        Assert.Equal("https://cdn.example.com/v.mp4", single.Url);
        Assert.Equal("v.mp4", single.Name);
        Assert.Equal("https://site.example.com/watch", single.Referer);
        Assert.Equal("a=b", single.Headers["Cookie"]);
        Assert.False(single.Headers.ContainsKey("X-Evil"));
        Assert.Equal("https://site.example.com", single.Headers["Origin"]);
        // "Video" is not a routable classification: only HLS/DASH/Stream/page map.
        Assert.False(single.Headers.ContainsKey("X-WDM-StreamType"));
        Assert.Equal("Great Video", single.Title);
    }

    [Fact]
    public async Task SingleDownload_BlobRejected()
    {
        var (status, _) = await PostAsync("/download", "{\"url\":\"blob:https://site.example.com/abc\"}");
        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Empty(_fx.Singles);
    }

    [Fact]
    public async Task Batch_AcceptsValidReportsInvalid()
    {
        var (status, body) = await PostAsync("/download/batch",
            "{\"items\":[" +
            "{\"url\":\"https://cdn.example.com/a.mp4\",\"fileName\":\"a.mp4\"}," +
            "{\"url\":\"blob:https://site.example.com/x\"}," +
            "{\"url\":\"https://cdn.example.com/b.m3u8\",\"streamType\":\"HLS\",\"headers\":{\"Cookie\":\"s=1\"}}" +
            "]}");
        Assert.Equal(HttpStatusCode.OK, status);
        using var doc = JsonDocument.Parse(body);
        Assert.Equal(2, doc.RootElement.GetProperty("accepted").GetInt32());
        Assert.Single(doc.RootElement.GetProperty("errors").EnumerateArray());
        Assert.Single(_fx.Batches);
        var batch = _fx.Batches[0];
        Assert.Equal(2, batch.Count);
        Assert.Equal("https://cdn.example.com/a.mp4", batch[0].Url);
        Assert.Equal("HLS", batch[1].Headers["X-WDM-StreamType"]);
        Assert.Equal("s=1", batch[1].Headers["Cookie"]);
    }

    [Fact]
    public async Task Batch_PrivateTargetRejectedWithoutToken()
    {
        var (status, body) = await PostAsync("/download/batch",
            "{\"items\":[" +
            "{\"url\":\"http://192.168.1.50/nas.mkv\"}," +
            "{\"url\":\"https://cdn.example.com/ok.mp4\"}" +
            "]}");
        Assert.Equal(HttpStatusCode.OK, status);
        using var doc = JsonDocument.Parse(body);
        Assert.Equal(1, doc.RootElement.GetProperty("accepted").GetInt32());
        Assert.Single(_fx.Batches);
        Assert.Equal("https://cdn.example.com/ok.mp4", _fx.Batches[0][0].Url);
    }

    [Fact]
    public async Task Batch_OverCapRejected()
    {
        var sb = new StringBuilder("{\"items\":[");
        for (int i = 0; i < 51; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append("{\"url\":\"https://cdn.example.com/f").Append(i).Append(".mp4\"}");
        }
        sb.Append("]}");
        var (status, _) = await PostAsync("/download/batch", sb.ToString());
        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Empty(_fx.Batches);
    }

    [Fact]
    public async Task Batch_EmptyRejected()
    {
        var (status, _) = await PostAsync("/download/batch", "{\"items\":[]}");
        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Empty(_fx.Batches);
    }

    [Fact]
    public async Task Batch_AllInvalidRejected()
    {
        var (status, _) = await PostAsync("/download/batch", "{\"items\":[{\"url\":\"\"},{\"url\":\"ftp://x/y\"}]}");
        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Empty(_fx.Batches);
    }

    [Fact]
    public async Task Batch_NoHandlerAnswers503()
    {
        _fx.Server.OnBatchCapture = null;
        try
        {
            var (status, _) = await PostAsync("/download/batch", "{\"items\":[{\"url\":\"https://cdn.example.com/a.mp4\"}]}");
            Assert.Equal(HttpStatusCode.ServiceUnavailable, status);
        }
        finally
        {
            _fx.Server.OnBatchCapture = items =>
            {
                lock (_fx.Batches) _fx.Batches.Add(items);
            };
        }
    }

    [Fact]
    public void AddTasks_BatchCreatesAndStarts()
    {
        var vm = new WDM.ViewModels.MainViewModel();
        try
        {
            vm.SuppressPersistence();
            vm.Tasks.Clear();
            vm.AddTasks(new List<CaptureServer.BatchCaptureItem>
            {
                new() { Url = "https://cdn.example.com/a.mp4", FileName = "a.mp4", Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) },
                new() { Url = "  ", FileName = "skip" },
                new() { Url = "https://cdn.example.com/b.mp4" },
            });
            Assert.Equal(2, vm.Tasks.Count);
            Assert.Equal("a.mp4", vm.Tasks[0].FileName);
            Assert.NotEqual(WDM.Models.TaskStatus.Paused, vm.Tasks[0].Status);
        }
        finally
        {
            try { vm.Engine.PauseAll(); } catch { }
            try { vm.Dispose(); } catch { }
        }
    }
}
