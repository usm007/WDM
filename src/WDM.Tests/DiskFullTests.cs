// DiskFullTests — disk-full pauses (never fails): helper classification,
// user text, resume-refused reasons.
using WDM.Services;
using WDM.Tests.TestInfrastructure;

namespace WDM.Tests;

public sealed class DiskFullTests
{
    private static IOException DiskFull()
    {
        var ex = new IOException("There is not enough space on the disk.");
        ex.HResult = unchecked((int)0x80070070);
        return ex;
    }

    [Trait("Category", Cats.Unit)][Fact]
    public void IsDiskFullError_Classifies_Only_NoSpace()
    {
        Assert.True(FatalErrors.IsDiskFullError(DiskFull()));
        Assert.True(FatalErrors.IsFatalDiskError(DiskFull()));
        Assert.False(FatalErrors.IsDiskFullError(new UnauthorizedAccessException()));
        Assert.False(FatalErrors.IsDiskFullError(new IOException("transient")));
    }

    [Trait("Category", Cats.Unit)][Fact]
    public void GetFreeBytesForPath_Returns_System_Drive_Value()
    {
        long? free = FatalErrors.GetFreeBytesForPath(Path.GetTempPath());
        Assert.NotNull(free);
        Assert.True(free > 0);
        Assert.Null(FatalErrors.GetFreeBytesForPath(null));
        Assert.Null(FatalErrors.GetFreeBytesForPath(""));
    }

    [Trait("Category", Cats.Unit)][Fact]
    public void DiskFull_Maps_To_Paused_Text()
    {
        string text = UserFriendlyError.ForDownload(new DownloadEngine.DiskFullPausedException("x"));
        Assert.StartsWith("Paused:", text);
    }

    [Trait("Category", Cats.Unit)][Fact]
    public void ResumeRefused_Reasons_Name_Cause_And_Action()
    {
        Assert.Contains("session expired", UserFriendlyError.ForDownload(
            new DownloadEngine.FileChangedException("x", "session-expired")), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("resume", UserFriendlyError.ForDownload(
            new DownloadEngine.FileChangedException("x", "range-unsupported")), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ETag", UserFriendlyError.ForDownload(
            new DownloadEngine.FileChangedException("x", "etag-changed")));
    }
}
