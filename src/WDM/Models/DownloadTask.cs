using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Threading;
using WDM.Services;

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

    /// <summary>Custom HTTP headers sent with every request (e.g. Cookie, Authorization).
    /// Keys are header names; values are header values.</summary>
    public Dictionary<string, string> Headers { get; set; } = new(StringComparer.OrdinalIgnoreCase);

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

    public bool IsYouTube { get; set; }
    public string? YouTubeFormatArg { get; set; }
    public string? YouTubeExtraArgs { get; set; }
    public string? YouTubeVideoId { get; set; }
    public string? ThumbnailUrl { get; set; }

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
                Raise(nameof(Progress));
                Raise(nameof(IsDownloading));
                Raise(nameof(SpeedText));
                Raise(nameof(ProgressSpeedText));
                Raise(nameof(Eta));
                Raise(nameof(DownloadedOfTotalText));
                Raise(nameof(DisplaySizeText));
                Raise(nameof(RowTelemetryStatusText));
                Raise(nameof(PrimaryStatusText));
                Raise(nameof(RowTimeOrEtaText));
                Raise(nameof(EtaOrDashText));
                Raise(nameof(HasFailureError));
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
                if (_totalBytes > 0 && DownloadedBytes > 0 && Status != TaskStatus.Completed)
                {
                    int pct = (int)Math.Clamp((double)DownloadedBytes * 100.0 / _totalBytes, 0, 100);
                    if (_progress != pct)
                    {
                        _progress = pct;
                        Raise(nameof(Progress));
                    }
                }
                Raise(nameof(SizeText));
                Raise(nameof(DownloadedOfTotalText));
                Raise(nameof(DisplaySizeText));
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
                if (TotalBytes > 0 && Status != TaskStatus.Completed)
                {
                    int pct = (int)Math.Clamp((double)_downloadedBytes * 100.0 / TotalBytes, 0, 100);
                    if (_progress != pct)
                    {
                        _progress = pct;
                        Raise(nameof(Progress));
                    }
                }
                Raise(nameof(DownloadedText));
                Raise(nameof(ProgressSpeedText));
                Raise(nameof(DownloadedOfTotalText));
                Raise(nameof(DisplaySizeText));
            }
        }
    }

    private int _progress;
    public int Progress
    {
        get
        {
            if (Status == TaskStatus.Completed)
                return 100;
            if (TotalBytes > 0 && DownloadedBytes > 0)
            {
                int calc = (int)Math.Clamp((double)DownloadedBytes * 100.0 / TotalBytes, 0, 100);
                return Math.Max(_progress, calc);
            }
            return _progress;
        }
        set
        {
            if (Set(ref _progress, value))
            {
                Raise(nameof(ProgressText));
                Raise(nameof(ProgressSpeedText));
            }
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

    /// <summary>Whether the download can be resumed/paused mid-transfer, as determined
    /// by the engine's probe (server Range support, known size, non-HLS).</summary>
    private bool _isResumable;
    public bool IsResumable
    {
        get => _isResumable;
        set => Set(ref _isResumable, value);
    }

    private string _resumeCapabilityText = "Checking server support…";
    public string ResumeCapabilityText
    {
        get => _resumeCapabilityText;
        set => Set(ref _resumeCapabilityText, value);
    }

    public string SizeText => TotalBytes > 0 ? FormatBytes(TotalBytes) : "—";

    public string DisplaySizeText
    {
        get
        {
            if (Status == TaskStatus.Downloading)
                return DownloadedBytes > 0 ? DownloadedText : (TotalBytes > 0 ? $"0 B" : "—");

            // Paused, Done/Completed, Queued, Failed
            if (TotalBytes > 0)
                return SizeText;
            if (DownloadedBytes > 0)
                return DownloadedText;
            return "—";
        }
    }

    public string DownloadedText => FormatBytes(DownloadedBytes);

    public string ProgressText => Status == TaskStatus.Downloading ? $"{Progress}%" : "";

    public bool IsDownloading => Status == TaskStatus.Downloading;

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

    public string PrimaryStatusText => Status switch
    {
        TaskStatus.Downloading => "Running",
        TaskStatus.Paused => "Paused",
        TaskStatus.Completed => "Done",
        TaskStatus.Failed => "Failed",
        TaskStatus.Queued => QueuePosition > 0 ? $"Queue #{QueuePosition}" : "Queued",
        _ => Status.ToString()
    };

    public string RowTimeOrEtaText => Status switch
    {
        TaskStatus.Downloading => !string.IsNullOrEmpty(Eta) ? Eta : "",
        TaskStatus.Completed => CompletedAt.HasValue ? CompletedAt.Value.ToString("HH:mm") : "",
        _ => ""
    };

    public string EtaOrDashText => Status switch
    {
        TaskStatus.Downloading => !string.IsNullOrEmpty(Eta) ? Eta : "--",
        _ => "--"
    };

    public bool HasFailureError => Status == TaskStatus.Failed && !string.IsNullOrWhiteSpace(Error);

    public string RowTelemetryStatusText => Status switch
    {
        TaskStatus.Downloading => !string.IsNullOrEmpty(Eta) ? Eta : "Estimating…",
        TaskStatus.Completed => CompletedAt.HasValue ? CompletedAt.Value.ToString("HH:mm") : "",
        TaskStatus.Failed => "",
        TaskStatus.Queued => QueuePosition > 0 ? $"Queue #{QueuePosition}" : "Queued",
        TaskStatus.Paused => "",
        _ => "",
    };

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

    public string DownloadedOfTotalText
    {
        get
        {
            if (Status == TaskStatus.Completed)
            {
                if (TotalBytes > 0)
                    return $"{SizeText} / {SizeText}";
                if (DownloadedBytes > 0)
                    return $"{DownloadedText} / {DownloadedText}";
                return SizeText;
            }
            if (TotalBytes > 0)
                return $"{DownloadedText} / {SizeText}";
            if (DownloadedBytes > 0)
                return DownloadedText;
            return "—";
        }
    }

    public long RemainingBytes => TotalBytes > DownloadedBytes ? TotalBytes - DownloadedBytes : 0;
    public string RemainingBytesText => TotalBytes > 0 && RemainingBytes > 0 ? FormatBytes(RemainingBytes) : (Status == TaskStatus.Completed ? "0 B" : "—");
    public string ConnectionsText => ChunkCount > 1 ? $"{ChunkCount} threads" : "1 thread";
    public string EtaDetailText => !string.IsNullOrEmpty(Eta) ? $"{Eta} remaining" : (Status == TaskStatus.Downloading ? "Calculating…" : "—");
    public string ExactBytesText => TotalBytes > 0 ? $"{TotalBytes:N0} B" : (DownloadedBytes > 0 ? $"{DownloadedBytes:N0} B" : "Unknown");

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

    public string CategoryColorHex
    {
        get
        {
            string key = Category switch
            {
                DownloadCategory.Video => "Brush.CatVideo",
                DownloadCategory.Music => "Brush.CatMusic",
                DownloadCategory.Document => "Brush.CatDocument",
                DownloadCategory.Compressed => "Brush.CatCompressed",
                DownloadCategory.Program => "Brush.CatProgram",
                _ => "Brush.TextMuted",
            };
            if (System.Windows.Application.Current?.Resources[key] is System.Windows.Media.SolidColorBrush brush)
                return brush.Color.ToString();
            return Category switch
            {
                DownloadCategory.Video => "#EF4444",
                DownloadCategory.Music => "#8B5CF6",
                DownloadCategory.Document => "#3B82F6",
                DownloadCategory.Compressed => "#F59E0B",
                DownloadCategory.Program => "#10B981",
                _ => "#90939E",
            };
        }
    }

    public System.Windows.Media.Brush CategoryBrush =>
        (System.Windows.Media.Brush)System.Windows.Application.Current.Resources[
            Category switch
            {
                DownloadCategory.Video => "Brush.CatVideo",
                DownloadCategory.Music => "Brush.CatMusic",
                DownloadCategory.Document => "Brush.CatDocument",
                DownloadCategory.Compressed => "Brush.CatCompressed",
                DownloadCategory.Program => "Brush.CatProgram",
                _ => "Brush.Text",
            }];

    public string TypeIcon => Category switch
    {
        DownloadCategory.Video => char.ConvertFromUtf32(0xF0381),
        DownloadCategory.Music => char.ConvertFromUtf32(0xF0387),
        DownloadCategory.Document => char.ConvertFromUtf32(0xF0219),
        DownloadCategory.Compressed => char.ConvertFromUtf32(0xF05C4),
        DownloadCategory.Program => char.ConvertFromUtf32(0xF08C6),
        _ => char.ConvertFromUtf32(0xF0224),
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
