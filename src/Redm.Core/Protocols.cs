namespace Redm.Core;

public static class Protocols
{
    public static readonly string[] HttpResumeHeaders = { "Range", "If-Range", "Accept-Ranges", "Content-Range", "ETag", "Last-Modified" };
    public static readonly string[] FtpCommands = { "USER", "PASS", "PASV", "EPSV", "TYPE I", "REST", "RETR", "CWD", "LIST", "FEAT", "QUIT" };
    public static readonly string[] HlsSchemes = { "hls2toredm://", "hlstoredm://", "redmhlstomp4://", "hls2toredmtomp4://", "redmhlstomp4v2://" };
    public static bool IsResumable(int status, string acceptRanges)
        => status == 206 || acceptRanges.Equals("bytes", System.StringComparison.OrdinalIgnoreCase);
}
