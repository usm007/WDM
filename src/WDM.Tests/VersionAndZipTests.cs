using System.IO.Compression;
using System.Reflection;
using WDM.Services;

namespace WDM.Tests;

public sealed class VersionAndZipTests
{
    [Fact]
    public void ToSystemVersion_MapsSemVer()
    {
        var sem = NuGet.Versioning.SemanticVersion.Parse("2.7.2");
        var v = VelopackUpdateService.ToSystemVersion(sem);
        Assert.Equal(new Version(2, 7, 2), v);
    }

    [Fact]
    public void ExtractZipSlip_IsRejected()
    {
        var m = typeof(EngineManager).GetMethod("ExtractZipSafely",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        string tmp = Path.Combine(Path.GetTempPath(), "wdm-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            string zip = Path.Combine(tmp, "evil.zip");
            using (var fs = new FileStream(zip, FileMode.CreateNew))
            using (var a = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                var e = a.CreateEntry("../../evil.txt");
                using var w = new StreamWriter(e.Open());
                w.Write("evil");
            }
            string out1 = Path.Combine(tmp, "out");
            Directory.CreateDirectory(out1);
            Assert.Throws<TargetInvocationException>(() => m.Invoke(null, new object[] { zip, out1 }));
        }
        finally
        {
            try { Directory.Delete(tmp, true); } catch { }
        }
    }
}
