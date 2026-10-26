using WDM.Services;
using WDM.Services.Chunking;
using WDM.Tests.TestInfrastructure;
using Xunit;

namespace WDM.Tests;

/// <summary>Small pure helpers behind the optional clipboard and per-host
/// settings UI. No window or clipboard is created here.</summary>
public sealed class ClipboardAndHostPolicyTests
{
    [Trait("Category", Cats.Unit)]
    [Theory]
    [InlineData("https://example.com/file.zip", "https://example.com/file.zip")]
    [InlineData("See [the file](https://example.com/a.bin).", "https://example.com/a.bin")]
    [InlineData("  http://cdn.test/path?x=1&y=2  ", "http://cdn.test/path?x=1&y=2")]
    [InlineData("https://example.com/end).", "https://example.com/end")]
    public void Clipboard_Extracts_Http_Url(string text, string expected)
    {
        Assert.Equal(expected, ClipboardWatcher.ExtractHttpUrl(text));
    }

    [Trait("Category", Cats.Unit)]
    [Theory]
    [InlineData("not a link")]
    [InlineData("ftp://example.com/file.zip")]
    [InlineData("javascript:alert(1)")]
    [InlineData("https://")]
    public void Clipboard_Rejects_Non_Http_Or_Incomplete(string text)
    {
        Assert.Null(ClipboardWatcher.ExtractHttpUrl(text));
    }

    [Trait("Category", Cats.Unit)]
    [Fact]
    public void Host_Limits_Parse_And_Normalize()
    {
        var parsed = OptionsControl.ParseHostLimits(
            "example.com=2\nhttps://cdn.example.com:443=4\n# comment\nbad line\nhttps://zero.test=0\nhttp://big.test=99");

        Assert.Equal(1, parsed[OriginController.KeyOf("https://zero.test")]);
        Assert.Equal(2, parsed[OriginController.KeyOf("https://example.com")]);
        Assert.Equal(4, parsed[OriginController.KeyOf("https://cdn.example.com")]);
        Assert.Equal(32, parsed[OriginController.KeyOf("http://big.test")]);
        Assert.DoesNotContain("bad line", parsed.Keys);
    }
}
