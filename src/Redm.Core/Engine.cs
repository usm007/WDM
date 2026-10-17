namespace Redm.Core;

public record Segment(int Index, long Start, long End);
public record DownloadJob(string Url, string Dest, int Connections = 8);

public static class Engine
{
    public static List<Segment> Plan(long? size, int connections)
    {
        if (size is null || size <= 0) return new() { new(0, 0, -1) };
        int n = Math.Clamp(connections, 1, 8);
        long chunk = size.Value / n;
        var list = new List<Segment>();
        for (int i = 0; i < n; i++)
        {
            long s = i * chunk;
            long e = i == n - 1 ? size.Value - 1 : (i + 1) * chunk - 1;
            list.Add(new(i, s, e));
        }
        return list;
    }

    public static string RangeHeader(long start, long end)
        => end < 0 ? $"bytes={start}-" : $"bytes={start}-{end}";
}
