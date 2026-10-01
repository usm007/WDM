using System;
using System.IO;

namespace WDM.Services;

/// <summary>Local disk failures that no retry or mirror rotation will ever fix.</summary>
public static class FatalErrors
{
    /// <summary>Full disk, ACL denial, over-long path (BUG-025).</summary>
    public static bool IsFatalDiskError(Exception ex)
    {
        if (ex is UnauthorizedAccessException or PathTooLongException)
            return true;
        return IsDiskFullError(ex);
    }

    /// <summary>Disk-full only (subset of fatal): pausable — freeing space and
    /// resuming is meaningful, unlike ACL/path errors.</summary>
    public static bool IsDiskFullError(Exception ex)
    {
        if (ex is IOException io)
        {
            // 0x80070070 ERROR_DISK_FULL, 0x80070027 drive full (FAT), 0x80070070 variants.
            int code = io.HResult & 0xFFFF;
            if (code is 0x70 or 0x27)
                return true;
        }
        return false;
    }

    /// <summary>Free bytes available on the volume holding <paramref name="path"/>,
    /// or null when unknowable (never fail a download on telemetry failure).</summary>
    public static long? GetFreeBytesForPath(string? path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path))
                return null;
            string? root = Path.GetPathRoot(Path.GetFullPath(path));
            if (string.IsNullOrWhiteSpace(root))
                return null;
            return new DriveInfo(root).AvailableFreeSpace;
        }
        catch { return null; }
    }
}
