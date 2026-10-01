using WDM.Models;
using WDM.Services;
using WDM.ViewModels;
using TaskStatus = WDM.Models.TaskStatus;

namespace WDM.Tests;

/// <summary>Round 8 — move/prune automation (C2), shell notify + orphan cleanup (C3).</summary>
public sealed class Round8Tests
{
    private static MainViewModel NewVm()
    {
        var vm = new MainViewModel();
        vm.SuppressPersistence();
        vm.Tasks.Clear();
        return vm;
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
