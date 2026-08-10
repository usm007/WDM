using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Threading;

namespace WDM.Models;

public enum TaskStatus
{
    Queued,
    Downloading,
    Paused,
    Completed,
    Failed,
    Scheduled,
}

public enum PriorityLevel
{
    Low = 0,
    Normal = 1,
    High = 2,
}

public enum DownloadCategory
{
    Other = 0,
    Video = 1,
    Music = 2,
    Document = 3,
    Compressed = 4,
    Program = 5,
}

public sealed class DownloadTask : INotifyPropertyChanged
{
    private readonly Dispatcher _ui;

    public DownloadTask(Dispatcher ui)
    {
        _ui = ui;
    }

    public Guid Id { get; } = Guid.NewGuid();
    public DateTime AddedAt { get; init; } = DateTime.Now;

    public string Url { get; set; } = "";
    public string? Referer { get; set; }
    public int ChunkCount { get; set; } = 4;
    public long SpeedLimitKbps { get; set; }
    public DownloadCategory Category { get; set; } = DownloadCategory.Other;
    public string? Checksum { get; set; }
    public DateTime? CompletedAt { get; set; }

    private PriorityLevel _priority = PriorityLevel.Normal;
    public PriorityLevel Priority
    {
        get => _priority;
        set => Set(ref _priority, value);
    }

    private DateTime? _scheduledStart;
    public DateTime? ScheduledStart
    {
        get => _scheduledStart;
        set
        {
            if (Set(ref _scheduledStart, value))
                Raise(nameof(ScheduledStartText));
        }
    }

    private string _saveFolder = DefaultSaveFolder;
    public string SaveFolder
    {
        get => _saveFolder;
        set => Set(ref _saveFolder, value);
    }

    public static string DefaultSaveFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");

    private string _fileName = "";
    public string FileName
    {
        get => _fileName;
        set => Set(ref _fileName, value);
    }

    public string FullPath => string.IsNullOrWhiteSpace(SaveFolder) ? "" : Path.Combine(SaveFolder, FileName);

    private TaskStatus _status = TaskStatus.Queued;
    public TaskStatus Status
    {
        get => _status;
        set
        {
            if (Set(ref _status, value))
                Raise(nameof(StatusText));
        }
    }

    private long _totalBytes = -1;
    public long TotalBytes
    {
        get => _totalBytes;
        set
        {
            if (Set(ref _totalBytes, value))
                Raise(nameof(SizeText));
        }
    }

    private long _downloadedBytes;
    public long DownloadedBytes
    {
        get => _downloadedBytes;
        set
        {
            if (Set(ref _downloadedBytes, value))
                Raise(nameof(SizeText));
        }
    }

    private int _progress;
    public int Progress
    {
        get => _progress;
        set => Set(ref _progress, value);
    }

    private double _speedBps;
    public double SpeedBps
    {
        get => _speedBps;
        set
        {
            if (Set(ref _speedBps, value))
                Raise(nameof(SpeedText));
        }
    }

    private string _eta = "";
    public string Eta
    {
        get => _eta;
        set => Set(ref _eta, value);
    }

    private string? _error;
    public string? Error
    {
        get => _error;
        set
        {
            if (Set(ref _error, value))
                Raise(nameof(StatusText));
        }
    }

    public string SizeText => TotalBytes > 0 ? FormatBytes(TotalBytes) : "—";

    public string DownloadedText => FormatBytes(DownloadedBytes);

    public string ProgressText => $"{Progress}%";

    public string SpeedText => SpeedBps >= 1 ? $"{FormatBytes((long)SpeedBps)}/s" : "";

    public string QueueText => Status == TaskStatus.Queued || Status == TaskStatus.Downloading ? "Q" : "";

    public string CategoryColorHex => Category switch
    {
        DownloadCategory.Video => "#EF4444",
        DownloadCategory.Music => "#8B5CF6",
        DownloadCategory.Document => "#3B82F6",
        DownloadCategory.Compressed => "#F59E0B",
        DownloadCategory.Program => "#10B981",
        _ => "#6B7280",
    };

    public string TypeIcon => Category switch
    {
        DownloadCategory.Video => "\uE714",
        DownloadCategory.Music => "\uE8D6",
        DownloadCategory.Document => "\uE8A5",
        DownloadCategory.Compressed => "\uF133",
        DownloadCategory.Program => "\uE756",
        _ => "\uE7C3",
    };

    public string StatusText => Status switch
    {
        TaskStatus.Failed => Error ?? "Failed",
        TaskStatus.Scheduled => ScheduledStart is DateTime s ? $"Scheduled · {s:HH:mm}" : "Scheduled",
        _ => Status.ToString(),
    };

    public string ScheduledStartText => ScheduledStart is DateTime s ? s.ToString("yyyy-MM-dd HH:mm") : "";

    public static string FormatBytes(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return unit == 0 ? $"{value:0} {units[unit]}" : $"{value:0.0} {units[unit]}";
    }

    public static DownloadCategory Categorize(string fileName)
    {
        string ext = Path.GetExtension(fileName ?? "").TrimStart('.').ToLowerInvariant();
        if (VideoExtensions.Contains(ext)) return DownloadCategory.Video;
        if (MusicExtensions.Contains(ext)) return DownloadCategory.Music;
        if (DocumentExtensions.Contains(ext)) return DownloadCategory.Document;
        if (ArchiveExtensions.Contains(ext)) return DownloadCategory.Compressed;
        if (ProgramExtensions.Contains(ext)) return DownloadCategory.Program;
        return DownloadCategory.Other;
    }

    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "mp4", "mkv", "avi", "mov", "wmv", "flv", "webm", "m4v", "mpg", "mpeg", "3gp", "ts", "mts", "m2ts",
    };
    private static readonly HashSet<string> MusicExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "mp3", "wav", "flac", "aac", "ogg", "wma", "m4a", "opus", "mid", "midi", "ape", "aiff",
    };
    private static readonly HashSet<string> DocumentExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "pdf", "doc", "docx", "xls", "xlsx", "ppt", "pptx", "txt", "md", "rtf", "odt", "csv", "epub", "mobi",
    };
    private static readonly HashSet<string> ArchiveExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "zip", "rar", "7z", "tar", "gz", "bz2", "xz", "iso", "cab", "dmg", "tgz", "tbz2",
    };
    private static readonly HashSet<string> ProgramExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "exe", "msi", "apk", "deb", "rpm", "dmg", "appimage", "run", "sh", "jar", "pkg",
    };

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;
        field = value;
        Raise(name);
        return true;
    }

    private void Raise(string? name)
    {
        var handler = PropertyChanged;
        if (handler is null)
            return;
        if (_ui.CheckAccess())
            handler(this, new PropertyChangedEventArgs(name));
        else
            _ui.BeginInvoke(() => handler(this, new PropertyChangedEventArgs(name)));
    }
}
