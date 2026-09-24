using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
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
                .OrderBy(m => int.TryParse(m.Groups[1].Value, out int idx) ? idx : 0);
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

        // 4. Tier 1 & Tier 2: Browser page title and referer slug hints
        // CRITICAL: Only use page title or referer when the current stem is generic / uninformative
        // (manifests, hashes, random tokens, pure numbers, or generic words like "video", "download").
        // If the stem already has a meaningful, specific filename (e.g. movie, software, document),
        // we MUST preserve it and NEVER overwrite it with the browser tab's title!
        if (IsGenericStem(stem))
        {
            if (!string.IsNullOrWhiteSpace(pageTitle))
            {
                string fromPage = CleanPageTitle(pageTitle);
                if (!string.IsNullOrWhiteSpace(fromPage) && fromPage.Length >= 4 && !IsGenericPageTitle(fromPage))
                {
                    string tags = ExtractQualityTags(stem);
                    if (!string.IsNullOrWhiteSpace(tags) && !fromPage.Contains(tags, StringComparison.OrdinalIgnoreCase))
                        return FinalizeName($"{fromPage} {tags}", ext);
                    return FinalizeName(fromPage, ext);
                }
            }

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

        // E1. Strip generic web-series noise that clutters video names
        name = Regex.Replace(name, @"(?i)\bCompleted(\s+Web)?(\s+Series)?\b", " ");
        name = Regex.Replace(name, @"(?i)\bWeb\s*Series\b", " ");
        name = Regex.Replace(name, @"(?i)\bE\s*Sub(s)?\b", " ");
        name = Regex.Replace(name, @"(?i)\s*\(Ep\.\s*\d+\s*-\s*\d+\)\s*", " ");

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
            ["Agnst"] = "Against",
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
        name = Regex.Replace(name, @"\((.*?)\)", match => (keepRegex.IsMatch(match.Value) || Regex.IsMatch(match.Value, @"^\(?\d+\)?$")) ? match.Value : "");

        // I. Enforce video format: [Name] [year] [language] [quality]
        string formatted = TryFormatVideoName(name, ext);
        if (formatted is not null)
            return FinalizeName(formatted, ext);

        return FinalizeName(name, ext);
    }

    private static string FinalizeName(string stem, string ext)
    {
        string name = Regex.Replace(stem, @"\s+", " ").Trim();
        name = name.Trim('-', ' ', '.', '_', ',', '|', '~', ':', ';');

        if (string.IsNullOrWhiteSpace(name))
            name = $"download_{DateTime.Now:yyyyMMdd_HHmmss}";

        name = HardenStem(name);
        ext = SanitizeExtension(ext);

        if (!string.IsNullOrEmpty(ext))
        {
            if (!ext.StartsWith('.')) ext = "." + ext;
            return name + ext;
        }
        return name;
    }

    /// <summary>Last line of defense for every filename tier: strips path
    /// separators/traversal, reserved device names, leading dots and over-long
    /// stems. All <see cref="FinalizeName"/> callers are covered.</summary>
    private static string HardenStem(string name)
    {
        name = name.Replace('/', ' ').Replace('\\', ' ');
        name = name.Replace("..", " ");
        foreach (char c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, ' ');
        name = Regex.Replace(name, @":", " ");
        name = Regex.Replace(name, @"\s+", " ").Trim();
        name = name.TrimStart('.');
        name = name.Trim('-', ' ', '_', ',', '|', '~', ';');
        if (string.IsNullOrWhiteSpace(name))
            return $"download_{DateTime.Now:yyyyMMddHHmmss}";
        // Reserved Windows device names (CON, PRN, AUX, NUL, COM1-9, LPT1-9).
        string upper = name.ToUpperInvariant();
        int dot = upper.IndexOf('.');
        string basePart = dot > 0 ? upper[..dot] : upper;
        if (basePart is "CON" or "PRN" or "AUX" or "NUL" ||
            Regex.IsMatch(basePart, @"^(COM[1-9]|LPT[1-9])$"))
            name = "_" + name;
        const int maxStem = 120;
        if (name.Length > maxStem)
            name = name[..maxStem].TrimEnd();
        return string.IsNullOrWhiteSpace(name) ? $"download_{DateTime.Now:yyyyMMddHHmmss}" : name;
    }

    private static string SanitizeExtension(string ext)
    {
        if (string.IsNullOrWhiteSpace(ext))
            return "";
        ext = ext.Trim();
        if (!ext.StartsWith('.'))
            ext = "." + ext;
        string body = ext[1..];
        if (body.Length is < 1 or > 10 || !Regex.IsMatch(body, @"^[A-Za-z0-9]+$"))
            return "";
        return "." + body.ToLowerInvariant();
    }

    public static string CleanVideoFileName(string fileName, string? pageTitle = null, string? referer = null) =>
        SmartSanitizeFileName(fileName, pageTitle, referer);

    /// <summary>
    /// Checks if a file stem is generic/uninformative (e.g. manifest name, random token, pure numbers,
    /// or generic words like "video", "download", "master", "index", etc.) such that
    /// a browser page title or referer slug would provide a much better filename.
    /// Returns false if the stem is already a specific, meaningful title (e.g. "Green Lantern", "ubuntu-22.04", etc.).
    /// </summary>
    public static bool IsGenericStem(string? stem)
    {
        if (string.IsNullOrWhiteSpace(stem)) return true;
        string s = stem.Trim();
        if (IsManifestStem(s)) return true;
        if (s.StartsWith("download_", StringComparison.OrdinalIgnoreCase)) return true;
        if (s.Equals("download", StringComparison.OrdinalIgnoreCase) ||
            s.Equals("file", StringComparison.OrdinalIgnoreCase) ||
            s.Equals("document", StringComparison.OrdinalIgnoreCase) ||
            s.Equals("default", StringComparison.OrdinalIgnoreCase) ||
            s.Equals("videoplayback", StringComparison.OrdinalIgnoreCase) ||
            s.Equals("get_video", StringComparison.OrdinalIgnoreCase) ||
            s.Equals("media", StringComparison.OrdinalIgnoreCase) ||
            s.Equals("stream", StringComparison.OrdinalIgnoreCase) ||
            s.Equals("source", StringComparison.OrdinalIgnoreCase) ||
            s.Equals("asset", StringComparison.OrdinalIgnoreCase) ||
            s.Equals("blob", StringComparison.OrdinalIgnoreCase) ||
            s.Equals("attachment", StringComparison.OrdinalIgnoreCase) ||
            s.Equals("content", StringComparison.OrdinalIgnoreCase))
            return true;

        if (Guid.TryParse(s, out _)) return true;

        // Strip known extension if still attached
        int dot = s.LastIndexOf('.');
        if (dot > 0) s = s[..dot];

        // Pure digits or digits with delimiters e.g. "12345678" or "12_34_56"
        if (s.All(c => char.IsDigit(c) || c is '_' or '-' or '.')) return true;

        // Long hex or alphanumeric random hashes e.g. "2735e051cc694963100cf875399f5b71" or "168095063_480p_h264_init_k5Opf1yJuSMMz3rv"
        if (s.Length >= 16 && s.All(c => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F') || c is '_' or '-'))
            return true;

        // Generic chunk/init segment names or stream init tokens (e.g. 168095063_480p_h264_init_k5Opf1yJuSMMz3rv)
        if (Regex.IsMatch(s, @"(^|[-_])(init|segment|seg|chunk|part|frag)([-_0-9a-zA-Z]*|$)", RegexOptions.IgnoreCase) ||
            Regex.IsMatch(s, @"^\d{6,}[-_]", RegexOptions.IgnoreCase))
            return true;

        return false;
    }

    /// <summary>
    /// Checks if a page title is just a cloud storage host branding / tagline rather than a content title.
    /// </summary>
    public static bool IsGenericPageTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return true;
        string t = title.Trim();
        if (t.Length < 3) return true;
        return Regex.IsMatch(t,
            @"^(Seedr(\s*[:\-–—]?\s*.*)?|Google Drive|OneDrive|Dropbox|MediaFire|Mega|iCloud|Home|Index|Untitled|Welcome|Download|Direct Download)$",
            RegexOptions.IgnoreCase);
    }
    /// <summary>[Name] [year] [language] [quality] — the only video name shape.
    /// Returns null when the input doesn't look like a video title (no year /
    /// quality signal), so callers fall back to the existing heuristic.</summary>
    private static string? TryFormatVideoName(string stem, string ext)
    {
        if (string.IsNullOrWhiteSpace(stem)) return null;
        string lowerExt = (ext ?? "").ToLowerInvariant();
        bool isVideo = lowerExt is ".mp4" or ".mkv" or ".avi" or ".mov" or ".webm" or ".ts" or ".flv" or ".m4v";
        if (!isVideo) return null;

        // Must have at least a year or a quality token to qualify as a video release name.
        var yearMatch = Regex.Match(stem, @"\(?(19|20)\d{2}\)?");
        bool hasQuality = Regex.IsMatch(stem, @"(?i)\b(480p|720p|1080p|2160p|4K|10bit|HDR|HEVC|x264|x265|WEB-DL|BluRay)\b");
        if (!yearMatch.Success && !hasQuality) return null;

        string working = stem;

        // Extract year (keep parentheses form)
        string? year = null;
        var ym = Regex.Match(working, @"\((19|20)\d{2}\)");
        if (ym.Success) year = ym.Value;
        else
        {
            var ym2 = Regex.Match(working, @"\b(19|20)\d{2}\b");
            if (ym2.Success) year = $"({ym2.Value})";
        }
        if (year is not null) working = Regex.Replace(working, Regex.Escape(year), " ", RegexOptions.IgnoreCase);

        // Extract language (first match wins)
        string? language = null;
        var langRx = new Regex(@"(?i)\b(Hindi|English|Tamil|Telugu|Malayalam|Kannada|Bengali|Marathi|Punjabi|Dual Audio|Multi Audio)\b");
        var lm = langRx.Match(working);
        if (lm.Success) { language = lm.Value; working = working.Remove(lm.Index, lm.Length).Insert(lm.Index, " "); }

        // Extract quality tokens in order of appearance: 480p/720p/1080p/4K/HEVC/x264 etc
        var qualityTokens = new List<string>();
        var qRx = new Regex(@"(?i)\b(480p|720p|1080p|2160p|4K|HEVC|x264|x265|10bit|HDR|BluRay|WEB-DL)\b");
        foreach (Match m in qRx.Matches(working))
            if (!qualityTokens.Any(t => string.Equals(t, m.Value, StringComparison.OrdinalIgnoreCase)))
                qualityTokens.Add(m.Value);
        working = qRx.Replace(working, " ");

        // Remaining is the name — collapse, strip trailing dashes/parens
        string name = Regex.Replace(working, @"\s+", " ").Trim().Trim('-', ' ', '.', '_', ',', '|', '~', ':', ';', '(', ')');
        name = Regex.Replace(name, @"\s+", " ").Trim();
        if (string.IsNullOrWhiteSpace(name)) return null;

        var parts = new List<string> { name };
        if (year is not null) parts.Add(year);
        if (language is not null) parts.Add(language);
        if (qualityTokens.Count > 0) parts.Add(string.Join(" ", qualityTokens));
        return string.Join(" ", parts);
    }

    /// <summary>True for manifest/chunklist basenames that carry no title
    /// ("master", "index", "playlist", "index-v1-a1", "seg-12", ...).</summary>
    public static bool IsManifestStem(string? stem)
    {
        if (string.IsNullOrWhiteSpace(stem)) return true;
        string s = stem.Trim();
        // Strip one media extension before comparing ("master.m3u8" -> "master").
        int dot = s.LastIndexOf('.');
        if (dot > 0) s = s[..dot];
        return Regex.IsMatch(s,
            @"^(master|index|playlist|chunklist|manifest|stream|play|video|media|file|download|index-v1-a\d+|seg-?\d*)$",
            RegexOptions.IgnoreCase);
    }

    public static string CleanPageTitle(string pageTitle)
    {
        if (string.IsNullOrWhiteSpace(pageTitle)) return "";
        string title = pageTitle.Trim();

        // Strip player prefixes e.g. "Watch My Film", "Now Playing: My Film".
        title = Regex.Replace(title, @"^\s*(Watch|Now Playing)\s*[:\-–—]\s*", "", RegexOptions.IgnoreCase);
        title = Regex.Replace(title, @"^\s*(Watch|Now Playing)\s+", "", RegexOptions.IgnoreCase);

        // Strip site suffix e.g. " - World4uFree", " | Vegamovies", " » 1TamilMV"
        title = Regex.Replace(title, @"\s*[-–—|»•]\s*(World4uFree|Vegamovies|1TamilMV|Bolly4u|MoviesMod|Khatrimaza|FilmyZilla|9xmovies|Pagalworld|Mp4moviez|.*?\.(vu|org|com|net|in|cc|ws|top|vip|site)).*$", "", RegexOptions.IgnoreCase);

        // Generic trailing site tag when the known-site list missed
        // ("My Film - VOE", "Show | dood"): strip the last " - X" chunk only when
        // X looks like a site tag (dotted, very short, or all-caps) so real
        // subtitles ("Episode 5 - Finale") survive.
        title = Regex.Replace(title, @"\s*[-–—|»•]\s*([^-–—|»•]{1,32})$", m =>
        {
            string chunk = m.Groups[1].Value.Trim();
            bool siteLike = chunk.Contains('.')
                || chunk.Length <= 5
                || Regex.IsMatch(chunk, @"^[A-Z0-9]{2,}$");
            return siteLike ? "" : m.Value;
        }, RegexOptions.None).Trim();

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
