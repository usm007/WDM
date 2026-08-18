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

    /// <summary>Alternative URLs for the same file. Used as failover mirrors; the
    /// engine rotates to the next mirror when the current URL keeps failing.</summary>
    public List<string> Mirrors { get; set; } = new();

    /// <summary>Server identity of the file (ETag / Last-Modified) captured at probe
    /// time. Compared on resume to detect that the file changed on the server.</summary>
    public string? Etag { get; set; }
    public string? LastModified { get; set; }

    /// <summary>Set when the user refreshed the download link. On the next start the
    /// engine skips the ETag identity check (a new URL may serve the same file with
    /// different headers) and resumes from the existing progress; it only restarts
    /// from zero if the new file has a different size. Not persisted.</summary>
    public bool LinkRefreshed { get; set; }

    private int _chunkCount = 0;
    public int ChunkCount
    {
        get => _chunkCount;
        set => Set(ref _chunkCount, Math.Max(0, value));
    }

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
        set
        {
            if (Set(ref _fileName, value))
                Raise(nameof(DisplayFileName));
        }
    }

    public string DisplayFileName
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(FileName))
                return FileName;
            if (Uri.TryCreate(Url, UriKind.Absolute, out var uri) && !string.IsNullOrWhiteSpace(uri.LocalPath))
            {
                string name = Path.GetFileName(uri.LocalPath);
                if (!string.IsNullOrWhiteSpace(name))
                    return name;
            }
            return Url;
        }
    }

    public string FullPath => (string.IsNullOrWhiteSpace(SaveFolder) || string.IsNullOrWhiteSpace(FileName))
        ? "" : Path.Combine(SaveFolder, FileName);

    private TaskStatus _status = TaskStatus.Queued;
    public TaskStatus Status
    {
        get => _status;
        set
        {
            if (Set(ref _status, value))
            {
                Raise(nameof(StatusText));
                Raise(nameof(ProgressText));
                Raise(nameof(SpeedText));
                Raise(nameof(ProgressSpeedText));
                Raise(nameof(Eta));
                Raise(nameof(DownloadedOfTotalText));
            }
        }
    }

    private long _totalBytes = -1;
    public long TotalBytes
    {
        get => _totalBytes;
        set
        {
            if (Set(ref _totalBytes, value))
            {
                Raise(nameof(SizeText));
                Raise(nameof(DownloadedOfTotalText));
            }
        }
    }

    private long _downloadedBytes;
    public long DownloadedBytes
    {
        get => _downloadedBytes;
        set
        {
            if (Set(ref _downloadedBytes, value))
            {
                Raise(nameof(DownloadedText));
                Raise(nameof(ProgressSpeedText));
                Raise(nameof(DownloadedOfTotalText));
            }
        }
    }

    private int _progress;
    public int Progress
    {
        get => Status == TaskStatus.Completed ? 100 : _progress;
        set
        {
            if (Set(ref _progress, value))
                Raise(nameof(ProgressSpeedText));
        }
    }

    private double _speedBps;
    public double SpeedBps
    {
        get => _speedBps;
        set
        {
            if (Set(ref _speedBps, value))
            {
                Raise(nameof(SpeedText));
                Raise(nameof(ProgressSpeedText));
            }
        }
    }

    private string _eta = "";
    public string Eta
    {
        get => Status == TaskStatus.Downloading ? _eta : "";
        set
        {
            Set(ref _eta, value);
        }
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

    public string ProgressText => Status == TaskStatus.Downloading ? $"{Progress}%" : "";

    public string SpeedText => Status == TaskStatus.Downloading && SpeedBps >= 1
        ? $"{FormatBytes((long)SpeedBps)}/s"
        : "";

    public string DomainText
    {
        get
        {
            if (Uri.TryCreate(Url, UriKind.Absolute, out var uri))
                return uri.Host;
            return "—";
        }
    }

    public string SpeedOrDetailText => Status switch
    {
        TaskStatus.Downloading => SpeedText,
        TaskStatus.Completed => CompletedAt.HasValue ? $"Completed {CompletedAt.Value:HH:mm}" : "",
        TaskStatus.Failed => !string.IsNullOrEmpty(Error) ? Error : "",
        _ => "",
    };

    public string ProgressSpeedText => Status == TaskStatus.Downloading
        ? $"{Progress}% · {SpeedText}".TrimEnd('·', ' ')
        : "";

    public string DownloadedOfTotalText => Status == TaskStatus.Downloading
        ? TotalBytes > 0
            ? $"{DownloadedText} of {SizeText}"
            : DownloadedText
        : "";

    public string QueueText => Status == TaskStatus.Queued ? (QueuePosition > 0 ? QueuePosition.ToString() : "Q") : "";

    private int _queuePosition;
    public int QueuePosition
    {
        get => _queuePosition;
        set
        {
            if (_queuePosition == value)
                return;
            _queuePosition = value;
            Raise(nameof(QueueText));
        }
    }

    public string CategoryColorHex => Category switch
    {
        DownloadCategory.Video => "#EF4444",
        DownloadCategory.Music => "#8B5CF6",
        DownloadCategory.Document => "#3B82F6",
        DownloadCategory.Compressed => "#F59E0B",
        DownloadCategory.Program => "#10B981",
        _ => "#000000",
    };

    public System.Windows.Media.Brush CategoryBrush =>
        (System.Windows.Media.Brush)System.Windows.Application.Current.Resources["Brush.TextDim"];

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
        TaskStatus.Downloading => "Running",
        TaskStatus.Failed => "Failed",
        TaskStatus.Completed => "Done",
        TaskStatus.Paused => "Paused",
        TaskStatus.Queued => "Queued",
        _ => Status.ToString(),
    };

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
