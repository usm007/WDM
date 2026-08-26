using System.Text.Json;
using WDM.Models;

namespace WDM.Services;

public enum AppTheme
{
    Default,
    WdmOriginal
}

public sealed class AppSettings
{
    public string DownloadFolder { get; set; } = DownloadTask.DefaultSaveFolder;
    public int DefaultChunkCount { get; set; } = 0;
    public int MaxConcurrentDownloads { get; set; } = 3;
    public long GlobalSpeedLimitKbps { get; set; }
    public bool HasPromptedExtensionInstall { get; set; } = false;
    public bool MinimizeToTray { get; set; } = true;
    public bool NotifyOnCompletion { get; set; } = true;
    public bool ShowTrayProgress { get; set; } = false;
    public double? ProgressPanelLeft { get; set; }
    public double? ProgressPanelTop { get; set; }
    public bool RunAtStartup { get; set; }
    public bool StartInBackground { get; set; }
    public bool UseDarkTheme { get; set; }
    public AppTheme Theme { get; set; } = AppTheme.Default;
    public string? LastRunVersion { get; set; }

    // YouTube & Media downloads
    public bool EnableYouTubeDownloads { get; set; } = false;
    public string? YouTubeBrowserCookies { get; set; } = "none";

    // Updates
    public bool CheckForUpdates { get; set; } = true;
    public string? LastUpdateCheckUtc { get; set; }

    // Automatic retry
    public int MaxRetries { get; set; } = 3;

    // Category auto-routing
    public bool RouteByCategory { get; set; } = true;
    public Dictionary<string, string> CategoryFolders { get; set; } = new()
    {
        { "Video",      Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "Video") },
        { "Music",      Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "Music") },
        { "Document",   Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "Documents") },
        { "Compressed", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "Compressed") },
        { "Program",    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "Programs") },
        { "Other",      Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads") },
    };

    // Post-download
    public bool ComputeChecksum { get; set; }
    public string? PostDownloadScript { get; set; }
}

public sealed class TaskStore
{
    public static readonly string AppDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WDM");
    private static readonly string SettingsPath = Path.Combine(AppDir, "settings.json");
    private static readonly string TasksPath = Path.Combine(AppDir, "tasks.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
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
            AtomicFile.Write(SettingsPath, JsonSerializer.Serialize(settings, JsonOptions));
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
                Referer = t.Referer,
                Headers = t.Headers,
                Mirrors = t.Mirrors?.ToList() ?? new(),
                Etag = t.Etag,
                LastModified = t.LastModified,
                FileName = t.FileName,
                SaveFolder = t.SaveFolder,
                ChunkCount = t.ChunkCount,
                Status = t.Status,
                TotalBytes = t.TotalBytes,
                DownloadedBytes = t.DownloadedBytes,
                Progress = t.Progress,
                SpeedLimitKbps = t.SpeedLimitKbps,
                Priority = t.Priority,
                Category = t.Category,
                Checksum = t.Checksum,
                Error = t.Error,
                AddedAt = t.AddedAt,
                CompletedAt = t.CompletedAt,
            }).ToList();
            AtomicFile.Write(TasksPath, JsonSerializer.Serialize(records, JsonOptions));
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
    public Dictionary<string, string> Headers { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> Mirrors { get; set; } = new();
    public string? Etag { get; set; }
    public string? LastModified { get; set; }
    public string FileName { get; set; } = "";
    public string SaveFolder { get; set; } = "";
    public int ChunkCount { get; set; } = 0;
    public TaskStatus Status { get; set; }
    public long TotalBytes { get; set; } = -1;
    public long DownloadedBytes { get; set; }
    public int Progress { get; set; }
    public long SpeedLimitKbps { get; set; }
    public PriorityLevel Priority { get; set; } = PriorityLevel.Normal;
    public DownloadCategory Category { get; set; } = DownloadCategory.Other;
    public string? Checksum { get; set; }
    public string? Error { get; set; }
    public DateTime AddedAt { get; set; } = DateTime.Now;
    public DateTime? CompletedAt { get; set; }
}
