namespace Redm.Core;

public record GeneralSettings(bool LaunchOnStartup = false, bool ClipboardMonitor = true, bool AltBypass = true, string SpecialKey = "Insert", bool ShowStartDialog = true, bool ShowCompleteDialog = true);

public class Settings
{
    public GeneralSettings General { get; set; } = new();
    public string SaveDir { get; set; } = System.IO.Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile), "Downloads");
    public string TempDir { get; set; } = System.IO.Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile), "Downloads", ".redm-tmp");
    public List<string> FileTypes { get; set; } = new("exe zip rar 7z mp4 mp3 pdf avi mkv mov".Split(' '));
    public int MaxConnections { get; set; } = 8;
    public string AvCommand { get; set; } = "";
    public bool PlaySound { get; set; } = true;
    public bool ShowProgress { get; set; } = true;
    public string Language { get; set; } = "en";
    /// <summary>Appearance preference: "system", "light", or "dark".</summary>
    public string Appearance { get; set; } = "system";
    public long MinSizeBytes { get; set; } = 0;
    public long SpeedLimitBps { get; set; } = 0;
    public bool ShutdownOnDone { get; set; } = false;
    public List<string> AddressMasks { get; set; } = new();
    public List<string> PanelMasks { get; set; } = new();
    public string SoundPath { get; set; } = "";
    public List<string> Columns { get; set; } = new() { "Filename", "Size", "Status", "Progress", "Rate", "Eta", "Url" };
    public string UpdateFeedUrl { get; set; } = "";
    public ProxyConfig Proxy { get; set; } = new();
    public static readonly string[] RegistryKeys = { @"SOFTWARE\ReDM", @"Software\DownloadManager\ReDM" };
}
