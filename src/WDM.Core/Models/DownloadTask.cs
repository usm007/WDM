using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
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
    private readonly SynchronizationContext _sync;

    public DownloadTask(SynchronizationContext? sync = null)
    {
        _sync = sync ?? SynchronizationContext.Current ?? new SynchronizationContext();
    }

    public Guid Id { get; internal set; } = Guid.NewGuid();
    public DateTime AddedAt { get; init; } = DateTime.Now;

    public string Url { get; set; } = "";
    public string? Referer { get; set; }

    /// <summary>Original player/embed page URL when <see cref="Url"/> was resolved to a
    /// direct stream by the embed resolver. Persisted so expiring signed links can be
    /// re-resolved fresh on resume/restart instead of reusing a stale CDN URL.</summary>
    public string? SourcePageUrl { get; set; }

    /// <summary>Custom HTTP headers sent with every request (e.g. Cookie, Authorization).
    /// Keys are header names; values are header values.</summary>
    public Dictionary<string, string> Headers { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Per-download full-session replay (user-approved): Cookie and
    /// Authorization ride even cross-host (mirrors/CDNs), unlike the default
    /// same-host-only gating. Persisted so resumes keep working.</summary>
    public bool FullSessionReplay { get; set; }

    /// <summary>Form POST body replay (IDM Bc equivalent): base64 request body
    /// captured from the page's own form POST, plus its content type. Replayed
    /// on probe + single-stream download; such tasks never use ranged/chunked
    /// requests. Persisted like Headers.</summary>
    public string? PostData { get; set; }
    public string? PostContentType { get; set; }

    /// <summary>Browser proxy mirror: effective proxy the browser used when the
    /// link was captured (host/port/type, never credentials). Honored for probe
    /// + single-stream via a scoped client; forces single-stream (the shared
    /// chunk pool has no per-task proxy). Persisted.</summary>
    public string? ProxyHost { get; set; }
    public int ProxyPort { get; set; }
    public string? ProxyType { get; set; }

    public bool HasPostBody() => !string.IsNullOrWhiteSpace(PostData);
    public bool HasProxyOverride() => !string.IsNullOrWhiteSpace(ProxyHost) && ProxyPort >= 1 && ProxyPort <= 65535;

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

    /// <summary>Auto-resume attempts spent by the Failed auto-resume timer (1DM
    /// always_retry_download, bounded by settings MaxRetries). Transient by design:
    /// reset on manual retry/success/remove, never mapped into TaskRecord.</summary>
    public int AutoResumeAttempts { get; set; }

    /// <summary>Per-chunk completion snapshot mirrored from the engine's chunk
    /// bitmap at save time (1DM db/ThreadInfo equivalent, coarse). Restores
    /// resume progress when the .wdmstate sidecar is lost; the sidecar stays
    /// authoritative when present. Transient: mapped via TaskRecord only.</summary>
    public List<Services.SegmentRecord>? SegmentSnapshot { get; set; }

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
    private DownloadCategory _category = DownloadCategory.Other;
    public DownloadCategory Category
    {
        get => _category;
        set => Set(ref _category, value);
    }
    public string? Checksum { get; set; }
    public DateTime? CompletedAt { get; set; }

    /// <summary>Set when an HLS download was kept as .ts because conversion was
    /// wanted (container MP4/MKV) but ffmpeg is not installed. The UI notifies
    /// the user where to get it. Transient: never persisted.</summary>
    public bool RemuxSkippedNoFfmpeg { get; set; }

    private PriorityLevel _priority = PriorityLevel.Normal;
    public PriorityLevel Priority
    {
        get => _priority;
        set => Set(ref _priority, value);
    }

    private bool _waitForIdle;
    /// <summary>"Add to Queue": starts only when no other download is active,
    /// overriding MaxConcurrentDownloads. Cleared by an explicit Start/Retry;
    /// bulk resume and auto-starts respect it. Persisted.</summary>
    public bool WaitForIdle
    {
        get => _waitForIdle;
        set => Set(ref _waitForIdle, value);
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
                // Leaving the active state always clears the preparing indicator;
                // the engine re-enables it on every new session.
                if (_status != TaskStatus.Downloading && _isPreparing)
                {
                    _isPreparing = false;
                    Raise(nameof(IsPreparing));
                }
                Raise(nameof(StatusText));
                Raise(nameof(ProgressText));
                Raise(nameof(Progress));
                Raise(nameof(IsDownloading));
                Raise(nameof(SpeedText));
                Raise(nameof(ProgressSpeedText));
                Raise(nameof(Eta));
                Raise(nameof(DownloadedOfTotalText));
                Raise(nameof(DisplaySizeText));
                Raise(nameof(CompletionPercentText));
                Raise(nameof(RowTelemetryStatusText));
                Raise(nameof(PrimaryStatusText));
                Raise(nameof(RowTimeOrEtaText));
                Raise(nameof(EtaOrDashText));
                Raise(nameof(HasFailureError));
            }
        }
    }

    private bool _isSizeEstimated;
    /// <summary>True when TotalBytes is a bitrate/extrapolated estimate
    /// (unmeasurable HLS), not a probed length. Shown with a "~" prefix.</summary>
    public bool IsSizeEstimated
    {
        get => _isSizeEstimated;
        set
        {
            if (Set(ref _isSizeEstimated, value))
            {
                Raise(nameof(SizeText));
                Raise(nameof(DisplaySizeText));
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
                Raise(nameof(CompletionPercentText));
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
                Raise(nameof(CompletionPercentText));
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
                // Computed live so upward TotalBytes revisions (refresh-link,
                // re-probe) move the bar back down instead of pinning it at a
                // stale high watermark. TotalBytes/DownloadedBytes setters keep
                // _progress in sync for the unknown-size path below.
                return (int)Math.Clamp((double)DownloadedBytes * 100.0 / TotalBytes, 0, 100);
            }
            return _progress;
        }
        set
        {
            if (Set(ref _progress, value))
            {
                Raise(nameof(ProgressText));
                Raise(nameof(ProgressSpeedText));
                Raise(nameof(CompletionPercentText));
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
        get
        {
            if (Status != TaskStatus.Downloading)
                return "-";
            if (!string.IsNullOrEmpty(_eta))
                return _eta;
            // Preparing gap (resolve/probe) + early 1% stall: never blank.
            return _isPreparing ? "…" : "-";
        }
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

    /// <summary>Full guidance for the failed task (hover tooltip). The
    /// <see cref="Error"/> row text stays short; this keeps the actionable
    /// detail one hover away.</summary>
    private string? _errorDetail;
    public string? ErrorDetail
    {
        get => _errorDetail;
        set => Set(ref _errorDetail, value);
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

    /// <summary>True from session start until the first bytes actually flow.
    /// Covers embed resolving, title sync, HEAD/probe, HLS playlist + segment
    /// probing and ffmpeg spawn — the window where the progress dialog would
    /// otherwise show empty stats. Set/cleared by the engine only.</summary>
    private bool _isPreparing;
    public bool IsPreparing
    {
        get => _isPreparing;
        set
        {
            if (Set(ref _isPreparing, value))
            {
                Raise(nameof(SpeedText));
                Raise(nameof(Eta));
                Raise(nameof(RowTelemetryStatusText));
            }
        }
    }

    /// <summary>Human-readable phase shown under the file name while
    /// <see cref="IsPreparing"/> is true, e.g. "Resolving stream…".</summary>
    private string _phaseText = "";
    public string PhaseText
    {
        get => _phaseText;
        set
        {
            if (Set(ref _phaseText, value))
                Raise(nameof(RowTelemetryStatusText));
        }
    }

    public string SizeText => TotalBytes > 0
        ? (IsSizeEstimated ? "~" + FormatBytes(TotalBytes) : FormatBytes(TotalBytes))
        : "-";

    public string CompletionPercentText
    {
        get
        {
            if (Status == TaskStatus.Completed)
                return "100%";
            if (TotalBytes > 0)
                return $"{Progress}%";
            return "-";
        }
    }

    public string DisplaySizeText
    {
        get
        {
            if (TotalBytes > 0)
            {
                if (Status == TaskStatus.Completed)
                    return $"{SizeText} / {SizeText}";
                if (DownloadedBytes > 0)
                    return $"{DownloadedText} / {SizeText}";
                return $"0 B / {SizeText}";
            }

            // TotalBytes <= 0: total size cannot be calculated
            if (DownloadedBytes > 0)
                return DownloadedText;

            return "-";
        }
    }

    public string DownloadedText => FormatBytes(DownloadedBytes);

    public string ProgressText => Status == TaskStatus.Downloading ? $"{Progress}%" : "";

    public bool IsDownloading => Status == TaskStatus.Downloading;

    public string SpeedText
    {
        get
        {
            if (Status != TaskStatus.Downloading)
                return "-";
            if (SpeedBps >= 1)
                return $"{FormatBytes((long)SpeedBps)}/s";
            // Preparing gap + 1% stall: never blank so the row never collapses.
            return _isPreparing ? "…" : "0 B/s";
        }
    }

    public string DomainText
    {
        get
        {
            if (Uri.TryCreate(Url, UriKind.Absolute, out var uri))
                return uri.Host;
            return "-";
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
        TaskStatus.Downloading => _isPreparing && !string.IsNullOrWhiteSpace(_phaseText) ? _phaseText
            : (!string.IsNullOrEmpty(Eta) ? Eta : "Estimating…"),
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

    public string DownloadedOfTotalText => DisplaySizeText;

    public long RemainingBytes => TotalBytes > DownloadedBytes ? TotalBytes - DownloadedBytes : 0;
    public string RemainingBytesText => TotalBytes > 0 && RemainingBytes > 0 ? FormatBytes(RemainingBytes) : (Status == TaskStatus.Completed ? "0 B" : "-");
    public string ConnectionsText => ChunkCount > 1 ? $"{ChunkCount} threads" : "1 thread";
    public string EtaDetailText => !string.IsNullOrEmpty(Eta) ? $"{Eta} remaining" : (Status == TaskStatus.Downloading ? "Calculating…" : "-");
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
        var args = new PropertyChangedEventArgs(name);
        if (SynchronizationContext.Current == _sync)
        {
            handler(this, args);
        }
        else
        {
            try
            {
                // UI thread captured at construction: Post == Dispatcher.BeginInvoke.
                // Falls back to inline Send when no context was captured.
                _sync.Post(_ => handler(this, args), null);
            }
            catch (Exception)
            {
                // Target context may have shut down between check and invoke
            }
        }
    }
}
