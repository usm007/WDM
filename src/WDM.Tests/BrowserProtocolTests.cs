// BrowserProtocolTests — IPC framing/validation + session state machine.
// Deterministic, no browser binary: the host side arrives in Phase 9.
using WDM.Browser.Ipc;
using WDM.Browser.Sessions;
using WDM.Tests.TestInfrastructure;
using Xunit;

namespace WDM.Tests;

public sealed class BrowserProtocolTests
{
    [Trait("Category", Cats.Unit)][Fact]
    public void Frame_RoundTrips()
    {
        byte[] frame = BrowserMessage.Encode(BrowserProtocol.Navigate, "s1", new { url = "https://example.com/" });
        Assert.True(BrowserMessage.TryDecode(frame, out var msg, out string? error), error);
        Assert.NotNull(msg);
        Assert.Equal(BrowserProtocol.Navigate, msg.Type);
        Assert.Equal("s1", msg.SessionId);
        Assert.NotNull(msg.Payload);
    }

    [Trait("Category", Cats.Unit)][Fact]
    public void Decode_RejectsShortFrame()
    {
        Assert.False(BrowserMessage.TryDecode(new byte[] { 1, 2 }, out _, out string? error));
        Assert.NotNull(error);
    }

    [Trait("Category", Cats.Unit)][Fact]
    public void Decode_RejectsLengthMismatch()
    {
        var frame = new byte[] { 9, 0, 0, 0, (byte)'{', (byte)'}' };
        Assert.False(BrowserMessage.TryDecode(frame, out _, out _));
    }

    [Trait("Category", Cats.Unit)][Fact]
    public void Decode_RejectsVersionMismatch()
    {
        byte[] body = System.Text.Encoding.UTF8.GetBytes("{\"v\":999,\"type\":\"Hello\"}");
        var frame = new byte[4 + body.Length];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(frame, body.Length);
        body.CopyTo(frame, 4);
        Assert.False(BrowserMessage.TryDecode(frame, out _, out string? error));
        Assert.Contains("version", error);
    }

    [Trait("Category", Cats.Unit)][Fact]
    public void Decode_RejectsMissingType()
    {
        byte[] body = System.Text.Encoding.UTF8.GetBytes("{\"v\":1}");
        var frame = new byte[4 + body.Length];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(frame, body.Length);
        body.CopyTo(frame, 4);
        Assert.False(BrowserMessage.TryDecode(frame, out _, out _));
    }

    [Trait("Category", Cats.Unit)][Fact]
    public void Decode_RejectsOversizeSessionId()
    {
        byte[] frame = BrowserMessage.Encode(BrowserProtocol.Hello, new string('x', 200), null);
        Assert.False(BrowserMessage.TryDecode(frame, out _, out string? error));
        Assert.Contains("session", error);
    }

    [Trait("Category", Cats.Unit)][Theory]
    [InlineData("http://example.com/v.mp4", true)]
    [InlineData("https://example.com/m.m3u8", true)]
    [InlineData("blob:https://example.com/x", true)]
    [InlineData("file:///etc/passwd", false)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("", false)]
    public void SafeUrl_FiltersSchemes(string url, bool ok)
    {
        Assert.Equal(ok, BrowserMessage.IsSafeUrl(url));
    }

    [Trait("Category", Cats.Unit)][Fact]
    public void SanitizeHeaders_CapsAndStrips()
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Evil"] = "a\r\nInjected: yes",
            ["Big"] = new string('y', 9000),
        };
        for (int i = 0; i < 150; i++)
            headers["X-H" + i] = "v";
        var clean = BrowserMessage.SanitizeHeaders(headers);
        Assert.Equal(BrowserMessage.MaxHeaders, clean.Count);
        Assert.DoesNotContain("Evil", clean.Keys);
        Assert.Equal(BrowserMessage.MaxHeaderBytes, clean["Big"].Length);
    }

    [Trait("Category", Cats.Unit)][Theory]
    [InlineData(BrowserSessionState.Created, BrowserSessionState.Starting, true)]
    [InlineData(BrowserSessionState.Starting, BrowserSessionState.Navigating, true)]
    [InlineData(BrowserSessionState.Navigating, BrowserSessionState.Loading, true)]
    [InlineData(BrowserSessionState.Loading, BrowserSessionState.Analyzing, true)]
    [InlineData(BrowserSessionState.Analyzing, BrowserSessionState.Resolving, true)]
    [InlineData(BrowserSessionState.Resolving, BrowserSessionState.CandidatesFound, true)]
    [InlineData(BrowserSessionState.CandidatesFound, BrowserSessionState.Completed, true)]
    [InlineData(BrowserSessionState.Resolving, BrowserSessionState.TimedOut, true)]
    [InlineData(BrowserSessionState.Created, BrowserSessionState.Completed, false)]
    [InlineData(BrowserSessionState.Completed, BrowserSessionState.Resolving, false)]
    [InlineData(BrowserSessionState.Failed, BrowserSessionState.Disposed, false)]
    [InlineData(BrowserSessionState.Disposed, BrowserSessionState.Created, false)]
    public void StateMachine_Transitions(BrowserSessionState from, BrowserSessionState to, bool ok)
    {
        Assert.Equal(ok, from.CanTransitionTo(to));
    }

    [Trait("Category", Cats.Unit)][Fact]
    public void SessionOptions_HaveSaneDefaults()
    {
        var o = new BrowserSessionOptions();
        Assert.True(o.StartupTimeout > TimeSpan.Zero);
        Assert.True(o.OverallTimeout >= o.DiscoveryTimeout);
        Assert.False(o.PersistentProfile);
        Assert.True(o.MaxNetworkEvents > 0);
    }
}
