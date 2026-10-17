namespace Redm.Core;

public static class Updater
{
    public static string CheckUrl(string host, string version) => $"https://www{host}/update.cgi?v={version}";
}
