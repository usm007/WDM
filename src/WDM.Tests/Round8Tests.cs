using System.Net;
using System.Net.Sockets;
using System.Text;
using WDM.Models;
using WDM.Services;
using WDM.ViewModels;
using TaskStatus = WDM.Models.TaskStatus;

namespace WDM.Tests;

/// <summary>Round 8 — P5: scheduler window policy + tick/pausing (A5),
/// move/prune automation (C2), shell notify + orphan cleanup (C3).</summary>
public sealed class Round8Tests
{
    private static readonly List<DayOfWeek> AllDays = new()
    {
        DayOfWeek.Sunday, DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday,
        DayOfWeek.Thursday, DayOfWeek.Friday, DayOfWeek.Saturday,
    };

    // ---------- A5: window policy ----------

    [Theory]
    // Normal window 09:00-17:00, Wednesday.
    [InlineData(2026, 9, 16, 9, 0, 9, 0, 17, 0, true)]   // inclusive start
    [InlineData(2026, 9, 16, 12, 0, 9, 0, 17, 0, true)]
    [InlineData(2026, 9, 16, 16, 59, 9, 0, 17, 0, true)]
    [InlineData(2026, 9, 16, 17, 0, 9, 0, 17, 0, false)]  // exclusive stop
    [InlineData(2026, 9, 16, 8, 59, 9, 0, 17, 0, false)]
    // Overnight 22:00-07:00.
    [InlineData(2026, 9, 16, 23, 30, 22, 0, 7, 0, true)]
    [InlineData(2026, 9, 17, 6, 59, 22, 0, 7, 0, true)]
    [InlineData(2026, 9, 17, 7, 0, 22, 0, 7, 0, false)]
    [InlineData(2026, 9, 16, 12, 0, 22, 0, 7, 0, false)]
    [InlineData(2026, 9, 16, 22, 0, 22, 0, 7, 0, true)]
    // All-day (equal times).
    [InlineData(2026, 9, 16, 3, 0, 0, 0, 0, 0, true)]
    public void IsInWindow_Matrix(int y, int mo, int d, int h, int mi,
        int sh, int sm, int eh, int em, bool expected)
    {
        var now = new DateTime(y, mo, d, h, mi, 0);
        Assert.Equal(expected, SchedulerPolicy.IsInWindow(
            now, new TimeSpan(sh, sm, 0), new TimeSpan(eh, em, 0), AllDays));
    }

    [Fact]
    public void IsInWindow_RespectsDays()
    {
        var wedNoon = new DateTime(2026, 9, 16, 12, 0, 0); // Wednesday
        Assert.True(SchedulerPolicy.IsInWindow(wedNoon, new TimeSpan(9, 0, 0), new TimeSpan(17, 0, 0), AllDays));
        Assert.False(SchedulerPolicy.IsInWindow(wedNoon, new TimeSpan(9, 0, 0), new TimeSpan(17, 0, 0),
            new List<DayOfWeek> { DayOfWeek.Monday }));
        Assert.False(SchedulerPolicy.IsInWindow(wedNoon, new TimeSpan(9, 0, 0), new TimeSpan(17, 0, 0),
            new List<DayOfWeek>()));
    }

    [Fact]
    public void Engine_SchedulerCapCombinesWithBase()
    {
        var engine = new DownloadEngine { MaxConcurrent = 1 };
        var m = typeof(DownloadEngine).GetMethod("EffectiveLimitKbps",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        long Get() => (long)m.Invoke(engine, null)!;

        engine.GlobalSpeedLimitKbps = 0;
        engine.SchedulerSpeedLimitKbps = 0;
        Assert.Equal(0, Get());
        engine.SchedulerSpeedLimitKbps = 100;
        Assert.Equal(100, Get());
        engine.GlobalSpeedLimitKbps = 200;
        Assert.Equal(100, Get()); // tighter wins
        engine.SchedulerSpeedLimitKbps = 500;
        Assert.Equal(200, Get());
        engine.GlobalSpeedLimitKbps = 0;
        Assert.Equal(500, Get());
    }

    // ---------- A5: tick holds + resumes ----------

    private sealed class LoopbackServer : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly ManualResetEventSlim _gate;
        public string BaseUrl { get; }

        public LoopbackServer(ManualResetEventSlim gate)
        {
            _gate = gate;
            int port;
            using (var tcp = new TcpListener(IPAddress.Loopback, 0))
            {
                tcp.Start();
                port = ((IPEndPoint)tcp.LocalEndpoint).Port;
            }
            BaseUrl = $"http://127.0.0.1:{port}/";
            _listener.Prefixes.Add(BaseUrl);
            _listener.Start();
            _ = Task.Run(AcceptLoop);
        }

        private async Task AcceptLoop()
        {
            while (_listener.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = await _listener.GetContextAsync(); }
                catch { return; }
                _ = Task.Run(() => Serve(ctx));
            }
        }

        private void Serve(HttpListenerContext ctx)
        {
            try
            {
                if (ctx.Request.HttpMethod == "HEAD")
                    _gate.Wait(TimeSpan.FromSeconds(25));
                byte[] body = Encoding.UTF8.GetBytes("0123456789");
                ctx.Response.StatusCode = 200;
                ctx.Response.ContentType = "application/octet-stream";
                ctx.Response.ContentLength64 = body.Length;
                if (ctx.Request.HttpMethod != "HEAD")
                    ctx.Response.OutputStream.Write(body, 0, body.Length);
                ctx.Response.OutputStream.Close();
            }
            catch
            {
                try { ctx.Response.Abort(); } catch { }
            }
        }

        public void Dispose()
        {
            try { _listener.Stop(); } catch { }
            try { _listener.Close(); } catch { }
        }
    }

    private static MainViewModel NewVm()
    {
        var vm = new MainViewModel();
        vm.SuppressPersistence();
        vm.Tasks.Clear();
        return vm;
    }

    [Fact]
    public void SchedulerTick_HoldsOutsideResumesInside()
    {
        using var gate = new ManualResetEventSlim(false);
        using var server = new LoopbackServer(gate);
        using var vm = NewVm();
        string dir = Path.Combine(Path.GetTempPath(), "wdm_r8_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            vm.Engine.MaxConcurrent = 1;
            var t1 = new DownloadTask { Url = server.BaseUrl + "a", FileName = "a.bin", SaveFolder = dir };
            var t2 = new DownloadTask { Url = server.BaseUrl + "b", FileName = "b.bin", SaveFolder = dir };
            vm.Tasks.Add(t1);
            vm.Tasks.Add(t2);
            vm.Engine.Start(t1);
            vm.Engine.Start(t2);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while ((vm.Engine.ActiveCount != 1 || vm.Engine.QueuedCount != 1) && sw.Elapsed < TimeSpan.FromSeconds(15))
                Thread.Sleep(50);
            Assert.Equal(1, vm.Engine.ActiveCount);

            // Outside window: both held and marked (user intent still recorded).
            vm.Settings.SchedulerEnabled = true;
            var now = DateTime.Now;
            vm.Settings.SchedulerStart = now.AddHours(1).TimeOfDay;
            vm.Settings.SchedulerStop = now.AddHours(2).TimeOfDay;
            vm.Settings.SchedulerDays = AllDays;
            vm.OnSchedulerTick(now);
            Assert.Equal(TaskStatus.Paused, t1.Status);
            Assert.Equal(TaskStatus.Paused, t2.Status);
            Assert.True(t1.SchedulerPaused);
            Assert.True(t2.SchedulerPaused);
            // Scheduler cap is zeroed outside the window.
            Assert.Equal(0, vm.Engine.SchedulerSpeedLimitKbps);

            // Inside window (all-day): both resume, marks cleared, cap applied.
            vm.Settings.SchedulerStart = TimeSpan.Zero;
            vm.Settings.SchedulerStop = TimeSpan.Zero;
            vm.Settings.SchedulerSpeedLimitKbps = 1234;
            vm.OnSchedulerTick(now);
            Assert.False(t1.SchedulerPaused);
            Assert.False(t2.SchedulerPaused);
            Assert.Equal(1234, vm.Engine.SchedulerSpeedLimitKbps);
        }
        finally
        {
            gate.Set();
            try { vm.Engine.PauseAll(); } catch { }
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void SchedulerTick_DisabledLeavesUserPausedAlone()
    {
        using var vm = NewVm();
        var t = new DownloadTask { Url = "https://s.example.com/a", FileName = "a.bin", SaveFolder = Path.GetTempPath(), Status = TaskStatus.Paused };
        vm.Tasks.Add(t);
        vm.Settings.SchedulerEnabled = false;
        vm.OnSchedulerTick(DateTime.Now);
        Assert.Equal(TaskStatus.Paused, t.Status);
        Assert.False(t.SchedulerPaused);
    }

    // ---------- C2: prune + move ----------

    [Fact]
    public void PruneFinishedLinks_RemovesOnlyStaleCompletedRows()
    {
        using var vm = NewVm();
        var now = new DateTime(2026, 9, 16, 12, 0, 0);
        string dir = Path.Combine(Path.GetTempPath(), "wdm_r8p_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string keepFile = Path.Combine(dir, "keep.bin");
        File.WriteAllText(keepFile, "x");
        try
        {
            var old = new DownloadTask { Url = "https://s.example.com/old", FileName = "old.bin", SaveFolder = dir, Status = TaskStatus.Completed, CompletedAt = now.AddDays(-10) };
            var recent = new DownloadTask { Url = "https://s.example.com/new", FileName = "keep.bin", SaveFolder = dir, Status = TaskStatus.Completed, CompletedAt = now.AddDays(-1) };
            var failed = new DownloadTask { Url = "https://s.example.com/f", FileName = "f.bin", SaveFolder = dir, Status = TaskStatus.Failed, CompletedAt = now.AddDays(-10) };
            vm.Tasks.Add(old);
            vm.Tasks.Add(recent);
            vm.Tasks.Add(failed);
            vm.Settings.DeleteFinishedLinksAfterDays = 7;
            Assert.Equal(1, vm.PruneFinishedLinks(now));
            Assert.DoesNotContain(old, vm.Tasks);
            Assert.Contains(recent, vm.Tasks);
            Assert.Contains(failed, vm.Tasks);
            Assert.True(File.Exists(keepFile)); // link pruned, file stays

            vm.Settings.DeleteFinishedLinksAfterDays = 0;
            Assert.Equal(0, vm.PruneFinishedLinks(now));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void TryMoveFinishedFile_MovesNumbersAndUpdatesTask()
    {
        string src = Path.Combine(Path.GetTempPath(), "wdm_r8m_" + Guid.NewGuid().ToString("N"));
        string dst = Path.Combine(Path.GetTempPath(), "wdm_r8d_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(src);
        Directory.CreateDirectory(dst);
        try
        {
            File.WriteAllText(Path.Combine(src, "film.mp4"), "data");
            File.WriteAllText(Path.Combine(dst, "film.mp4"), "existing");
            var task = new DownloadTask { Url = "https://s.example.com/f", FileName = "film.mp4", SaveFolder = src, Status = TaskStatus.Completed };
            Assert.True(PostDownloadActions.TryMoveFinishedFile(task, dst));
            Assert.Equal(dst, task.SaveFolder);
            Assert.Equal("film (1).mp4", task.FileName);
            Assert.True(File.Exists(Path.Combine(dst, "film (1).mp4")));
            Assert.False(File.Exists(Path.Combine(src, "film.mp4")));

            // Already there: no-op success.
            Assert.True(PostDownloadActions.TryMoveFinishedFile(task, dst));

            // Missing source: false, no crash.
            var ghost = new DownloadTask { Url = "https://s.example.com/g", FileName = "ghost.mp4", SaveFolder = src, Status = TaskStatus.Completed };
            Assert.False(PostDownloadActions.TryMoveFinishedFile(ghost, dst));
        }
        finally
        {
            try { Directory.Delete(src, recursive: true); } catch { }
            try { Directory.Delete(dst, recursive: true); } catch { }
        }
    }

    // ---------- C3: notify + orphans ----------

    [Fact]
    public void NotifyFileCreated_NeverThrows()
    {
        PostDownloadActions.NotifyFileCreated(null);
        PostDownloadActions.NotifyFileCreated("");
        PostDownloadActions.NotifyFileCreated(Path.Combine(Path.GetTempPath(), "nope-" + Guid.NewGuid().ToString("N")));
        string f = Path.GetTempFileName();
        try { PostDownloadActions.NotifyFileCreated(f); }
        finally { try { File.Delete(f); } catch { } }
    }

    [Fact]
    public void CleanupOrphanedState_RemovesOnlyOrphans()
    {
        string dir = Path.Combine(Path.GetTempPath(), "wdm_r8c_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string liveFile = Path.Combine(dir, "live.bin");
            File.WriteAllText(liveFile, "data");
            File.WriteAllText(liveFile + ".wdmstate", "bitmap");
            string orphanFile = Path.Combine(dir, "orphan.bin");
            File.WriteAllText(orphanFile, "stale download whose task is gone");
            File.WriteAllText(orphanFile + ".wdmstate", "stale");
            string userFile = Path.Combine(dir, "notes.txt");
            File.WriteAllText(userFile, "keep me");

            int removed = PostDownloadActions.CleanupOrphanedState(
                new[] { liveFile }, new[] { dir });
            Assert.Equal(1, removed);
            Assert.True(File.Exists(liveFile + ".wdmstate"));
            Assert.False(File.Exists(orphanFile + ".wdmstate"));
            Assert.True(File.Exists(orphanFile)); // the (user?) file itself stays
            Assert.True(File.Exists(userFile));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }
}
