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
            NotifyOnError = false,
            NotificationSound = false,
            DeleteFinishedLinksAfterDays = 30,
        };
        string json = JsonSerializer.Serialize(s, JsonOptions);
        var back = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions)!;
        Assert.True(back.AutoResumeFailed);
        Assert.False(back.NotifyOnError);
        Assert.False(back.NotificationSound);
        Assert.Equal(30, back.DeleteFinishedLinksAfterDays);
    }

    [Fact]
    public void AppSettings_LegacyJsonLoadsWithSaneDefaults()
    {
        // Pre-borrow settings file: none of the new keys present.
        string legacy = "{\"DownloadFolder\":\"C:\\\\DL\",\"MaxRetries\":5,\"NotifyOnCompletion\":false}";
        var back = JsonSerializer.Deserialize<AppSettings>(legacy, JsonOptions)!;
        Assert.False(back.AutoResumeFailed);
        Assert.True(back.NotifyOnError); // new default, not legacy false
        Assert.True(back.NotificationSound);
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
            DeleteFinishedLinksAfterDays = 999,
        };
        var v = Call(s);
        Assert.Equal(365, v.DeleteFinishedLinksAfterDays);
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
            AppContext.BaseDirectory, "..", "..", "..", "..", "WDM.App", file));
        Assert.True(File.Exists(path), path);
        var doc = new XmlDocument();
        doc.Load(path); // throws on malformed markup
        Assert.Equal(expectedRoot, doc.DocumentElement!.LocalName);
    }

    [Fact]
    public void EditedXaml_ContainsNewControls()
    {
        string dir = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "WDM.App"));
        string options = File.ReadAllText(Path.Combine(dir, "OptionsControl.xaml"));
        foreach (string name in new[]
        {
            "AutoResumeBox",
            "NotifyBox", "NotifyErrorBox", "NotifySoundBox",
            "DeleteAfterDaysBox", "MinCatchBox", "MinCatchCustomBox",
        })
            Assert.Contains($"x:Name=\"{name}\"", options);

        string batch = File.ReadAllText(Path.Combine(dir, "BatchAddDialog.xaml"));
        Assert.Contains("x:Name=\"ItemsList\"", batch);
        Assert.Contains("x:Name=\"AddButton\"", batch);
        Assert.Contains("x:Name=\"TitleText\"", batch);
    }
}
