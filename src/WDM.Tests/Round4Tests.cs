using System.IO;
using System.IO.Compression;
using System.Text;
using WDM.Models;
using WDM.Services;
using WDM.Services.Embed;

namespace WDM.Tests;

public sealed class Round4Tests
{
    [Fact]
    public void ExtractZipSafely_ExtractsValidArchiveUnder1GB()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "wdm_zip_test_" + Guid.NewGuid().ToString("N"));
        string zipPath = Path.Combine(Path.GetTempPath(), "wdm_test_" + Guid.NewGuid().ToString("N") + ".zip");
        try
        {
            Directory.CreateDirectory(tempDir);
            using (var fs = new FileStream(zipPath, FileMode.Create))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                var entry1 = zip.CreateEntry("bin/test1.txt");
                using (var s = entry1.Open())
                {
                    byte[] data = Encoding.UTF8.GetBytes("hello engine test");
                    s.Write(data, 0, data.Length);
                }
                var entry2 = zip.CreateEntry("bin/test2.txt");
                using (var s = entry2.Open())
                {
                    byte[] data = Encoding.UTF8.GetBytes("second entry content");
                    s.Write(data, 0, data.Length);
                }
            }

            EngineManager.ExtractZipSafely(zipPath, tempDir);

            Assert.True(File.Exists(Path.Combine(tempDir, "bin", "test1.txt")));
            Assert.True(File.Exists(Path.Combine(tempDir, "bin", "test2.txt")));
            Assert.Equal("hello engine test", File.ReadAllText(Path.Combine(tempDir, "bin", "test1.txt")));
        }
        finally
        {
            try { File.Delete(zipPath); } catch { }
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void ExtractZipSafely_RejectsPathTraversal()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "wdm_zip_traversal_" + Guid.NewGuid().ToString("N"));
        string zipPath = Path.Combine(Path.GetTempPath(), "wdm_traversal_" + Guid.NewGuid().ToString("N") + ".zip");
        try
        {
            Directory.CreateDirectory(tempDir);
            using (var fs = new FileStream(zipPath, FileMode.Create))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                var entry = zip.CreateEntry("../../evil.exe");
                using var s = entry.Open();
                s.WriteByte(0x41);
            }

            var ex = Assert.Throws<InvalidOperationException>(() =>
                EngineManager.ExtractZipSafely(zipPath, tempDir));
            Assert.Contains("unsafe path", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            try { File.Delete(zipPath); } catch { }
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void MergeCookies_CombinesIncomingAndJarCookies()
    {
        string incoming = "cf_clearance=12345; user_id=42";
        var jar = new Dictionary<string, string>
        {
            ["session_token"] = "abcde",
            ["user_id"] = "99" // server updated cookie
        };

        string merged = EmbedResolver.MergeCookies(incoming, jar);

        Assert.Contains("cf_clearance=12345", merged);
        Assert.Contains("session_token=abcde", merged);
        Assert.Contains("user_id=99", merged);
        Assert.DoesNotContain("user_id=42", merged);
    }

    [Fact]
    public void MergeCookies_HandlesEmptyIncomingOrJar()
    {
        var jar = new Dictionary<string, string> { ["token"] = "xyz" };
        Assert.Equal("token=xyz", EmbedResolver.MergeCookies("", jar));
        Assert.Equal("token=xyz", EmbedResolver.MergeCookies(null, jar));

        var emptyJar = new Dictionary<string, string>();
        Assert.Equal("a=b; c=d", EmbedResolver.MergeCookies("a=b; c=d", emptyJar));
    }

    [Fact]
    public void RefreshLinkDialog_GetTargetPageUrl_PrioritizesSourcePageUrl()
    {
        var task = new DownloadTask
        {
            SourcePageUrl = "https://example.com/watch/video-123",
            Referer = "https://example.com/other",
            Url = "https://cdn.example.com/stream/expired-signed-link.mp4"
        };

        string target = RefreshLinkDialog.GetTargetPageUrl(task);
        Assert.Equal("https://example.com/watch/video-123", target);
    }

    [Fact]
    public void RefreshLinkDialog_GetTargetPageUrl_FallsBackToRefererAndUrl()
    {
        var taskWithReferer = new DownloadTask
        {
            SourcePageUrl = "",
            Referer = "https://example.com/gallery",
            Url = "https://cdn.example.com/image.jpg"
        };
        Assert.Equal("https://example.com/gallery", RefreshLinkDialog.GetTargetPageUrl(taskWithReferer));

        var taskWithOnlyUrl = new DownloadTask
        {
            SourcePageUrl = null,
            Referer = null,
            Url = "https://cdn.example.com/file.zip"
        };
        Assert.Equal("https://cdn.example.com/file.zip", RefreshLinkDialog.GetTargetPageUrl(taskWithOnlyUrl));
    }

    [Theory]
    [InlineData("https://site.com/video", "site.com", true)]
    [InlineData("https://site.com/video", "cdn.site.com", true)]
    [InlineData("https://video.site.com/video", "site.com", true)]
    [InlineData(null, "cdn.site.com", true)]
    [InlineData("", "cdn.site.com", true)]
    [InlineData("https://site.com/video", "othersite.com", false)]
    [InlineData("https://site.com/video", "site.com.evil.com", false)]
    public void HlsDownloader_IsSameHostOrSubdomain_BehavesCorrectly(string? referer, string targetHost, bool expected)
    {
        bool result = HlsDownloader.IsSameHostOrSubdomain(referer, targetHost);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void CaptureAuth_HeaderName_IsXWDMToken()
    {
        Assert.Equal("X-WDM-Token", CaptureAuth.HeaderName);
    }

    [Fact]
    public void TaskStatus_PreparingStateCanBeCleared()
    {
        var task = new DownloadTask
        {
            IsPreparing = true,
            PhaseText = "Analyzing stream..."
        };
        Assert.True(task.IsPreparing);

        task.IsPreparing = false;
        task.PhaseText = "";

        Assert.False(task.IsPreparing);
        Assert.Equal("", task.PhaseText);
    }
}
