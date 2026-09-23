using System.Text.Json;
using System.Xml;
using WDM.Services;

namespace WDM.Tests;

/// <summary>Round 11 — config robustness (new settings survive JSON round-trips,
/// validation clamps, legacy files load) + XAML well-formedness of every edited
/// dialog (parse-level; bindings resolve silently at runtime by design).</summary>
public sealed class Round11Tests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
    };

    [Fact]
    public void AppSettings_RoundTripsNewFields()
    {
        var s = new AppSettings
        {
            AutoResumeFailed = true,
            NotifyOnAdded = true,
            NotifyOnStarted = true,
            NotifyOnError = false,
            NotificationSound = false,
            DetailedNotifications = true,
            SchedulerEnabled = true,
            SchedulerStart = new TimeSpan(21, 30, 0),
            SchedulerStop = new TimeSpan(6, 0, 0),
            SchedulerSpeedLimitKbps = 512,
            SchedulerDays = new List<DayOfWeek> { DayOfWeek.Saturday, DayOfWeek.Sunday },
            MoveOnFinish = true,
            MoveOnFinishFolder = "C:\\Done",
            RemoveLinkAfterFinish = true,
            DeleteFinishedLinksAfterDays = 30,
        };
        string json = JsonSerializer.Serialize(s, JsonOptions);
        var back = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions)!;
        Assert.True(back.AutoResumeFailed);
        Assert.True(back.NotifyOnAdded);
        Assert.True(back.NotifyOnStarted);
        Assert.False(back.NotifyOnError);
        Assert.False(back.NotificationSound);
        Assert.True(back.DetailedNotifications);
        Assert.True(back.SchedulerEnabled);
        Assert.Equal(new TimeSpan(21, 30, 0), back.SchedulerStart);
        Assert.Equal(new TimeSpan(6, 0, 0), back.SchedulerStop);
        Assert.Equal(512, back.SchedulerSpeedLimitKbps);
        Assert.Equal(new List<DayOfWeek> { DayOfWeek.Saturday, DayOfWeek.Sunday }, back.SchedulerDays);
        Assert.True(back.MoveOnFinish);
        Assert.Equal("C:\\Done", back.MoveOnFinishFolder);
        Assert.True(back.RemoveLinkAfterFinish);
        Assert.Equal(30, back.DeleteFinishedLinksAfterDays);
    }

    [Fact]
    public void AppSettings_LegacyJsonLoadsWithSaneDefaults()
    {
        // Pre-borrow settings file: none of the new keys present.
        string legacy = "{\"DownloadFolder\":\"C:\\\\DL\",\"MaxRetries\":5,\"NotifyOnCompletion\":false}";
        var back = JsonSerializer.Deserialize<AppSettings>(legacy, JsonOptions)!;
        Assert.False(back.AutoResumeFailed);
        Assert.False(back.NotifyOnAdded);
        Assert.True(back.NotifyOnError); // new default, not legacy false
        Assert.True(back.NotificationSound);
        Assert.False(back.SchedulerEnabled);
        Assert.Equal(new TimeSpan(22, 0, 0), back.SchedulerStart);
        Assert.Equal(7, back.SchedulerDays.Count);
        Assert.False(back.MoveOnFinish);
        Assert.Equal(0, back.DeleteFinishedLinksAfterDays);
        Assert.Equal(5, back.MaxRetries);
        Assert.False(back.NotifyOnCompletion);
    }

    [Fact]
    public void ValidateSettings_ClampsAndRepairs()
    {
        var m = typeof(TaskStore).GetMethod("ValidateSettings",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        AppSettings Call(AppSettings s) => (AppSettings)m.Invoke(null, new object[] { s })!;

        var s = new AppSettings
        {
            SchedulerSpeedLimitKbps = 5_000_000,
            DeleteFinishedLinksAfterDays = 999,
            SchedulerDays = null!,
            MoveOnFinishFolder = "C:\\ok",
        };
        var v = Call(s);
        Assert.Equal(1_000_000, v.SchedulerSpeedLimitKbps);
        Assert.Equal(365, v.DeleteFinishedLinksAfterDays);
        Assert.Equal(7, v.SchedulerDays.Count);

        var empty = new AppSettings { SchedulerDays = new List<DayOfWeek>() };
        Assert.Empty(Call(empty).SchedulerDays); // explicit empty preserved: window never applies

        var badPath = new AppSettings { MoveOnFinishFolder = "C:\\a<b" };
        Assert.Null(Call(badPath).MoveOnFinishFolder);
    }

    [Theory]
    [InlineData("OptionsControl.xaml", "UserControl")]
    [InlineData("BatchAddDialog.xaml", "Window")]
    [InlineData("MainWindow.xaml", "FluentWindow")]
    [InlineData("AddDownloadDialog.xaml", "Window")]
    [InlineData("DownloadCompleteDialog.xaml", "Window")]
    public void EditedXaml_IsWellFormedXml(string file, string expectedRoot)
    {
        string path = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "WDM", file));
        Assert.True(File.Exists(path), path);
        var doc = new XmlDocument();
        doc.Load(path); // throws on malformed markup
        Assert.Equal(expectedRoot, doc.DocumentElement!.LocalName);
    }

    [Fact]
    public void EditedXaml_ContainsNewControls()
    {
        string dir = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "WDM"));
        string options = File.ReadAllText(Path.Combine(dir, "OptionsControl.xaml"));
        foreach (string name in new[]
        {
            "AutoResumeBox", "SchedulerBox", "SchedulerStartBox", "SchedulerStopBox",
            "SchedulerSpeedBox", "SchedulerDayMonday", "SchedulerDaySunday",
            "NotifyAddedBox", "NotifyStartedBox", "NotifyErrorBox", "NotifySoundBox",
            "DetailedNotifyBox", "MoveOnFinishBox", "MoveFolderBox", "RemoveLinkBox",
            "DeleteAfterDaysBox", "MinCatchBox", "MinCatchCustomBox",
        })
            Assert.Contains($"x:Name=\"{name}\"", options);

        string batch = File.ReadAllText(Path.Combine(dir, "BatchAddDialog.xaml"));
        Assert.Contains("x:Name=\"ItemsList\"", batch);
        Assert.Contains("x:Name=\"AddButton\"", batch);
        Assert.Contains("x:Name=\"TitleText\"", batch);
    }
}
