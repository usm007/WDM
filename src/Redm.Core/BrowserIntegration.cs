namespace Redm.Core;

public static class BrowserIntegration
{
    public const string HostName = "com.redm.host";
    public static readonly string[] ChromeIds = { "ngpampappnmepgilojfohadhhmbhlaek", "llbjbkhnmlidjebalopleeepgdfgcpec" };
    public static readonly string[] FirefoxIds = { "redm@redm.app" };
    public static string ChromeHostManifest() => """
        {"name":"com.redm.host","description":"ReDM extension helper","type":"stdio",
         "allowed_origins":["chrome-extension://ngpampappnmepgilojfohadhhmbhlaek/"],
         "path":"Redm.MsgHost.exe"}
        """;
    public static string FirefoxHostManifest() => """
        {"name":"com.redm.host","description":"ReDM extension helper","type":"stdio",
         "allowed_extensions":["redm@redm.app"],
         "path":"Redm.MsgHost.exe"}
        """;
}
