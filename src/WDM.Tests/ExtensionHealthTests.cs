// ExtensionHealthTests — deployed-copy freshness without a browser:
// version/key/update_url/file-set comparison that drives the reload notice.
using WDM.Services;
using WDM.Tests.TestInfrastructure;

namespace WDM.Tests;

public sealed class ExtensionHealthTests
{
    private static string Manifest(string version, string? key = "k", string? updateUrl = null)
    {
        string keyJson = key is null ? "" : $",\n  \"key\": \"{key}\"";
        string updJson = updateUrl is null ? "" : $",\n  \"update_url\": \"{updateUrl}\"";
        return $"{{\n  \"manifest_version\": 3,\n  \"name\": \"t\",\n  \"version\": \"{version}\"{keyJson}{updJson}\n}}";
    }

    private static (string src, string dst) Layout(string srcVer, string dstVer, bool dstUpdateUrl = false, bool dropDstFile = false)
    {
        string root = TestFiles.NewTempDir("wdm-exthealth");
        string src = Path.Combine(root, "src");
        string dst = Path.Combine(root, "dst");
        Directory.CreateDirectory(src);
        Directory.CreateDirectory(dst);
        File.WriteAllText(Path.Combine(src, "manifest.json"), Manifest(srcVer));
        File.WriteAllText(Path.Combine(src, "background.js"), "// src");
        File.WriteAllText(Path.Combine(dst, "manifest.json"),
            Manifest(dstVer, updateUrl: dstUpdateUrl ? "https://example.com/u.xml" : null));
        if (!dropDstFile)
            File.WriteAllText(Path.Combine(dst, "background.js"), "// dst");
        return (src, dst);
    }

    [Trait("Category", Cats.Unit)][Fact]
    public void MatchingDeploy_IsCurrent()
    {
        var (src, dst) = Layout("1.2.8", "1.2.8");
        try { Assert.True(BrowserIntegration.IsDeployedCurrent(src, dst)); }
        finally { TestFiles.DeleteDir(Path.GetDirectoryName(src)!); }
    }

    [Trait("Category", Cats.Unit)][Fact]
    public void VersionBump_IsStale()
    {
        var (src, dst) = Layout("1.2.9", "1.2.8");
        try { Assert.False(BrowserIntegration.IsDeployedCurrent(src, dst)); }
        finally { TestFiles.DeleteDir(Path.GetDirectoryName(src)!); }
    }

    [Trait("Category", Cats.Unit)][Fact]
    public void UpdateUrlInDeploy_IsStale()
    {
        var (src, dst) = Layout("1.2.8", "1.2.8", dstUpdateUrl: true);
        try { Assert.False(BrowserIntegration.IsDeployedCurrent(src, dst)); }
        finally { TestFiles.DeleteDir(Path.GetDirectoryName(src)!); }
    }

    [Trait("Category", Cats.Unit)][Fact]
    public void MissingFile_IsStale()
    {
        var (src, dst) = Layout("1.2.8", "1.2.8", dropDstFile: true);
        try { Assert.False(BrowserIntegration.IsDeployedCurrent(src, dst)); }
        finally { TestFiles.DeleteDir(Path.GetDirectoryName(src)!); }
    }
}
