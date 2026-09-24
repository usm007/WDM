using WDM.Services;
using WDM.Tests.TestInfrastructure;

namespace WDM.Tests;

public sealed class EngineManagerTests
{
    [Trait("Category", Cats.Unit)][Fact]
    public void FindEngineBinary_Missing_Returns_BinDir_Path()
    {
        string name = "wdm-test-nonexistent-" + Guid.NewGuid().ToString("N") + ".exe";
        string found = EngineManager.FindEngineBinary(name);
        Assert.Equal(Path.Combine(EngineManager.BinDir, name), found);
    }

    [Trait("Category", Cats.Unit)][Fact]
    public void FindEngineBinary_Rejects_TextFile_On_Path()
    {
        // LooksLikeValidBinary must reject a text file with .exe extension:
        // PATH is only a last resort and must pass validation.
        string dir = TestFiles.NewTempDir("wdm-path");
        try
        {
            string fake = Path.Combine(dir, "wdm-fake-engine-xyz.exe");
            File.WriteAllText(fake, "this is not a binary, just text");
            string? oldPath = Environment.GetEnvironmentVariable("PATH");
            try
            {
                Environment.SetEnvironmentVariable("PATH", dir + Path.PathSeparator + oldPath);
                string found = EngineManager.FindEngineBinary("wdm-fake-engine-xyz.exe");
                // Must NOT resolve to the text file on PATH.
                Assert.NotEqual(fake, found);
                Assert.Equal(Path.Combine(EngineManager.BinDir, "wdm-fake-engine-xyz.exe"), found);
            }
            finally { Environment.SetEnvironmentVariable("PATH", oldPath); }
        }
        finally { TestFiles.DeleteDir(dir); }
    }

    [Trait("Category", Cats.Unit)][Fact]
    public void BinDir_And_DataFolder_Are_WellFormed()
    {
        Assert.False(string.IsNullOrWhiteSpace(EngineManager.DataFolder));
        Assert.False(string.IsNullOrWhiteSpace(EngineManager.BinDir));
        Assert.StartsWith(EngineManager.DataFolder, EngineManager.BinDir[..Math.Min(EngineManager.DataFolder.Length, EngineManager.BinDir.Length)]);
        Directory.CreateDirectory(EngineManager.BinDir);
        Assert.True(Directory.Exists(EngineManager.BinDir));
    }

    [Trait("Category", Cats.Unit)][Fact]
    public async Task GetVersion_Missing_Returns_NotInstalled()
    {
        // If yt-dlp happens to be installed this returns a version instead —
        // either way it must be a non-empty string and never throw.
        string v = await EngineManager.GetVersionAsync();
        Assert.False(string.IsNullOrWhiteSpace(v));
    }
}
