namespace Redm.Core;

public record SiteLogin(string Host, string User, string Pass);
public class SiteLogins
{
    private readonly List<SiteLogin> _items = new();
    public IReadOnlyList<SiteLogin> Items => _items;
    public void Add(string host, string user, string pass) => _items.Add(new(host, user, pass));
    public SiteLogin? Find(string url)
        => _items.FirstOrDefault(s => url.Contains(s.Host, StringComparison.OrdinalIgnoreCase));
}

public record Category(string Name, string Folder, string[] Extensions);
public static class Categories
{
    public static List<Category> Default(string baseDir) => new()
    {
        new("Programs", System.IO.Path.Combine(baseDir, "Programs"), new[]{"exe","msi"}),
        new("Video", System.IO.Path.Combine(baseDir, "Video"), new[]{"mp4","avi","mkv","mov","flv"}),
        new("Music", System.IO.Path.Combine(baseDir, "Music"), new[]{"mp3","wav","ogg"}),
        new("Documents", System.IO.Path.Combine(baseDir, "Documents"), new[]{"pdf","docx","txt"}),
        new("Compressed", System.IO.Path.Combine(baseDir, "Compressed"), new[]{"zip","rar","7z"}),
    };
}

public static class Filters
{
    public static IEnumerable<string> Apply(IEnumerable<string> urls, string mustContain = "", long minSize = 0, Func<string,long>? sizeOf = null)
    {
        foreach (var u in urls)
        {
            if (!string.IsNullOrEmpty(mustContain) && !u.Contains(mustContain, StringComparison.OrdinalIgnoreCase)) continue;
            if (minSize > 0 && (sizeOf?.Invoke(u) ?? long.MaxValue) < minSize) continue;
            yield return u;
        }
    }
    public static bool IsDuplicate(IEnumerable<string> existing, string url)
        => existing.Any(e => e.Equals(url, StringComparison.OrdinalIgnoreCase));
}
