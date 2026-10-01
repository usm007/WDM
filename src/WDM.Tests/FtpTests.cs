// FtpTests — PASV download engine: URL parsing, byte-exact transfer,
// REST resume, auth rejection. A minimal in-process fake FTP server
// (control + passive data connections, no external dependencies).
using System.Net;
using System.Net.Sockets;
using System.Text;
using WDM.Models;
using WDM.Services;
using WDM.Tests.TestInfrastructure;
using TaskStatus = WDM.Models.TaskStatus;

namespace WDM.Tests;

public sealed class FtpTests : IDisposable
{
    private readonly DownloadEngine _engine = new();
    private readonly List<string> _dirs = new();
    private readonly List<IDisposable> _servers = new();

    public void Dispose()
    {
        try { _engine.PauseAll(); } catch { }
        foreach (var s in _servers)
            try { s.Dispose(); } catch { }
        foreach (var d in _dirs)
            try { Directory.Delete(d, recursive: true); } catch { }
    }

    private string NewDir(string prefix)
    {
        string d = Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        _dirs.Add(d);
        return d;
    }

    private sealed class FakeFtpServer : IDisposable
    {
        private readonly TcpListener _control;
        private readonly byte[] _file;
        private readonly string _user;
        private readonly string _pass;
        private readonly CancellationTokenSource _cts = new();
        private Task? _loop;
        public int Port => ((IPEndPoint)_control.LocalEndpoint).Port;
        public List<string> Log = new();

        public FakeFtpServer(byte[] file, string user = "anonymous", string pass = "wdm@local")
        {
            _file = file;
            _user = user;
            _pass = pass;
            _control = new TcpListener(IPAddress.Loopback, 0);
            _control.Start();
            _loop = Task.Run(AcceptLoopAsync);
        }

        public void Dispose()
        {
            try { _cts.Cancel(); } catch { }
            try { _control.Stop(); } catch { }
        }

        private async Task AcceptLoopAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await _control.AcceptTcpClientAsync(_cts.Token); }
                catch { return; }
                _ = Task.Run(() => ServeAsync(client));
            }
        }

        private async Task ServeAsync(TcpClient client)
        {
            using (client)
            using (var stream = client.GetStream())
            using (var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, leaveOpen: true))
            using (var writer = new StreamWriter(stream, Encoding.ASCII, 1024, leaveOpen: true) { AutoFlush = true, NewLine = "\r\n" })
            {
                TcpListener? dataListener = null;
                long restOffset = 0;
                try
                {
                    await writer.WriteLineAsync("220 FakeFTP ready");
                    while (!_cts.IsCancellationRequested)
                    {
                        string? line = await reader.ReadLineAsync(_cts.Token);
                        if (line is null)
                            return;
                        lock (Log) Log.Add(line);
                        string cmd = line.Length >= 4 ? line[..4].ToUpperInvariant() : line.ToUpperInvariant();
                        string arg = line.Length > 5 ? line[5..].Trim() : "";
                        switch (cmd)
                        {
                            case "USER":
                                await writer.WriteLineAsync(arg == _user ? "331 Password required" : "530 Denied");
                                break;
                            case "PASS":
                                await writer.WriteLineAsync(arg == _pass ? "230 Logged in" : "530 Denied");
                                break;
                            case "TYPE":
                                await writer.WriteLineAsync("200 Binary mode");
                                break;
                            case "SIZE":
                                await writer.WriteLineAsync($"213 {_file.Length}");
                                break;
                            case "PASV":
                                try { dataListener?.Stop(); } catch { }
                                dataListener = new TcpListener(IPAddress.Loopback, 0);
                                dataListener.Start();
                                int port = ((IPEndPoint)dataListener.LocalEndpoint).Port;
                                await writer.WriteLineAsync($"227 Entering Passive Mode (127,0,0,1,{port / 256},{port % 256})");
                                break;
                            case "REST":
                                if (long.TryParse(arg, out long off) && off >= 0 && off <= _file.Length)
                                {
                                    restOffset = off;
                                    await writer.WriteLineAsync($"350 Restarting at {off}");
                                }
                                else
                                    await writer.WriteLineAsync("550 Bad offset");
                                break;
                            case "RETR":
                                if (dataListener is null)
                                {
                                    await writer.WriteLineAsync("425 No data connection");
                                    break;
                                }
                                await writer.WriteLineAsync("150 Sending data");
                                try
                                {
                                    using var data = await dataListener.AcceptTcpClientAsync(_cts.Token);
                                    await data.GetStream().WriteAsync(_file.AsMemory((int)restOffset), _cts.Token);
                                }
                                catch { }
                                try { dataListener.Stop(); } catch { }
                                dataListener = null;
                                restOffset = 0;
                                await writer.WriteLineAsync("226 Done");
                                break;
                            case "QUIT":
                                await writer.WriteLineAsync("221 Bye");
                                return;
                            default:
                                await writer.WriteLineAsync("502 Unknown");
                                break;
                        }
                    }
                }
                catch { }
                finally { try { dataListener?.Stop(); } catch { } }
            }
        }
    }

    private static async Task WaitForIdle(DownloadTask task, int timeoutMs = 30000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (task.Status is TaskStatus.Completed or TaskStatus.Failed or TaskStatus.Paused)
                return;
            await Task.Delay(50);
        }
    }

    [Trait("Category", Cats.Unit)][Fact]
    public void TryParseUrl_Matrix()
    {
        Assert.True(FtpTransport.TryParseUrl("ftp://files.example.com/pub/a.zip", out var h, out var p, out var u, out var pw, out var path));
        Assert.Equal("files.example.com", h);
        Assert.Equal(21, p);
        Assert.Equal("anonymous", u);
        Assert.Equal("a.zip", Path.GetFileName(path));
        Assert.True(FtpTransport.TryParseUrl("ftp://bob:s3cret@h.test:2121/f.bin", out h, out p, out u, out pw, out path));
        Assert.Equal("bob", u);
        Assert.Equal("s3cret", pw);
        Assert.Equal(2121, p);
        Assert.False(FtpTransport.TryParseUrl("http://h.test/f", out _, out _, out _, out _, out _));
        Assert.False(FtpTransport.TryParseUrl("ftp://h.test/dir/", out _, out _, out _, out _, out _));
        Assert.False(FtpTransport.TryParseUrl("not a url", out _, out _, out _, out _, out _));
    }

    [Trait("Category", Cats.E2E)][Fact]
    public async Task Ftp_Download_ByteExact()
    {
        byte[] content = TestFiles.Make(300 * 1024, seed: 21);
        using var server = new FakeFtpServer(content);
        _servers.Add(server);
        string dir = NewDir("wdm-ftp-");
        var task = new DownloadTask
        {
            Url = $"ftp://127.0.0.1:{server.Port}/files/release.bin",
            FileName = "release.bin",
            SaveFolder = dir,
        };
        _engine.Start(task);
        await WaitForIdle(task);
        Assert.Equal(TaskStatus.Completed, task.Status);
        Assert.Equal(content, await File.ReadAllBytesAsync(Path.Combine(dir, "release.bin")));
        Assert.Equal(content.Length, task.TotalBytes);
    }

    [Trait("Category", Cats.E2E)][Fact]
    public async Task Ftp_Resume_Appends()
    {
        byte[] content = TestFiles.Make(256 * 1024, seed: 22);
        using var server = new FakeFtpServer(content);
        _servers.Add(server);
        string dir = NewDir("wdm-ftp-");
        byte[] half = new byte[content.Length / 2];
        Array.Copy(content, half, half.Length);
        await File.WriteAllBytesAsync(Path.Combine(dir, "r.bin"), half);
        var task = new DownloadTask
        {
            Url = $"ftp://127.0.0.1:{server.Port}/r.bin",
            FileName = "r.bin",
            SaveFolder = dir,
        };
        _engine.Start(task);
        await WaitForIdle(task);
        Assert.Equal(TaskStatus.Completed, task.Status);
        Assert.Equal(content, await File.ReadAllBytesAsync(Path.Combine(dir, "r.bin")));
        Assert.Contains(server.Log, l => l.StartsWith("REST ", StringComparison.Ordinal));
    }

    [Trait("Category", Cats.E2E)][Fact]
    public async Task Ftp_BadLogin_Fails_Cleanly()
    {
        byte[] content = TestFiles.Make(1024, seed: 23);
        using var server = new FakeFtpServer(content, user: "bob", pass: "s3cret");
        _servers.Add(server);
        string dir = NewDir("wdm-ftp-");
        var task = new DownloadTask
        {
            Url = $"ftp://127.0.0.1:{server.Port}/f.bin",
            FileName = "f.bin",
            SaveFolder = dir,
        };
        _engine.Start(task);
        await WaitForIdle(task);
        Assert.Equal(TaskStatus.Failed, task.Status);
    }
}
