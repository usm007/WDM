using System.Text.Json;

namespace WDM.Services;

public sealed record MediaItem
{
    public string Id { get; init; } = "";
    public string Title { get; init; } = "";
    public string Channel { get; init; } = "";
    public string Url { get; init; } = "";
    public string ThumbnailUrl { get; init; } = "";
    public TimeSpan? Duration { get; init; }
    public int Index { get; init; } = 1;
}

public sealed record QualityOption
{
    public string Label { get; init; } = "Best quality";
    public string FormatArg { get; init; } = "bestvideo+bestaudio/best";
    public long? EstimatedBytes { get; init; }
}

public sealed record ResolvedQuery
{
    public List<MediaItem> Items { get; init; } = new();
    public bool IsPlaylist { get; init; }
    public string? PlaylistTitle { get; init; }
    public List<QualityOption> QualityOptions { get; init; } = new();
}

public static class MediaResolver
{
    public static readonly (string Label, int Height)[] Tiers =
    {
        ("Best quality", 0),
        ("2160p (4K)", 2160),
        ("1440p (2K)", 1440),
        ("1080p (Full HD)", 1080),
        ("720p (HD)", 720),
        ("480p (SD)", 480),
        ("360p", 360),
        ("Audio Only (MP3/M4A)", -1)
    };

    public static bool IsYoutubeUrl(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var t = text.Trim();
        return t.Contains("youtube.com/", StringComparison.OrdinalIgnoreCase)
            || t.Contains("youtu.be/", StringComparison.OrdinalIgnoreCase)
            || t.Contains("youtube-nocookie.com/", StringComparison.OrdinalIgnoreCase);
    }

    public static async Task<ResolvedQuery> ResolveAsync(string url, CancellationToken ct)
    {
        var json = await YtDlpRunner.RunJsonAsync(url, ct);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (root.TryGetProperty("entries", out var entries) && entries.ValueKind == JsonValueKind.Array)
        {
            var items = new List<MediaItem>();
            var playlistTitle = root.TryGetProperty("playlist_title", out var pt) ? pt.GetString() : null;
            if (string.IsNullOrWhiteSpace(playlistTitle) && root.TryGetProperty("title", out var t))
                playlistTitle = t.GetString();
            playlistTitle ??= "Playlist";

            var index = 0;
            foreach (var entry in entries.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object)
                    continue;
                if (!entry.TryGetProperty("id", out var idEl) || string.IsNullOrWhiteSpace(idEl.GetString()))
                    continue;

                var id = idEl.GetString()!;
                index++;

                items.Add(new MediaItem
                {
                    Id = id,
                    Title = entry.TryGetProperty("title", out var entryTitle) ? entryTitle.GetString() ?? id : id,
                    Channel = entry.TryGetProperty("channel", out var entryChannel) ? entryChannel.GetString() ?? "" : "",
                    Url = entry.TryGetProperty("url", out var entryUrl) && !string.IsNullOrWhiteSpace(entryUrl.GetString())
                        ? entryUrl.GetString()!
                        : $"https://www.youtube.com/watch?v={id}",
                    ThumbnailUrl = $"https://i.ytimg.com/vi/{id}/hqdefault.jpg",
                    Duration = entry.TryGetProperty("duration", out var entryDuration) && entryDuration.ValueKind == JsonValueKind.Number
                        ? TimeSpan.FromSeconds(entryDuration.GetDouble())
                        : null,
                    Index = index
                });
            }

            if (items.Count == 0)
                throw new YtDlpException("No videos found in this link. Try a direct video link.");

            return new ResolvedQuery
            {
                Items = items,
                IsPlaylist = true,
                PlaylistTitle = playlistTitle
            };
        }

        // Single video
        var vid = root.TryGetProperty("id", out var vidEl) ? vidEl.GetString() ?? "" : "";
        if (string.IsNullOrWhiteSpace(vid))
            throw new YtDlpException("Could not identify this link as a YouTube video.");

        var video = new MediaItem
        {
            Id = vid,
            Title = root.TryGetProperty("title", out var videoTitle) ? videoTitle.GetString() ?? vid : vid,
            Channel = root.TryGetProperty("channel", out var videoChannel)
                ? videoChannel.GetString() ?? ""
                : root.TryGetProperty("uploader", out var videoUploader) ? videoUploader.GetString() ?? "" : "",
            Url = $"https://www.youtube.com/watch?v={vid}",
            ThumbnailUrl = $"https://i.ytimg.com/vi/{vid}/hqdefault.jpg",
            Duration = root.TryGetProperty("duration", out var videoDuration) && videoDuration.ValueKind == JsonValueKind.Number
                ? TimeSpan.FromSeconds(videoDuration.GetDouble())
                : null,
            Index = 1
        };

        return new ResolvedQuery
        {
            Items = new List<MediaItem> { video },
            IsPlaylist = false,
            QualityOptions = BuildQualityOptions(root)
        };
    }

    private static List<QualityOption> BuildQualityOptions(JsonElement root)
    {
        long? bestAudioSize = null;
        long? bestVideoSize = null;
        var videoSizes = new Dictionary<int, long>();

        if (root.TryGetProperty("formats", out var formats) && formats.ValueKind == JsonValueKind.Array)
        {
            foreach (var f in formats.EnumerateArray())
            {
                if (f.ValueKind != JsonValueKind.Object)
                    continue;

                var vcodec = f.TryGetProperty("vcodec", out var vc) ? vc.GetString() : "none";
                var size = GetSize(f);
                if (size is null) continue;

                var isAudio = string.IsNullOrEmpty(vcodec) || vcodec == "none";
                if (isAudio)
                {
                    if (bestAudioSize is null || size > bestAudioSize)
                        bestAudioSize = size;
                }
                else
                {
                    if (bestVideoSize is null || size > bestVideoSize)
                        bestVideoSize = size;

                    if (f.TryGetProperty("height", out var h) && h.ValueKind == JsonValueKind.Number)
                    {
                        var height = h.GetInt32();
                        if (!videoSizes.TryGetValue(height, out var cur) || size > cur)
                            videoSizes[height] = size.Value;
                    }
                }
            }
        }

        var options = new List<QualityOption>();
        foreach (var (label, height) in Tiers)
        {
            long? total = null;
            if (height == -1)
            {
                total = bestAudioSize;
            }
            else if (bestAudioSize is not null)
            {
                if (height == 0)
                {
                    if (bestVideoSize is not null)
                        total = bestVideoSize + bestAudioSize;
                }
                else
                {
                    var best = videoSizes.Where(kv => kv.Key <= height).Select(kv => (long?)kv.Value).DefaultIfEmpty(null).Max();
                    if (best is not null)
                        total = best + bestAudioSize;
                }
            }

            options.Add(new QualityOption
            {
                Label = label,
                FormatArg = height == -1
                    ? "bestaudio/best"
                    : height == 0
                        ? "bestvideo+bestaudio/best"
                        : $"bestvideo[height<={height}]+bestaudio/best[height<={height}]/best",
                EstimatedBytes = total
            });
        }

        return options;
    }

    private static long? GetSize(JsonElement f)
    {
        if (f.TryGetProperty("filesize", out var fs) && fs.ValueKind == JsonValueKind.Number && fs.GetInt64() > 0)
            return fs.GetInt64();
        if (f.TryGetProperty("filesize_approx", out var fa) && fa.ValueKind == JsonValueKind.Number && fa.GetInt64() > 0)
            return fa.GetInt64();
        return null;
    }
}
