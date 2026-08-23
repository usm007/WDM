using System.Net;
using System.Text.RegularExpressions;

namespace WDM.Services;

/// <summary>
/// Shared helpers for turning server hints (Content-Type, Content-Disposition,
/// S3-style query parameters) into a usable, correctly-extended filename.
/// </summary>
public static class FileNameHelper
{
    /// <summary>Maps common media/archive MIME types to a file extension.</summary>
    private static readonly Dictionary<string, string> MimeToExtension = new(StringComparer.OrdinalIgnoreCase)
    {
        // Video
        ["video/mp4"] = ".mp4",
        ["video/x-m4v"] = ".m4v",
        ["video/mkv"] = ".mkv",
        ["video/x-matroska"] = ".mkv",
        ["video/webm"] = ".webm",
        ["video/quicktime"] = ".mov",
        ["video/x-msvideo"] = ".avi",
        ["video/x-ms-wmv"] = ".wmv",
        ["video/x-flv"] = ".flv",
        ["video/mp2t"] = ".ts",
        ["video/mpeg"] = ".mpeg",
        ["video/3gpp"] = ".3gp",
        ["video/ogg"] = ".ogv",
        // Streaming manifests
        ["application/vnd.apple.mpegurl"] = ".m3u8",
        ["application/x-mpegurl"] = ".m3u8",
        ["application/mpegurl"] = ".m3u8",
        ["application/dash+xml"] = ".mpd",
        // Audio
        ["audio/mpeg"] = ".mp3",
        ["audio/mp3"] = ".mp3",
        ["audio/mp4"] = ".m4a",
        ["audio/x-m4a"] = ".m4a",
        ["audio/aac"] = ".aac",
        ["audio/ogg"] = ".ogg",
        ["audio/opus"] = ".opus",
        ["audio/flac"] = ".flac",
        ["audio/wav"] = ".wav",
        ["audio/x-wav"] = ".wav",
        ["audio/webm"] = ".webm",
        ["audio/x-ms-wma"] = ".wma",
        // Archives / installers
        ["application/zip"] = ".zip",
        ["application/x-zip-compressed"] = ".zip",
        ["application/x-rar-compressed"] = ".rar",
        ["application/vnd.rar"] = ".rar",
        ["application/x-7z-compressed"] = ".7z",
        ["application/gzip"] = ".gz",
        ["application/x-gzip"] = ".gz",
        ["application/x-tar"] = ".tar",
        ["application/x-bzip2"] = ".bz2",
        ["application/x-xz"] = ".xz",
        // Documents
        ["application/pdf"] = ".pdf",
        ["application/epub+zip"] = ".epub",
        // Executables / packages
        ["application/octet-stream"] = "",
        ["application/x-msdownload"] = ".exe",
        ["application/vnd.android.package-archive"] = ".apk",
        ["application/vnd.apple.installer+xml"] = ".mpkg",
        ["application/x-apple-diskimage"] = ".dmg",
        ["application/x-iso9660-image"] = ".iso",
    };

    /// <summary>
    /// Best-effort mapping from a MIME content-type string to a file extension,
    /// e.g. "video/mp4; codecs=..." -> ".mp4". Empty when unknown.
    /// </summary>
    public static string ExtensionFromMime(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType))
            return "";
        string type = contentType.Split(';')[0].Trim();
        if (MimeToExtension.TryGetValue(type, out string? ext))
            return ext;

        // Fall back to generic families for types we know are media but not mapped exactly.
        if (type.StartsWith("video/", StringComparison.OrdinalIgnoreCase)) return ".mp4";
        if (type.StartsWith("audio/", StringComparison.OrdinalIgnoreCase)) return ".mp3";
        if (type.StartsWith("image/", StringComparison.OrdinalIgnoreCase)) return ".jpg";
        return "";
    }

    /// <summary>
    /// Parses a Content-Disposition header value (handling RFC 2231 <c>filename*=</c>,
    /// RFC 5987 encoded values, and plain <c>filename=</c>) into the server-provided name.
    /// Returns null when absent or empty.
    /// </summary>
    public static string? ParseDispositionFileName(string? disposition)
    {
        if (string.IsNullOrWhiteSpace(disposition))
            return null;

        string? name = null;

        // RFC 2231: filename*=UTF-8''<percent-encoded>  (or charset'lang'value)
        var star = Regex.Match(disposition, @"filename\*\s*=\s*(?:[^']*'[^']*')?([^;]+)", RegexOptions.IgnoreCase);
        if (star.Success)
        {
            string candidate = star.Groups[1].Value.Trim().Trim('"');
            try
            {
                name = Uri.UnescapeDataString(candidate);
            }
            catch (Exception)
            {
                name = candidate;
            }
        }

        // RFC 2231 continuation: filename*0*=..., filename*1*=... (rare, but S3/Drive use it)
        if (name is null)
        {
            var parts = new List<string>();
            var cont = Regex.Matches(disposition, @"filename\*(\d+)(\*?)\s*=\s*([^;]+)", RegexOptions.IgnoreCase)
                .Cast<Match>()
                .OrderBy(m => int.Parse(m.Groups[1].Value));
            foreach (Match m in cont)
            {
                string chunk = m.Groups[3].Value.Trim().Trim('"');
                if (m.Groups[2].Value == "*")
                {
                    try { chunk = Uri.UnescapeDataString(chunk); } catch (Exception) { }
                }
                parts.Add(chunk);
            }
            if (parts.Count > 0)
                name = string.Concat(parts);
        }

        // Plain: filename="foo.bin"
        if (name is null)
        {
            var plain = Regex.Match(disposition, @"filename\s*=\s*""?([^"";]+)""?", RegexOptions.IgnoreCase);
            if (plain.Success)
                name = plain.Groups[1].Value.Trim();
        }

        name = name?.Trim('"').Trim();
        return string.IsNullOrWhiteSpace(name) ? null : name;
    }

    /// <summary>
    /// Extracts a filename from an S3-style <c>response-content-disposition</c> query
    /// parameter, e.g. <c>response-content-disposition=attachment;%20filename=%22x.mkv%22</c>.
    /// </summary>
    public static string? FileNameFromS3Query(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.Query))
            return null;

        string? rcd = null;
        foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = pair.IndexOf('=');
            string key = eq < 0 ? pair : pair.Substring(0, eq);
            string value = eq < 0 ? "" : pair.Substring(eq + 1);
            if (string.Equals(key, "response-content-disposition", StringComparison.OrdinalIgnoreCase)
                || string.Equals(key, "x-content-disposition", StringComparison.OrdinalIgnoreCase))
            {
                try { rcd = Uri.UnescapeDataString(value); } catch (Exception) { rcd = value; }
                break;
            }
        }
        return ParseDispositionFileName(rcd);
    }

    /// <summary>
    /// Appends an extension to a name if it doesn't already have one. If the name is
    /// empty, produces <c>download_&lt;timestamp&gt;&lt;ext&gt;</c>.
    /// </summary>
    public static string EnsureExtension(string name, string extension, DateTime now)
    {
        string ext = extension.Trim();
        if (!ext.StartsWith('.') && ext.Length > 0)
            ext = "." + ext;
        if (string.IsNullOrWhiteSpace(name))
            return $"download_{now:yyyyMMdd_HHmmss}{ext}";
        if (ext.Length > 0 && !Path.HasExtension(name))
            return name + ext;
        return name;
    }
}
