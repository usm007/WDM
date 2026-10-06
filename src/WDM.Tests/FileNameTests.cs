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

    [Fact]
    public void SmartSanitize_NonGenericName_IsNotOverwrittenByPageTitle()
    {
        string input = "Green.Lantern.Beware.My.Power.2022.720p.BluRay.800MB.x264-GalaxyRG.mkv";
        string pageTitle = "Seedr: Fetch Files to Your Cloud, View and Save on Any Device";
        string referer = "https://www.seedr.cc/";

        string result = FileNameHelper.SmartSanitizeFileName(input, pageTitle, referer);

        Assert.Contains("Green Lantern", result);
        Assert.DoesNotContain("Seedr", result);
    }

    [Fact]
    public void SmartSanitize_GenericName_UsesPageTitle()
    {
        string input = "master.m3u8";
        string pageTitle = "Legion 720p - Official Player";
        string referer = "https://example.com/watch/legion";

        string result = FileNameHelper.SmartSanitizeFileName(input, pageTitle, referer);

        Assert.Contains("Legion", result);
    }

    [Fact]
    public void SmartSanitize_GenericHashName_UsesPageTitle()
    {
        string input = "168095063_480p_h264_init_k5Opf1yJuSMMz3rv.mp4";
        string pageTitle = "My Movie 2024";

        string result = FileNameHelper.SmartSanitizeFileName(input, pageTitle);

        Assert.Contains("My Movie 2024", result);
    }

    [Theory]
    [InlineData("I Edc EEPJ · Gofile", "I Edc EEPJ")]
    [InlineData("Some Movie (2024) - Mega", "Some Movie (2024)")]
    [InlineData("Clip | MediaFire", "Clip")]
    [InlineData("Show - Google Drive", "Show")]
    public void CleanPageTitle_StripsFileHostBranding(string title, string expected)
    {
        Assert.Equal(expected, FileNameHelper.CleanPageTitle(title));
    }

    [Fact]
    public void CleanPageTitle_KeepsHostWordInsideRealTitle()
    {
        Assert.Equal("Mega Shark vs Giant Octopus", FileNameHelper.CleanPageTitle("Mega Shark vs Giant Octopus"));
    }

    [Fact]
    public void DescriptiveStemFromUrl_ExtensionlessTitleTail()
    {
        string? stem = FileNameHelper.DescriptiveStemFromUrl(
            "https://file-na-lax-1.gofile.io/download/web/982bae6b-ca2c-4e6e-85f4-7fd43b368d71/Spider-Man%20-%20Brand%20New%20Day%20(2026");
        Assert.Equal("Spider-Man - Brand New Day (2026", stem);
    }

    [Theory]
    [InlineData("https://cdn.example.com/dl/982bae6bca2c4e6e85f47fd43b368d71")]
    [InlineData("https://cdn.example.com/dl/a1b2c3d4e5f60718293a4b5c6d7e8f90")]
    [InlineData("https://cdn.example.com/dl/12345678")]
    [InlineData("https://cdn.example.com/dl/x")]
    [InlineData("https://cdn.example.com/dl/video")]
    public void DescriptiveStemFromUrl_RejectsTokens(string url)
    {
        Assert.Null(FileNameHelper.DescriptiveStemFromUrl(url));
    }

    [Fact]
    public void DeriveName_PrefersDescriptiveStemOverBinFallback()
    {
        string name = WDM.Services.DownloadEngine.DeriveName(
            "https://file-na-lax-1.gofile.io/download/web/982bae6b-ca2c-4e6e-85f4-7fd43b368d71/Spider-Man%20-%20Brand%20New%20Day%20(2026",
            "application/octet-stream");
        Assert.Contains("Spider-Man", name);
        Assert.DoesNotContain(".bin", name);
        Assert.False(name.StartsWith("download_", StringComparison.OrdinalIgnoreCase), name);
    }
}
