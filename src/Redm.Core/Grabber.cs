namespace Redm.Core;

public record GrabberProject(string StartUrl, string DestDir, int MaxDepth = 3, string FileFilter = "*", long MinSizeBytes = 0, string Schedule = "");
public record Schedule(int StartH = 0, int StartM = 0, int StopH = 23, int StopM = 59, bool ShutdownOnDone = false, long SpeedLimitBps = 0);
