// EngineHashTests — supply-chain pinning without network: sums parsing,
// sha256 enforcement, sidecar trust-on-first-use + tamper detection.
using WDM.Services;
using WDM.Tests.TestInfrastructure;

namespace WDM.Tests;

public sealed class EngineHashTests
{
    [Trait("Category", Cats.Unit)][Fact]
    public void ExtractSumsHash_BothForms()
    {
        string text = "66674953fe251b89f4d08c5f0e35e0728679bd67ab3d7d05c0562af101dd3e7a  yt-dlp.exe\n" +
                      "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa *yt-dlp_linux\n";
        Assert.Equal("66674953fe251b89f4d08c5f0e35e0728679bd67ab3d7d05c0562af101dd3e7a",
            EngineManager.ExtractSumsHash(text, "yt-dlp.exe"));
        Assert.Equal("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            EngineManager.ExtractSumsHash(text, "yt-dlp_linux"));
        Assert.Null(EngineManager.ExtractSumsHash(text, "other.exe"));
        Assert.Null(EngineManager.ExtractSumsHash("not a sums file", "yt-dlp.exe"));
    }

    [Trait("Category", Cats.Unit)][Fact]
    public void VerifySha256_Match_Passes_Mismatch_Deletes_And_Throws()
    {
        string dir = TestFiles.NewTempDir("wdm-hash");
        try
        {
            string f = Path.Combine(dir, "e.bin");
            File.WriteAllBytes(f, new byte[] { 1, 2, 3, 4 });
            string good = EngineManager.ComputeSha256(f);
            EngineManager.VerifySha256OrWarn(f, good, "t");
            Assert.True(File.Exists(f));
            Assert.Throws<EngineMissingException>(() =>
                EngineManager.VerifySha256OrWarn(f, new string('0', 64), "t"));
            Assert.False(File.Exists(f));
        }
        finally { TestFiles.DeleteDir(dir); }
    }

    [Trait("Category", Cats.Unit)][Fact]
    public void Pin_RoundTrips_And_Detects_Tamper()
    {
        string dir = TestFiles.NewTempDir("wdm-hash");
        try
        {
            string f = Path.Combine(dir, "e.exe");
            File.WriteAllBytes(f, new byte[] { 9, 9, 9 });
            EngineManager.PinInstalledHash(f);
            Assert.True(EngineManager.VerifyInstalledHash(f));
            File.WriteAllBytes(f, new byte[] { 9, 9, 8 });
            Assert.False(EngineManager.VerifyInstalledHash(f));
        }
        finally { TestFiles.DeleteDir(dir); }
    }
}
