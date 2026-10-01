// BrowserResolutionTests — Level-4 resolution against a fake browser host
// (deterministic, no browser binary) plus the fake-host plumbing itself.
using System.Runtime.CompilerServices;
using WDM.Browser.Contracts;
using WDM.Browser.Models;
using WDM.Browser.Sessions;
using WDM.Media;
using WDM.Services;
using WDM.Tests.TestInfrastructure;
using Xunit;

namespace WDM.Tests;

public sealed class BrowserResolutionTests
{
    private sealed class FakeSession : IBrowserSession
    {
        private readonly List<BrowserNetworkEvent> _events;
        private readonly Dictionary<string, byte[]> _bodies;
        private readonly string _title;
        private readonly bool _crash;

        public FakeSession(List<BrowserNetworkEvent> events, Dictionary<string, byte[]> bodies, string title, bool crash = false)
        {
            _events = events;
            _bodies = bodies;
            _title = title;
            _crash = crash;
        }

        public string SessionId { get; } = "fake";
        public BrowserSessionState State { get; private set; } = BrowserSessionState.Created;
        public event Action<BrowserSessionState>? StateChanged;

        public async IAsyncEnumerable<BrowserNetworkEvent> NavigateAsync(string url,
            [EnumeratorCancellation] CancellationToken ct)
        {
            State = BrowserSessionState.Loading;
            if (_crash)
                throw new InvalidOperationException("Browser renderer terminated during analysis.");
            foreach (var e in _events)
            {
                ct.ThrowIfCancellationRequested();
                await Task.Yield();
                yield return e;
            }
            State = BrowserSessionState.Completed;
            StateChanged?.Invoke(State);
        }

        public Task<string?> ExecuteScriptAsync(string script, CancellationToken ct) => Task.FromResult<string?>(_title);

        public Task<byte[]?> FetchBodyAsync(string url, CancellationToken ct) =>
            Task.FromResult<byte[]?>(_bodies.TryGetValue(url, out byte[]? b) ? b : null);

        public Task CloseAsync()
        {
            State = BrowserSessionState.Disposed;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => new(CloseAsync());
    }

    private sealed class FakeHost : IBrowserHost
    {
        private readonly Func<IBrowserSession> _sessions;
        public FakeHost(Func<IBrowserSession> sessions) => _sessions = sessions;
        public BrowserHostState State => BrowserHostState.Ready;
        public event Action<BrowserHostState>? StateChanged;
        public Task StartAsync(BrowserSessionOptions options, CancellationToken ct) => Task.CompletedTask;
        public Task StopAsync() => Task.CompletedTask;
        public Task RestartAsync(BrowserSessionOptions options, CancellationToken ct) => Task.CompletedTask;
        public Task<IBrowserSession> CreateSessionAsync(BrowserSessionOptions options, CancellationToken ct) =>
            Task.FromResult(_sessions());
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static BrowserNetworkEvent Evt(string url, string contentType, string resourceType = "Xhr", long len = 0) =>
        new()
        {
            RequestId = Guid.NewGuid().ToString("N"),
            Timestamp = DateTimeOffset.UtcNow,
            Url = url,
            Method = "GET",
            StatusCode = 200,
            ContentType = contentType,
            ContentLength = len > 0 ? len : null,
            ResourceType = resourceType,
        };

    private static Func<IBrowserHost> UseFake(Func<IBrowserSession> sessions)
    {
        var prior = MediaEnvironment.BrowserHostFactory;
        MediaEnvironment.BrowserHostFactory = () => new FakeHost(sessions);
        return () => { MediaEnvironment.BrowserHostFactory = prior; return new FakeHost(sessions); };
    }

    /// <summary>Redirects the (static) data home at Core AppPaths so profile
    /// temp dirs never touch the real %LocalAppData%\WDM-Data.</summary>
    private static Action UseTempDataDir(out string dir)
    {
        dir = Path.Combine(Path.GetTempPath(), "wdm_l4_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string prior = WDM.Services.AppPaths.DataDir;
        WDM.Services.AppPaths.DataDir = dir;
        string captured = dir;
        return () =>
        {
            WDM.Services.AppPaths.DataDir = prior;
            try { Directory.Delete(captured, recursive: true); } catch { }
        };
    }

    [Trait("Category", Cats.Unit)][Fact]
    public async Task L4_BrowserMedia_ResolvesDirectAndManifest()
    {
        const string master = "https://cdn.example.com/master.m3u8";
        var events = new List<BrowserNetworkEvent>
        {
            Evt("https://example.com/watch/1", "text/html", "MainFrame"),
            Evt("https://cdn.example.com/v.mp4", "video/mp4", "Media", 9_000_000),
            Evt(master, "application/vnd.apple.mpegurl", "Xhr", 200),
        };
        var bodies = new Dictionary<string, byte[]>
        {
            [master] = System.Text.Encoding.UTF8.GetBytes("#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=800000,RESOLUTION=640x360\n360p.m3u8\n"),
        };
        var restore = UseFake(() => new FakeSession(events, bodies, "Browser Film"));
        var restoreDir = UseTempDataDir(out _);
        try
        {
            var res = await BrowserResolver.TryResolveAsync("https://example.com/watch/1", null, null, CancellationToken.None);
            Assert.NotNull(res);
            Assert.Equal(ResolutionStatus.Resolved, res.Status);
            Assert.Equal("Browser Film", res.Title);
            Assert.Contains(res.Variants, v => v.MediaUrl == "https://cdn.example.com/v.mp4" && !v.RequiresHls);
            var hls = res.Variants.FirstOrDefault(v => v.RequiresHls);
            Assert.NotNull(hls);
            Assert.Equal(master, hls.ManifestUrl);
        }
        finally
        {
            restore();
            restoreDir();
        }
    }

    [Trait("Category", Cats.Unit)][Fact]
    public async Task L4_DrmManifest_ReportsProtected()
    {
        const string mpd = "https://cdn.example.com/manifest.mpd";
        var events = new List<BrowserNetworkEvent> { Evt(mpd, "application/dash+xml", "Xhr") };
        var bodies = new Dictionary<string, byte[]>
        {
            [mpd] = System.Text.Encoding.UTF8.GetBytes("<MPD><Period><AdaptationSet><ContentProtection/></AdaptationSet></Period></MPD>"),
        };
        var restore = UseFake(() => new FakeSession(events, bodies, "DRM Film"));
        var restoreDir = UseTempDataDir(out _);
        try
        {
            var res = await BrowserResolver.TryResolveAsync("https://example.com/watch/9", null, null, CancellationToken.None);
            Assert.NotNull(res);
            Assert.Equal(ResolutionStatus.DrmProtected, res.Status);
        }
        finally
        {
            restore();
            restoreDir();
        }
    }

    [Trait("Category", Cats.Unit)][Fact]
    public async Task L4_NoMedia_ReturnsNull()
    {
        var events = new List<BrowserNetworkEvent>
        {
            Evt("https://example.com/page", "text/html", "MainFrame"),
            Evt("https://example.com/app.js", "application/javascript", "Script"),
        };
        var restore = UseFake(() => new FakeSession(events, new Dictionary<string, byte[]>(), "Plain Page"));
        var restoreDir = UseTempDataDir(out _);
        try
        {
            Assert.Null(await BrowserResolver.TryResolveAsync("https://example.com/page", null, null, CancellationToken.None));
        }
        finally
        {
            restore();
            restoreDir();
        }
    }

    [Trait("Category", Cats.Unit)][Fact]
    public async Task L4_Crash_RetriesOnceThenGivesUp()
    {
        int calls = 0;
        var restore = UseFake(() =>
        {
            calls++;
            return new FakeSession(new List<BrowserNetworkEvent>(), new Dictionary<string, byte[]>(), "", crash: true);
        });
        var restoreDir = UseTempDataDir(out _);
        try
        {
            Assert.Null(await BrowserResolver.TryResolveAsync("https://example.com/crash", null, null, CancellationToken.None));
            Assert.Equal(2, calls);
        }
        finally
        {
            restore();
            restoreDir();
        }
    }

    [Trait("Category", Cats.Unit)][Fact]
    public void IsAvailable_FalseWithoutHostBinary()
    {
        // No BrowserHost.exe ships next to the test assembly: L4 must report
        // unavailable rather than crash. (The App directory has the real one.)
        Assert.False(File.Exists(System.IO.Path.Combine(AppContext.BaseDirectory, "WDM.BrowserHost.exe")));
        Assert.False(BrowserResolver.IsAvailable());
    }
}
