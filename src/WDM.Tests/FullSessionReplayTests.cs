// FullSessionReplayTests — per-site page-session approval: engine forwards
// cross-host credentials only when flagged; the server approves only
// allow-listed page hosts (extension flag is advisory).
using System.Net.Http.Headers;
using System.Reflection;
using WDM.Models;
using WDM.Services;
using WDM.Tests.TestInfrastructure;
using TaskStatus = WDM.Models.TaskStatus;

namespace WDM.Tests;

[Collection("TaskStoreState")]
public sealed class FullSessionReplayTests : IDisposable
{
    private readonly string _appDir = Path.Combine(Path.GetTempPath(), "wdm_fsr_" + Guid.NewGuid().ToString("N"));
    private readonly string _realAppDir;

    public FullSessionReplayTests()
    {
        _realAppDir = TaskStore.AppDir;
        Directory.CreateDirectory(_appDir);
        TaskStore.AppDir = _appDir;
    }

    public void Dispose()
    {
        TaskStore.AppDir = _realAppDir;
        try { Directory.Delete(_appDir, recursive: true); } catch { }
    }

    private static DownloadTask Task(bool fullSession) => new()
    {
        Url = "https://cdn.example.com/file.bin",
        FileName = "file.bin",
        SaveFolder = Path.GetTempPath(),
        Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Cookie"] = "sid=abc",
            ["Authorization"] = "Bearer xyz",
        },
        FullSessionReplay = fullSession,
    };

    [Trait("Category", Cats.Unit)][Fact]
    public void Default_Strips_CrossHost_Credentials()
    {
        using var req = DownloadEngine.BuildRequest(HttpMethod.Get,
            Task(false), new RangeHeaderValue(0, 0), "https://mirror.example.net/file.bin");
        Assert.False(req.Headers.Contains("Cookie"));
        Assert.False(req.Headers.Contains("Authorization"));
    }

    [Trait("Category", Cats.Unit)][Fact]
    public void ApprovedTask_Forwards_CrossHost_Credentials()
    {
        using var req = DownloadEngine.BuildRequest(HttpMethod.Get,
            Task(true), new RangeHeaderValue(0, 0), "https://mirror.example.net/file.bin");
        Assert.True(req.Headers.Contains("Cookie"));
        Assert.True(req.Headers.Contains("Authorization"));
    }

    [Trait("Category", Cats.Unit)][Fact]
    public void ApprovedTask_Keeps_SameHost_Behavior()
    {
        using var req = DownloadEngine.BuildRequest(HttpMethod.Get,
            Task(true), new RangeHeaderValue(0, 0), "https://cdn.example.com/file.bin");
        Assert.True(req.Headers.Contains("Cookie"));
    }

    private static bool Approved(string? referer, string? pageUrl, string[] hosts)
    {
        var settings = TaskStore.LoadSettings();
        settings.FullSessionReplayHosts = hosts.ToList();
        TaskStore.SaveSettings(settings);
        var payloadType = typeof(CaptureServer).GetNestedType("CapturePayload", BindingFlags.NonPublic)!;
        var payload = Activator.CreateInstance(payloadType)!;
        payloadType.GetProperty("Referer")!.SetValue(payload, referer);
        payloadType.GetProperty("PageUrl")!.SetValue(payload, pageUrl);
        var m = typeof(CaptureServer).GetMethod("IsFullSessionApproved",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        return (bool)m.Invoke(null, new object?[] { payload, referer })!;
    }

    [Trait("Category", Cats.Unit)][Fact]
    public void Server_Approves_Only_AllowListed_Page_Hosts()
    {
        Assert.True(Approved("https://videos.test/watch/1", null, new[] { "videos.test" }));
        Assert.True(Approved("https://sub.videos.test/x", null, new[] { "videos.test" }));
        Assert.False(Approved("https://evil.test/x", null, new[] { "videos.test" }));
        Assert.False(Approved("https://videos.test/x", null, Array.Empty<string>()));
        Assert.False(Approved(null, null, new[] { "videos.test" }));
    }
}
