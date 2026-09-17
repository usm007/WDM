using System.Net;
using System.Text.Json;
using WDM.Services;

namespace WDM.Tests;

/// <summary>Round 13 — minimum auto-catch size: setting default/validation,
/// manual-entry parsing, /ping advertisement to the extension.</summary>
public sealed class Round13Tests
{
    private const long MB = 1024L * 1024;
    private const long GB = 1024L * 1024 * 1024;

    [Fact]
    public void MinCatchSize_DefaultIs200MB()
    {
        Assert.Equal(200 * MB, new AppSettings().MinCatchSizeBytes);
    }

    [Fact]
    public void MinCatchSize_LegacyJsonDefaults()
    {
        var back = JsonSerializer.Deserialize<AppSettings>("{\"MaxRetries\":5}")!;
        Assert.Equal(200 * MB, back.MinCatchSizeBytes);
    }

    [Theory]
    [InlineData("200", 200 * MB)]
    [InlineData("200 MB", 200 * MB)]
    [InlineData("200mb", 200 * MB)]
    [InlineData("200m", 200 * MB)]
    [InlineData("1 GB", 1 * GB)]
    [InlineData("1GB", 1 * GB)]
    [InlineData("1.5 GB", 1610612736L)]
    [InlineData("1g", 1 * GB)]
    [InlineData("1024 KB", 1024 * 1024L)]
    [InlineData("512k", 512 * 1024L)]
    [InlineData("100b", 100L)]
    [InlineData("0", 0L)]
    [InlineData("0 (catch all)", 0L)]
    public void TryParseCatchSize_Matrix(string text, long expected)
    {
        Assert.True(WDM.OptionsControl.TryParseCatchSize(text, out long bytes), text);
        Assert.Equal(expected, bytes);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("abc")]
    [InlineData("-5")]
    [InlineData("10 XB")]
    public void TryParseCatchSize_Rejects(string? text)
    {
        Assert.False(WDM.OptionsControl.TryParseCatchSize(text, out _));
    }

    [Fact]
    public void TryParseCatchSize_ClampsHuge()
    {
        Assert.True(WDM.OptionsControl.TryParseCatchSize("20 GB", out long bytes));
        Assert.Equal(10 * GB, bytes);
    }

    [Theory]
    [InlineData(0L, "0 (catch all)")]
    [InlineData(200 * MB, "200 MB")]
    [InlineData(1 * GB, "1 GB")]
    public void FormatCatchSize_Matrix(long bytes, string expected)
    {
        Assert.Equal(expected, WDM.OptionsControl.FormatCatchSize(bytes));
    }

    [Fact]
    public void ValidateSettings_ClampsMinCatch()
    {
        var m = typeof(TaskStore).GetMethod("ValidateSettings",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        AppSettings Call(AppSettings s) => (AppSettings)m.Invoke(null, new object[] { s })!;
        Assert.Equal(0, Call(new AppSettings { MinCatchSizeBytes = -5 }).MinCatchSizeBytes);
        Assert.Equal(10 * GB, Call(new AppSettings { MinCatchSizeBytes = long.MaxValue }).MinCatchSizeBytes);
    }

    [Fact]
    public async Task Ping_AdvertisesMinCatchBytes()
    {
        using var server = new PingServer();
        Assert.True(server.Running);
        using var http = new HttpClient();
        using var resp = await http.GetAsync($"http://127.0.0.1:{PingServer.Port}/ping");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.Equal("ok", doc.RootElement.GetProperty("status").GetString());
        Assert.Equal(200 * MB, doc.RootElement.GetProperty("minCatchBytes").GetInt64());
    }

    private sealed class PingServer : IDisposable
    {
        public const int Port = 17533;
        private readonly CaptureServer _server;
        public bool Running => _server.IsRunning;

        public PingServer()
        {
            _server = new CaptureServer((_, _, _, _, _) => { }, Port);
            _server.MinCatchBytesProvider = () => 200 * MB;
            _server.Start();
        }

        public void Dispose()
        {
            try { _server.Dispose(); } catch { }
        }
    }
}
