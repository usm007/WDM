using WDM.Services.Chunking;
using WDM.Tests.TestInfrastructure;

namespace WDM.Tests;

public sealed class CompletionMapIntegrityTests
{
    private static string TempState(string dir) => Path.Combine(dir, "f.wdmstate");

    [Trait("Category", Cats.Unit)][Fact(DisplayName = "CM-001 reload recovers ranges")]
    public void CM001_Reload_Recovers()
    {
        string dir = TestFiles.NewTempDir("wdm-cm");
        try
        {
            long total = 4L * 1024 * 1024;
            var map = new CompletionMap(total, 1024 * 1024);
            map.MarkCommitted(0, 2L * 1024 * 1024);
            map.Save(TempState(dir));
            var re = CompletionMap.LoadOrMigrate(TempState(dir), total, 1024 * 1024, null, null);
            Assert.Equal(2L * 1024 * 1024, re.CompletedBytes);
            Assert.False(re.IsComplete);
        }
        finally { TestFiles.DeleteDir(dir); }
    }

    [Trait("Category", Cats.Unit)][Fact(DisplayName = "CM-002 corrupt falls back to 0")]
    public void CM002_Corrupt_FallsBack()
    {
        string dir = TestFiles.NewTempDir("wdm-cm");
        try
        {
            long total = 2L * 1024 * 1024;
            File.WriteAllText(TempState(dir), "{corrupt!!!!");
            var re = CompletionMap.LoadOrMigrate(TempState(dir), total, 1024 * 1024, null, null);
            Assert.Equal(0, re.CompletedBytes);
            Assert.False(re.IsComplete);
        }
        finally { TestFiles.DeleteDir(dir); }
    }

    [Trait("Category", Cats.Unit)][Fact(DisplayName = "CM-003 resume at 60pct")]
    public void CM003_Resume_At_60()
    {
        string dir = TestFiles.NewTempDir("wdm-cm");
        try
        {
            long total = 10L * 1024 * 1024;
            var map = new CompletionMap(total, 1024 * 1024);
            map.MarkCommitted(0, 6L * 1024 * 1024);
            map.Save(TempState(dir));
            var re = CompletionMap.LoadOrMigrate(TempState(dir), total, 1024 * 1024, null, null);
            var pending = re.GetPendingRanges();
            long left = pending.Sum(p => p.EndExclusive - p.Start);
            Assert.Equal(4L * 1024 * 1024, left);
        }
        finally { TestFiles.DeleteDir(dir); }
    }

    [Trait("Category", Cats.Unit)][Fact(DisplayName = "CM-004 adjacent workers no overlap")]
    public void CM004_Adjacent_NoOverlap()
    {
        long total = 8L * 1024 * 1024;
        var map = new CompletionMap(total, 1024 * 1024);
        Parallel.For(0, 8, b => map.MarkCommitted(b * 1024L * 1024, (b + 1) * 1024L * 1024));
        Assert.True(map.IsComplete);
        Assert.Equal(total, map.CompletedBytes);
        Assert.Empty(map.GetPendingRanges());
    }

    [Trait("Category", Cats.Unit)][Fact(DisplayName = "CM-005 complete deletes state")]
    public void CM005_Complete_Deletes()
    {
        string dir = TestFiles.NewTempDir("wdm-cm");
        try
        {
            long total = 2L * 1024 * 1024;
            var map = new CompletionMap(total, 1024 * 1024);
            map.MarkCommitted(0, total);
            Assert.True(map.IsComplete);
            map.Save(TempState(dir));
            CompletionMap.Delete(TempState(dir));
            Assert.False(File.Exists(TempState(dir)));
        }
        finally { TestFiles.DeleteDir(dir); }
    }
}
