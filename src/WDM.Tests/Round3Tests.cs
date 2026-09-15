using System.IO;
using System.Text;
using System.Text.Json;
using WDM.Models;
using WDM.Services;
using WDM.Services.Embed;
using WdmTaskStatus = WDM.Models.TaskStatus;

namespace WDM.Tests;

public sealed class Round3Tests
{
    private static JsonSerializerOptions LenientOptions()
    {
        var opts = new JsonSerializerOptions();
        opts.Converters.Add(new LenientEnumConverterFactory());
        return opts;
    }

    // ── SSRF block-list hardening (mapped-v4, unspecified, short, hex) ──
    [Theory]
    [InlineData("http://[::ffff:127.0.0.1]/x")]  // IPv4-mapped loopback
    [InlineData("http://[::ffff:10.0.0.1]/x")]   // IPv4-mapped private
    [InlineData("http://[::]/x")]                // unspecified
    [InlineData("http://127.1/x")]               // short-form loopback
    [InlineData("http://10.1/x")]                // short-form private
    [InlineData("http://0xc0.0xa8.1.1/x")]       // per-part hex private
    [InlineData("http://2130706433/x")]          // decimal-integer loopback
    public void BlockedSpellings_AreBlocked(string url)
    {
        Assert.True(CaptureServer.IsBlockedResolveTarget(url), url);
    }

    [Theory]
    [InlineData("http://8.8.8.8/x")]             // public IPv4 literal (no DNS)
    [InlineData("http://1.1.1.1/x")]             // public IPv4 literal (no DNS)
    [InlineData("http://[2001:db8::1]/x")]       // public IPv6 doc-range
    public void PublicLiterals_AreAllowed(string url)
    {
        Assert.False(CaptureServer.IsBlockedResolveTarget(url), url);
    }

    // ── Lenient enum: numeric strings must not bypass IsDefined ──────────
    [Fact]
    public void LenientEnum_NumericString_OutOfRange_FallsBackToDefault()
    {
        var opts = LenientOptions();
        Assert.Equal(default(WdmTaskStatus), JsonSerializer.Deserialize<WdmTaskStatus>("\"99\"", opts));
        Assert.Equal(default(PriorityLevel), JsonSerializer.Deserialize<PriorityLevel>("\"7\"", opts));
    }

    [Fact]
    public void LenientEnum_NumericString_InRange_Parses()
    {
        var opts = LenientOptions();
        Assert.Equal(WdmTaskStatus.Downloading, JsonSerializer.Deserialize<WdmTaskStatus>("\"1\"", opts));
    }

    [Fact]
    public void LenientEnum_NamedValue_Parses()
    {
        var opts = LenientOptions();
        Assert.Equal(WdmTaskStatus.Paused, JsonSerializer.Deserialize<WdmTaskStatus>("\"Paused\"", opts));
        Assert.Equal(default(WdmTaskStatus), JsonSerializer.Deserialize<WdmTaskStatus>("\"Nope\"", opts));
    }

    // ── Embed bounded body read ──────────────────────────────────────────
    [Fact]
    public async Task ReadCappedStringAsync_TruncatesAtCap()
    {
        using var content = new StringContent(new string('a', EmbedResolver.MaxBodyBytes + 1000), Encoding.UTF8);
        string body = await EmbedResolver.ReadCappedStringAsync(content, CancellationToken.None);
        Assert.Equal(EmbedResolver.MaxBodyBytes, Encoding.UTF8.GetByteCount(body));
    }

    [Fact]
    public async Task ReadCappedStringAsync_SmallBody_PassesThrough()
    {
        using var content = new StringContent("<html>hi</html>", Encoding.UTF8);
        string body = await EmbedResolver.ReadCappedStringAsync(content, CancellationToken.None);
        Assert.Equal("<html>hi</html>", body);
    }

    // ── HLS stale-temp sweep never deletes the live dir ──────────────────
    [Fact]
    public void CleanStaleTempDirs_SkipsActiveDir()
    {
        string root = Path.Combine(Path.GetTempPath(), "wdm-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string active = Path.Combine(root, ".wdmseg_active");
            string stale = Path.Combine(root, ".wdmseg_stale");
            Directory.CreateDirectory(active);
            Directory.CreateDirectory(stale);
            Directory.SetLastWriteTimeUtc(active, DateTime.UtcNow - TimeSpan.FromHours(2));
            Directory.SetLastWriteTimeUtc(stale, DateTime.UtcNow - TimeSpan.FromHours(2));

            HlsDownloader.CleanStaleTempDirs(root, active);

            Assert.True(Directory.Exists(active));
            Assert.False(Directory.Exists(stale));
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }
}
