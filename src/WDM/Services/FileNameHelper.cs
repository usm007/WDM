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

    /// <summary>
    /// Intelligently cleans, expands, decodes, and normalizes all filenames (video, audio, software, documents, archives).
    /// Resolves messy dot-separated titles, CamelCase squashed words, shorthand resolution/codecs (72pHV -> 720p HEVC),
    /// URL-encodings, trailing dots, and strips site watermarks and promotional junk.
    /// </summary>
    public static string SmartSanitizeFileName(string fileName, string? pageTitle = null, string? referer = null)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return "";

        // 1. URL Unescape & Unicode control character cleaning
        string raw = fileName.Trim();
        try { raw = Uri.UnescapeDataString(raw); } catch { }
        raw = Regex.Replace(raw, @"[\u200B-\u200F\uFEFF\u0000-\u001F]", "");

        // 2. Extract valid extension (handling edge cases like double extensions or trailing dots)
        string ext = Path.GetExtension(raw);
        string stem = Path.GetFileNameWithoutExtension(raw);

        // If no extension found or raw ends with a dot
        if (string.IsNullOrEmpty(ext) && raw.Contains('.'))
        {
            int lastDot = raw.LastIndexOf('.');
            if (lastDot > 0 && lastDot < raw.Length - 1)
            {
                string possibleExt = raw.Substring(lastDot);
                if (possibleExt.Length is >= 2 and <= 6 && !possibleExt.Contains(' '))
                {
                    ext = possibleExt;
                    stem = raw.Substring(0, lastDot);
                }
            }
        }

        // Clean up double extensions (e.g. .mkv.mkv or .mp4.bin)
        if (!string.IsNullOrEmpty(ext))
        {
            string subExt = Path.GetExtension(stem);
            if (string.Equals(ext, subExt, StringComparison.OrdinalIgnoreCase))
                stem = Path.GetFileNameWithoutExtension(stem);
        }

        // 3. Clean invalid file name characters
        foreach (char c in Path.GetInvalidFileNameChars())
            stem = stem.Replace(c, ' ');

        // 4. Tier 1: Browser page title hint if available
        if (!string.IsNullOrWhiteSpace(pageTitle))
        {
            string fromPage = CleanPageTitle(pageTitle);
            if (!string.IsNullOrWhiteSpace(fromPage) && fromPage.Length >= 4)
            {
                string tags = ExtractQualityTags(stem);
                if (!string.IsNullOrWhiteSpace(tags) && !fromPage.Contains(tags, StringComparison.OrdinalIgnoreCase))
                    return FinalizeName($"{fromPage} {tags}", ext);
                return FinalizeName(fromPage, ext);
            }
        }

        // 5. Tier 2: Referer URL slug hint if available
        if (!string.IsNullOrWhiteSpace(referer) && Uri.TryCreate(referer, UriKind.Absolute, out var refUri))
        {
            string slug = refUri.AbsolutePath.Trim('/');
            if (!string.IsNullOrWhiteSpace(slug) && slug.Contains('-'))
            {
                string lastPart = slug.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? "";
                string fromSlug = CleanSlug(lastPart);
                if (!string.IsNullOrWhiteSpace(fromSlug) && fromSlug.Length >= 6 && !fromSlug.Equals("download", StringComparison.OrdinalIgnoreCase))
                {
                    string tags = ExtractQualityTags(stem);
                    if (!string.IsNullOrWhiteSpace(tags) && !fromSlug.Contains(tags, StringComparison.OrdinalIgnoreCase))
                        return FinalizeName($"{fromSlug} {tags}", ext);
                    return FinalizeName(fromSlug, ext);
                }
            }
        }

        // 6. Tier 3: Deep Heuristic De-Mangling on stem
        string name = stem;

        // A. Expand shorthand video qualities and codecs
        name = Regex.Replace(name, @"(?i)\b72p\s*HV\b|\b72pHV\b|\b720p\s*HV\b|\b720pHV\b", " 720p HEVC ");
        name = Regex.Replace(name, @"(?i)\b108p\s*HV\b|\b108pHV\b|\b1080p\s*HV\b|\b1080pHV\b", " 1080p HEVC ");
        name = Regex.Replace(name, @"(?i)\b48p\s*HV\b|\b48pHV\b|\b480p\s*HV\b|\b480pHV\b", " 480p HEVC ");
        name = Regex.Replace(name, @"(?i)\b2160p\s*HV\b|\b2160pHV\b|\b4k\s*HV\b|\b4kHV\b", " 4K HEVC ");
        name = Regex.Replace(name, @"(?i)\b72p\b", " 720p ");
        name = Regex.Replace(name, @"(?i)\b108p\b", " 1080p ");
        name = Regex.Replace(name, @"(?i)\b48p\b", " 480p ");
        name = Regex.Replace(name, @"(?i)(?<=\d{3,4}p|\b)\s*HV\b", " HEVC ");

        // B. Standardize Season & Episode markers (e.g. s01e05, S1 E5, S01.E05, Season 1 Episode 5 -> S01E05)
        name = Regex.Replace(name, @"(?i)\b(?:season|ssn|seas)\s*(\d{1,2})\s*(?:episode|ep|eps|e)\s*(\d{1,3})\b",
            m => $" S{int.Parse(m.Groups[1].Value):D2}E{int.Parse(m.Groups[2].Value):D2} ");
        name = Regex.Replace(name, @"(?i)\b[sS](\d{1,2})[\s._\-]?[eE](\d{1,3})\b",
            m => $" S{int.Parse(m.Groups[1].Value):D2}E{int.Parse(m.Groups[2].Value):D2} ");

        // C. Standardize Quality / Codec tokens
        name = Regex.Replace(name, @"(?i)\b1080p\b", "1080p");
        name = Regex.Replace(name, @"(?i)\b720p\b", "720p");
        name = Regex.Replace(name, @"(?i)\b480p\b", "480p");
        name = Regex.Replace(name, @"(?i)\b2160p\b", "2160p");
        name = Regex.Replace(name, @"(?i)\b4k\b", "4K");
        name = Regex.Replace(name, @"(?i)\b10bit\b", "10bit");
        name = Regex.Replace(name, @"(?i)\b(x264|h\.264|h264)\b", "x264");
        name = Regex.Replace(name, @"(?i)\b(x265|h\.265|h265|hevc)\b", "HEVC");
        name = Regex.Replace(name, @"(?i)\b(web-dl|webdl|webrip)\b", "WEB-DL");
        name = Regex.Replace(name, @"(?i)\b(bluray|brrip|bdrip)\b", "BluRay");

        // D. Strip watermarks and site domain branding
        var sitePatterns = new[]
        {
            @"(?i)\bworld4ufree(\s*(vu|org|com|cc|ws|vip|top|me|link|site|in))?\b",
            @"(?i)\bvegamovies(\s*(yt|nl|dad|is|in|org|com|cc|ws|vip|top|me|link|site))?\b",
            @"(?i)\bbolly4u(\s*(org|com|cc|ws|vip|top|me|link|site|in))?\b",
            @"(?i)\b1tamilmv(\s*(org|com|cc|ws|vip|top|me|link|site|in|cz))?\b",
            @"(?i)\bmoviesmod(\s*(org|com|cc|ws|vip|top|me|link|site|in|cc|at))?\b",
            @"(?i)\bkhatrimaza(\s*(org|com|cc|ws|vip|top|me|link|site|in))?\b",
            @"(?i)\bfilmyzilla(\s*(org|com|cc|ws|vip|top|me|link|site|in))?\b",
            @"(?i)\b9xmovies(\s*(org|com|cc|ws|vip|top|me|link|site|in))?\b",
            @"(?i)\b(pagalworld|mp4moviez|yts\.mx|yts|yify|eztv|psa|rarbg|tigole|qxr|megusta|galaxytt|galaxyrg|1337x|mkvcinemas)\b",
            @"(?i)\b(www\s+[a-z0-9\-]+\s+(com|org|net|in|vu|cc|ws))\b",
            @"(?i)\b(download\s+(full\s+movie|hd|movie|in\s+hindi))\b",
            @"(?i)\s+(vu|cc|ws|top|vip|site)\s*$"
        };
        foreach (var pattern in sitePatterns)
            name = Regex.Replace(name, pattern, " ");

        // E. Replace dots and underscores with spaces (protecting decimal numbers/versions like 5.1, v1.2.3)
        name = name.Replace('_', ' ');
        name = Regex.Replace(name, @"(?<=[a-zA-Z])\.(?=[a-zA-Z0-9])|(?<=[0-9])\.(?=[a-zA-Z])", " ");
        name = Regex.Replace(name, @"\.{2,}", " ");
        if (name.Count(c => c == '.') > 2 && !name.Contains(' '))
            name = name.Replace('.', ' ');

        // F. Split CamelCase if words were concatenated without spaces
        name = Regex.Replace(name, @"([a-z0-9])([A-Z])", "$1 $2");
        name = Regex.Replace(name, @"([A-Z]+)([A-Z][a-z])", "$1 $2");

        // G. Expand known compressed title abbreviations
        var expansions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["T One"] = "The One",
            ["Tone"] = "The One",
            ["Wlk"] = "Walk",
            ["Agnst"] = "Against",
            ["T Ran"] = "The Rain",
            ["Tran"] = "The Rain",
            ["Ssn"] = "Season",
            ["Seas"] = "Season",
            ["Ep"] = "Episode",
            ["Eps"] = "Episodes"
        };
        foreach (var kv in expansions)
            name = Regex.Replace(name, $@"\b{Regex.Escape(kv.Key)}\b", kv.Value, RegexOptions.IgnoreCase);

        // H. Clean bracketed noise unless it contains a keep tag
        var keepRegex = new Regex(@"(1080p|720p|4k|2160p|480p|x264|h264|x265|hevc|10bit|hdr|aac|dts|5\.1|7\.1|bluray|web-dl|webrip|S\d{2}E\d{2})", RegexOptions.IgnoreCase);
        name = Regex.Replace(name, @"\[(.*?)\]", match => keepRegex.IsMatch(match.Value) ? match.Value.Trim('[', ']') : "");
        name = Regex.Replace(name, @"\((.*?)\)", match => (keepRegex.IsMatch(match.Value) || Regex.IsMatch(match.Value, @"^\(?\d{4}\)?$")) ? match.Value : "");

        return FinalizeName(name, ext);
    }

    private static string FinalizeName(string stem, string ext)
    {
        string name = Regex.Replace(stem, @"\s+", " ").Trim();
        name = name.Trim('-', ' ', '.', '_', ',', '|', '~', ':', ';');

        if (string.IsNullOrWhiteSpace(name))
            name = $"download_{DateTime.Now:yyyyMMdd_HHmmss}";

        if (!string.IsNullOrEmpty(ext))
        {
            if (!ext.StartsWith('.')) ext = "." + ext;
            return name + ext;
        }
        return name;
    }

    public static string CleanVideoFileName(string fileName, string? pageTitle = null, string? referer = null) =>
        SmartSanitizeFileName(fileName, pageTitle, referer);

    public static string CleanPageTitle(string pageTitle)
    {
        if (string.IsNullOrWhiteSpace(pageTitle)) return "";
        string title = pageTitle.Trim();

        // Strip site suffix e.g. " - World4uFree", " | Vegamovies", " » 1TamilMV"
        title = Regex.Replace(title, @"\s*[-–—|»•]\s*(World4uFree|Vegamovies|1TamilMV|Bolly4u|MoviesMod|Khatrimaza|FilmyZilla|9xmovies|Pagalworld|Mp4moviez|.*?\.(vu|org|com|net|in|cc|ws|top|vip|site)).*$", "", RegexOptions.IgnoreCase);

        // Strip common promotional marketing buzzwords
        title = Regex.Replace(title, @"(?i)\b(Full Movie Download|Movie Download|Download in|Free Download|Watch Online|Direct Link|Download HD|Download Full Movie|Full Movie|Download)\b", " ");

        // Normalize quality/audio
        title = Regex.Replace(title, @"(?i)\b(Hindi Dubbed|Dual Audio|Multi Audio)\b", " ");

        title = Regex.Replace(title, @"\s+", " ").Trim();
        title = title.Trim('-', ' ', '|', ':', '•');
        return title;
    }

    public static string CleanSlug(string slug)
    {
        if (string.IsNullOrWhiteSpace(slug)) return "";
        string name = slug.Replace("-", " ").Replace("_", " ");
        name = Regex.Replace(name, @"(?i)\b(full\s+movie|movie|download|watch\s+online|hindi\s+dubbed|dual\s+audio)\b", " ");
        name = Regex.Replace(name, @"\s+", " ").Trim();
        return System.Globalization.CultureInfo.CurrentCulture.TextInfo.ToTitleCase(name);
    }

    public static string ExtractQualityTags(string fileName)
    {
        var match = Regex.Match(fileName, @"(?i)\b(2160p|4k|1080p|720p|480p|hevc|x265|x264|h264|10bit|hdr|bluray|web-dl|webrip)\b");
        return match.Success ? match.Value : "";
    }
}
