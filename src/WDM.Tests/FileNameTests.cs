using WDM.Models;
using WDM.Services;

namespace WDM.Tests;

public sealed class FileNameTests
{
    [Theory]
    [InlineData("../evil.mp4")]
    [InlineData("..\\evil.mp4")]
    [InlineData("CON.mp4")]
    [InlineData(".hidden.mp4")]
    public void SmartSanitize_NeverReturnsTraversalOrDeviceName(string input)
    {
        string out1 = FileNameHelper.SmartSanitizeFileName(input);
        Assert.False(out1.Contains(".."), out1);
        Assert.False(out1.Contains('/') || out1.Contains('\\'), out1);
        Assert.DoesNotMatch(@"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(\.|$)", out1.ToUpperInvariant());
    }

    [Theory]
    [InlineData("movie.mp4", DownloadCategory.Video)]
    [InlineData("song.mp3", DownloadCategory.Music)]
    [InlineData("doc.pdf", DownloadCategory.Document)]
    public void Categorize_MapsExtensions(string file, DownloadCategory expected)
    {
        Assert.Equal(expected, DownloadTask.Categorize(file));
    }

    [Fact]
    public void SmartSanitize_WhitespaceReturnsEmpty_EngineAppliesFallback()
    {
        // Contract: SmartSanitize returns "" for blank; DownloadEngine.SanitizeFileName applies download_ fallback.
        Assert.Equal("", FileNameHelper.SmartSanitizeFileName("   "));
        Assert.StartsWith("download_", WDM.Services.DownloadEngine.SanitizeFileName("   "));
    }
}
