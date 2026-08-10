using System.Text.Json;
using WDM.Models;

namespace WDM.Services;

public sealed class AppSettings
{
    public string DownloadFolder { get; set; } = DownloadTask.DefaultSaveFolder;
    public int DefaultChunkCount { get; set; } = 4;
    public int MaxConcurrentDownloads { get; set; } = 3;
    public long GlobalSpeedLimitKbps { get; set; }
    public bool MonitorClipboard { get; set; } = true;
    public bool MinimizeToTray { get; set; } = true;
    public bool NotifyOnCompletion { get; set; } = true;
    public bool RunAtStartup { get; set; }
    public bool StartInBackground { get; set; }

    // Automatic retry
    public int MaxRetries { get; set; } = 3;

    // Time-of-day throttle ("throttle during work hours")
    public bool ThrottleScheduleEnabled { get; set; }
    public string ThrottleStart { get; set; } = "09:00";
    public string ThrottleEnd { get; set; } = "17:00";
    public long ThrottleLimitKbps { get; set; }

    // Download window ("only download 1am–6am")
    public bool DownloadWindowEnabled { get; set; }
    public string WindowStart { get; set; } = "01:00";
    public string WindowEnd { get; set; } = "06:00";

    // Category auto-routing
    public bool RouteByCategory { get; set; }
    public Dictionary<string, string> CategoryFolders { get; set; } = new();

    // Post-download
    public bool ComputeChecksum { get; set; }
    public string? PostDownloadScript { get; set; }
}

public sealed class TaskStore
{
    private static readonly string AppDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WDM");
    private static readonly string SettingsPath = Path.Combine(AppDir, "settings.json");
    private static readonly string TasksPath = Path.Combine(AppDir, "tasks.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    public static AppSettings LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsPath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath), JsonOptions) ?? new AppSettings();
        }
        catch
        {
            // Fall back to defaults.
        }
        return new AppSettings();
    }

    public static void SaveSettings(AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(AppDir);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(settings, JsonOptions));
        }
        catch
        {
            // Ignore persistence failures.
        }
    }

    public static List<TaskRecord> LoadTasks()
    {
        try
        {
            if (File.Exists(TasksPath))
                return JsonSerializer.Deserialize<List<TaskRecord>>(File.ReadAllText(TasksPath), JsonOptions) ?? new();
        }
        catch
        {
            // Fall back to empty list.
        }
        return new List<TaskRecord>();
    }

    public static void SaveTasks(IEnumerable<DownloadTask> tasks)
    {
        try
        {
            Directory.CreateDirectory(AppDir);
            var records = tasks.Select(t => new TaskRecord
            {
                Url = t.Url,
                FileName = t.FileName,
                SaveFolder = t.SaveFolder,
                ChunkCount = t.ChunkCount,
                Status = t.Status,
                TotalBytes = t.TotalBytes,
                SpeedLimitKbps = t.SpeedLimitKbps,
                Referer = t.Referer,
                Priority = t.Priority,
                Category = t.Category,
                ScheduledStart = t.ScheduledStart,
                Checksum = t.Checksum,
                CompletedAt = t.CompletedAt,
            }).ToList();
            File.WriteAllText(TasksPath, JsonSerializer.Serialize(records, JsonOptions));
        }
        catch
        {
            // Ignore persistence failures.
        }
    }
}

public sealed class TaskRecord
{
    public string Url { get; set; } = "";
    public string? Referer { get; set; }
    public string FileName { get; set; } = "";
    public string SaveFolder { get; set; } = "";
    public int ChunkCount { get; set; } = 4;
    public TaskStatus Status { get; set; }
    public long TotalBytes { get; set; } = -1;
    public long SpeedLimitKbps { get; set; }
    public PriorityLevel Priority { get; set; } = PriorityLevel.Normal;
    public DownloadCategory Category { get; set; } = DownloadCategory.Other;
    public DateTime? ScheduledStart { get; set; }
    public string? Checksum { get; set; }
    public DateTime? CompletedAt { get; set; }
}
