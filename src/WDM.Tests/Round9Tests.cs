using System.Net;
using System.Text;
using System.Text.Json;
using WDM.Services;
using TaskStatus = WDM.Models.TaskStatus;

namespace WDM.Tests;

/// <summary>Round 9 — P6: blob chunk assembly/security (B5), MEGA sid capture
/// (B6a), blob import as finished file. Server tests run on :17532 so the
/// developer's running app on :17530 is never disturbed.</summary>
public sealed class Round9Tests : IClassFixture<Round9Tests.BlobFixture>
{
    public sealed class BlobFixture : IDisposable
    {
        public const int TestPort = 17532;
        public CaptureServer Server { get; }
        public List<CaptureServer.BlobResult> Blobs { get; } = new();

        public BlobFixture()
        {
            Server = new CaptureServer((_, _, _, _, _) => { }, TestPort);
            Server.OnBlobCaptured = r =>
            {
                lock (Blobs) Blobs.Add(r);
                return true;
            };
            Server.Start();
        }

        public void Dispose()
        {
            try { Server.Dispose(); } catch { }
        }
    }

    private readonly BlobFixture _fx;

    public Round9Tests(BlobFixture fx)
    {
        _fx = fx;
        Assert.True(_fx.Server.IsRunning, "Test loopback port is busy — retry the test run.");
        lock (_fx.Blobs) _fx.Blobs.Clear();
    }

    private static async Task<(HttpStatusCode Status, string Body)> PostAsync(string path, string json)
    {
        using var http = new HttpClient();
        using var req = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{BlobFixture.TestPort}{path}");
        req.Headers.Add("Origin", "chrome-extension://test");
        req.Content = new StringContent(json, Encoding.UTF8, "application/json");
        using var resp = await http.SendAsync(req);
        return (resp.StatusCode, await resp.Content.ReadAsStringAsync());
    }

    private static string ChunkJson(string id, int seq, bool last, string mime, byte[] data,
        string? fileName = "clip.mp4", string? referer = "https://site.example.com/watch") =>
        JsonSerializer.Serialize(new
        {
            id, seq, last, mime,
            data = Convert.ToBase64String(data),
            fileName, referer,
            pageTitle = "Test Video",
        });

    // ---------- B5: assembly ----------

    [Fact]
    public async Task Blob_OrderedChunks_AssembleAndFire()
    {
        byte[] part1 = Encoding.UTF8.GetBytes(new string('a', 100));
        byte[] part2 = Encoding.UTF8.GetBytes(new string('b', 50));
        var (s1, _) = await PostAsync("/download/blob-chunk", ChunkJson("t1", 0, false, "video/mp4", part1));
        Assert.Equal(HttpStatusCode.OK, s1);
        var (s2, body2) = await PostAsync("/download/blob-chunk", ChunkJson("t1", 1, true, "video/mp4", part2));
        Assert.Equal(HttpStatusCode.OK, s2);
        using var doc = JsonDocument.Parse(body2);
        Assert.True(doc.RootElement.GetProperty("staged").GetBoolean());
        Assert.Equal(150, doc.RootElement.GetProperty("bytes").GetInt64());

        Assert.Single(_fx.Blobs);
        var blob = _fx.Blobs[0];
        Assert.Equal(150, blob.TotalBytes);
        Assert.Equal("video/mp4", blob.Mime);
        Assert.Equal("clip.mp4", blob.FileName);
        Assert.True(File.Exists(blob.StagedPath));
        byte[] staged = await File.ReadAllBytesAsync(blob.StagedPath);
        Assert.Equal(part1.Concat(part2).ToArray(), staged);
        try { File.Delete(blob.StagedPath); } catch { }
    }

    [Fact]
    public async Task Blob_WrongSeqRejectedSessionKept()
    {
        byte[] part = Encoding.UTF8.GetBytes("hello");
        var (s1, _) = await PostAsync("/download/blob-chunk", ChunkJson("t2", 0, false, "video/mp4", part));
        Assert.Equal(HttpStatusCode.OK, s1);
        // Skip seq 1 -> 400, session survives for the retry.
        var (s2, _) = await PostAsync("/download/blob-chunk", ChunkJson("t2", 2, true, "video/mp4", part));
        Assert.Equal(HttpStatusCode.BadRequest, s2);
        var (s3, _) = await PostAsync("/download/blob-chunk", ChunkJson("t2", 1, true, "video/mp4", part));
        Assert.Equal(HttpStatusCode.OK, s3);
        Assert.Single(_fx.Blobs);
        try { File.Delete(_fx.Blobs[0].StagedPath); } catch { }
    }

    [Theory]
    [InlineData("bad id!", 0, "video/mp4", "aGVsbG8=", false)]   // invalid id chars
    [InlineData("t3", 0, "video/mp4", "!!!not-base64!!!", false)] // invalid base64
    [InlineData("t3", 0, "application/x-msdownload", "aGVsbG8=", false)] // disallowed mime
    [InlineData("t3", -1, "video/mp4", "aGVsbG8=", false)]        // negative seq
    [InlineData("t3", 0, "video/mp4", "aGVsbG8=", true)]          // valid control
    public async Task Blob_ValidationMatrix(string id, int seq, string mime, string data, bool ok)
    {
        string json = JsonSerializer.Serialize(new { id, seq, last = true, mime, data });
        var (status, _) = await PostAsync("/download/blob-chunk", json);
        Assert.Equal(ok ? HttpStatusCode.OK : HttpStatusCode.BadRequest, status);
        if (ok)
        {
            Assert.Single(_fx.Blobs);
            try { File.Delete(_fx.Blobs[0].StagedPath); } catch { }
        }
        else
        {
            Assert.Empty(_fx.Blobs);
        }
    }

    [Fact]
    public async Task Blob_WebOriginForbidden()
    {
        using var http = new HttpClient();
        using var req = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{BlobFixture.TestPort}/download/blob-chunk");
        req.Headers.Add("Origin", "https://evil.example.com");
        req.Content = new StringContent(ChunkJson("t9", 0, true, "video/mp4", new byte[] { 1 }));
        using var resp = await http.SendAsync(req);
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
        Assert.Empty(_fx.Blobs);
    }

    // ---------- B6a: MEGA sid ----------

    [Theory]
    [InlineData("https://mega.nz/file/abc", true)]
    [InlineData("https://www.mega.nz/login", true)]
    [InlineData("https://g.api.mega.co.nz/cs", true)]
    [InlineData("https://example.com/", false)]
    [InlineData("https://mega.nz.evil.com/", false)]
    [InlineData("not a url", false)]
    [InlineData(null, false)]
    public void IsMegaHost_Matrix(string? url, bool expected)
    {
        Assert.Equal(expected, CaptureServer.IsMegaHost(url));
    }

    [Fact]
    public async Task MegaSid_ValidAcceptedInvalidRejected()
    {
        var (ok, _) = await PostAsync("/download/mega-sid",
            JsonSerializer.Serialize(new { sid = "AbC123_-token-Value.9", host = "https://mega.nz" }));
        Assert.Equal(HttpStatusCode.OK, ok);
        Assert.True(CaptureServer.TryGetMegaSid(out string? sid));
        Assert.Equal("AbC123_-token-Value.9", sid);

        var (badHost, _) = await PostAsync("/download/mega-sid",
            JsonSerializer.Serialize(new { sid = "AbC123_-token-Value.9", host = "https://evil.example.com" }));
        Assert.Equal(HttpStatusCode.BadRequest, badHost);

        var (badSid, _) = await PostAsync("/download/mega-sid",
            JsonSerializer.Serialize(new { sid = "x", host = "https://mega.nz" }));
        Assert.Equal(HttpStatusCode.BadRequest, badSid);

        // The earlier valid sid is untouched by the rejections.
        Assert.True(CaptureServer.TryGetMegaSid(out string? still));
        Assert.Equal("AbC123_-token-Value.9", still);
    }

    // ---------- B5: import ----------

    [Fact]
    public void AddCompletedFile_ImportsStagedAsFinishedRow()
    {
        var vm = new WDM.ViewModels.MainViewModel();
        try
        {
            vm.SuppressPersistence();
            vm.Tasks.Clear();
            string dest = Path.Combine(Path.GetTempPath(), "wdm_r9_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dest);
            vm.Settings.DownloadFolder = dest;
            string staged = Path.Combine(Path.GetTempPath(), "wdm_r9s_" + Guid.NewGuid().ToString("N") + ".mp4");
            try
            {
                File.WriteAllBytes(staged, Encoding.UTF8.GetBytes("blob-bytes-here"));
                var completed = new ManualResetEventSlim(false);
                vm.TaskCompleted += _ => completed.Set();
                vm.AddCompletedFile(new CaptureServer.BlobResult
                {
                    StagedPath = staged,
                    FileName = "My Video",
                    Mime = "video/mp4",
                    Referer = "https://site.example.com/watch",
                    PageTitle = "My Video",
                    TotalBytes = 15,
                });
                Assert.True(completed.Wait(TimeSpan.FromSeconds(5)));
                Assert.Single(vm.Tasks);
                var task = vm.Tasks[0];
                Assert.Equal(TaskStatus.Completed, task.Status);
                Assert.Equal(15, task.TotalBytes);
                Assert.True(File.Exists(task.FullPath));
                Assert.Equal("blob-bytes-here", File.ReadAllText(task.FullPath));
                Assert.False(File.Exists(staged)); // staging cleaned
            }
            finally
            {
                try { Directory.Delete(dest, recursive: true); } catch { }
                try { if (File.Exists(staged)) File.Delete(staged); } catch { }
            }
        }
        finally
        {
            try { vm.Dispose(); } catch { }
        }
    }
}
