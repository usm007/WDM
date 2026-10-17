namespace Redm.Core;

public record ProxyConfig(
    bool UseHttp = false, string HttpHost = "", int HttpPort = 8080,
    string SocksType = "none", string SocksHost = "", int SocksPort = 1080,
    string PacUrl = "");

public record QueueItem(string Url, string Dest = "");
public class DlQueue
{
    public string Name { get; init; } = "main";
    public List<QueueItem> Items { get; } = new();
    public bool Running { get; private set; }
    public int RetrySeconds { get; set; } = 30;
    public void Add(string url, string dest = "") => Items.Add(new(url, dest));
    public void Start() => Running = true;
    public void Stop() => Running = false;
}
