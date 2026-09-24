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
        if (ex is IOException io)
        {
            // 0x80070070 ERROR_DISK_FULL, 0x80070027 drive full (FAT), 0x80070070 variants.
            int code = io.HResult & 0xFFFF;
            if (code is 0x70 or 0x27)
                return true;
        }
        return false;
    }
}
