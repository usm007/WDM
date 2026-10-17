namespace Redm.Core;

public record License(string Kind = "trial", long FirstUseUnix = 0, string Key = "")
{
    public int TrialDaysLeft(long nowUnix)
    {
        long used = (nowUnix - FirstUseUnix) / 86400;
        return (int)System.Math.Max(0, 30 - used);
    }
}
