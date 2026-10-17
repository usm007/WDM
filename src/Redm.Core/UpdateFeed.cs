namespace Redm.Core;

/// <summary>Update-feed schema + version comparison. The feed is a plain JSON
/// document the app fetches over HTTPS: {"version":"0.2.0","url":"..."}.</summary>
public record UpdateInfo(string Version, string Url, string Notes);

public static class UpdateFeed
{
    public static UpdateInfo Parse(string json)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var r = doc.RootElement;
        return new(
            r.TryGetProperty("version", out var v) ? v.GetString() ?? "0" : "0",
            r.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "",
            r.TryGetProperty("notes", out var n) ? n.GetString() ?? "" : "");
    }

    public static bool IsNewer(string current, string latest)
    {
        static int[] Parts(string v) => v.Split('.').Select(p => int.TryParse(p, out int n) ? n : 0).ToArray();
        var c = Parts(current);
        var l = Parts(latest);
        for (int i = 0; i < Math.Max(c.Length, l.Length); i++)
        {
            int a = i < c.Length ? c[i] : 0, b = i < l.Length ? l[i] : 0;
            if (b != a) return b > a;
        }
        return false;
    }

    public static async Task<UpdateInfo> FetchAsync(HttpClient http, string feedUrl, CancellationToken ct)
        => Parse(await http.GetStringAsync(feedUrl, ct));
}
