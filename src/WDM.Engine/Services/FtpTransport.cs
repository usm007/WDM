using System.Net.Sockets;
using System.Text;

namespace WDM.Services;

/// <summary>Minimal FTP client (PASV only, no FTPS): enough to download files
/// with resume. No new dependencies — raw control/data sockets with strict
/// reply parsing and caps. Active mode and encrypted sessions are out of
/// scope (documented to the user as such).</summary>
public sealed class FtpTransport : IDisposable
{
    private const int MaxLineChars = 16 * 1024;
    private const int MaxReplyLines = 64;
    private readonly TcpClient _control = new();
    private StreamReader? _reader;
    private StreamWriter? _writer;
    private bool _disposed;

    public string Host { get; }
    public int Port { get; }
    public string Username { get; }
    public string Password { get; }

    public FtpTransport(string host, int port, string username, string password)
    {
        Host = host;
        Port = port;
        Username = username;
        Password = password;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        try { _writer?.WriteLine("QUIT"); } catch { }
        try { _reader?.Dispose(); } catch { }
        try { _writer?.Dispose(); } catch { }
        try { _control.Close(); } catch { }
    }

    public static bool TryParseUrl(string url, out string host, out int port, out string user, out string pass, out string path)
    {
        host = "";
        port = 21;
        user = "anonymous";
        pass = "wdm@local";
        path = "";
        try
        {
            if (!Uri.TryCreate((url ?? "").Trim(), UriKind.Absolute, out var u) ||
                u.Scheme != Uri.UriSchemeFtp || string.IsNullOrWhiteSpace(u.Host))
                return false;
            host = u.Host;
            port = u.IsDefaultPort ? 21 : u.Port;
            if (port < 1 || port > 65535)
                return false;
            string[] userInfo = u.UserInfo.Split(':', 2);
            if (userInfo.Length > 0 && userInfo[0].Length > 0)
            {
                user = Uri.UnescapeDataString(userInfo[0]);
                if (userInfo.Length > 1)
                    pass = Uri.UnescapeDataString(userInfo[1]);
                else if (!string.Equals(user, "anonymous", StringComparison.OrdinalIgnoreCase) &&
                         !string.Equals(user, "ftp", StringComparison.OrdinalIgnoreCase))
                    pass = "";
            }
            if (user.Length == 0 || user.Length > 128 || pass.Length > 1024)
                return false;
            path = Uri.UnescapeDataString(u.AbsolutePath);
            if (string.IsNullOrWhiteSpace(path) || path.EndsWith('/'))
                return false;
            return true;
        }
        catch { return false; }
    }

    private async Task<string> ReadReplyAsync(CancellationToken ct)
    {
        var sb = new StringBuilder();
        string? code = null;
        for (int i = 0; i < MaxReplyLines; i++)
        {
            string? line = await _reader!.ReadLineAsync(ct).ConfigureAwait(false);
            if (line is null)
                throw new IOException("FTP control connection closed.");
            if (line.Length > MaxLineChars)
                throw new IOException("FTP reply line too long.");
            sb.AppendLine(line);
            if (line.Length >= 4 && char.IsDigit(line[0]) && char.IsDigit(line[1]) && char.IsDigit(line[2]))
            {
                if (code is null)
                    code = line[..3];
                if (line[3] == ' ' && line.StartsWith(code, StringComparison.Ordinal))
                    return sb.ToString();
            }
        }
        throw new IOException("FTP multiline reply too long.");
    }

    private static int ReplyCode(string reply) =>
        reply.Length >= 3 && int.TryParse(reply.AsSpan(0, 3), out int c) ? c : 0;

    private async Task<string> CommandAsync(string command, CancellationToken ct)
    {
        await _writer!.WriteLineAsync(command.AsMemory(), ct).ConfigureAwait(false);
        await _writer.FlushAsync(ct).ConfigureAwait(false);
        return await ReadReplyAsync(ct).ConfigureAwait(false);
    }

    public async Task ConnectAsync(CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        var tct = timeout.Token;
        await _control.ConnectAsync(Host, Port, tct).ConfigureAwait(false);
        _control.ReceiveTimeout = 30000;
        _control.SendTimeout = 30000;
        var stream = _control.GetStream();
        _reader = new StreamReader(stream, Encoding.ASCII, false, 1024, leaveOpen: true);
        _writer = new StreamWriter(stream, Encoding.ASCII, 1024, leaveOpen: true) { AutoFlush = true, NewLine = "\r\n" };
        string greet = await ReadReplyAsync(tct).ConfigureAwait(false);
        if (ReplyCode(greet) != 220)
            throw new IOException("FTP server refused connection.");
        string userResp = await CommandAsync($"USER {Username}", tct).ConfigureAwait(false);
        int userCode = ReplyCode(userResp);
        if (userCode == 230)
            return;
        if (userCode != 331)
            throw new UnauthorizedAccessException("FTP login rejected (username).");
        string passResp = await CommandAsync($"PASS {Password}", tct).ConfigureAwait(false);
        if (ReplyCode(passResp) != 230)
            throw new UnauthorizedAccessException("FTP login rejected (password).");
        string typeResp = await CommandAsync("TYPE I", tct).ConfigureAwait(false);
        if (ReplyCode(typeResp) != 200)
            throw new IOException("FTP server refused binary mode.");
    }

    public async Task<long> GetSizeAsync(string path, CancellationToken ct)
    {
        string resp = await CommandAsync($"SIZE {path}", ct).ConfigureAwait(false);
        if (ReplyCode(resp) != 213)
            return -1;
        string[] parts = resp.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 2 && long.TryParse(parts[1], out long size) && size >= 0)
            return size;
        return -1;
    }

    private async Task<TcpClient> OpenPassiveAsync(CancellationToken ct)
    {
        string resp = await CommandAsync("PASV", ct).ConfigureAwait(false);
        if (ReplyCode(resp) != 227)
            throw new IOException("FTP server refused passive mode (active mode unsupported).");
        int open = resp.IndexOf('(');
        int close = resp.IndexOf(')', open + 1);
        if (open < 0 || close < 0)
            throw new IOException("Unparseable PASV reply.");
        string[] nums = resp.Substring(open + 1, close - open - 1).Split(',');
        if (nums.Length != 6)
            throw new IOException("Unparseable PASV reply.");
        int[] p = new int[6];
        for (int i = 0; i < 6; i++)
        {
            if (!int.TryParse(nums[i].Trim(), out p[i]) || p[i] < 0 || p[i] > 255)
                throw new IOException("Unparseable PASV reply.");
        }
        string ip = $"{p[0]}.{p[1]}.{p[2]}.{p[3]}";
        int port = p[4] * 256 + p[5];
        var data = new TcpClient();
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            await data.ConnectAsync(ip, port, timeout.Token).ConfigureAwait(false);
        }
        catch
        {
            try { data.Close(); } catch { }
            throw;
        }
        return data;
    }

    /// <summary>Downloads a byte range into <paramref name="output"/> (offset =
    /// resume point; pass 0 from scratch). Size discovery is separate
    /// (<see cref="GetSizeAsync"/>).</summary>
    public async Task DownloadRangeAsync(string path, long offset, Stream output, Func<int, CancellationToken, Task>? onBytes, CancellationToken ct)
    {
        using var data = await OpenPassiveAsync(ct).ConfigureAwait(false);
        if (offset > 0)
        {
            string rest = await CommandAsync($"REST {offset}", ct).ConfigureAwait(false);
            if (ReplyCode(rest) != 350)
                throw new IOException("FTP server refused resume offset.");
        }
        string retr = await CommandAsync($"RETR {path}", ct).ConfigureAwait(false);
        int code = ReplyCode(retr);
        if (code != 150 && code != 125)
            throw new IOException($"FTP download rejected ({code}).");
        using var stream = data.GetStream();
        var buffer = new byte[256 * 1024];
        int read;
        while ((read = await stream.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false)) > 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            if (onBytes is not null)
                await onBytes(read, ct).ConfigureAwait(false);
        }
        data.Close();
        string done = await ReadReplyAsync(ct).ConfigureAwait(false);
        int doneCode = ReplyCode(done);
        if (doneCode != 226 && doneCode != 250)
            throw new IOException($"FTP transfer did not complete ({doneCode}).");
    }
}
