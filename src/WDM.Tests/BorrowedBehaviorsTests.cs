using System.Net;
using WDM.Models;
using WDM.Services;
using WDM.ViewModels;

namespace WDM.Tests;

/// <summary>Covers the IDM-derived behaviours added on top of the core swap:
/// proxy address exceptions, server file dates, and the queue-complete action.</summary>
public sealed class BorrowedBehaviorsTests
{
    [Fact]
    public void ParseBypassMasks_AcceptsLinesCommasAndSemicolons()
    {
        var masks = ProxyHelper.ParseBypassMasks(" *.example.com \r\n10.0.0.*, localhost;*.test/* ");
        Assert.Equal(new[] { "*.example.com", "10.0.0.*", "localhost", "*.test/*" }, masks);
        Assert.Empty(ProxyHelper.ParseBypassMasks("   "));
        Assert.Empty(ProxyHelper.ParseBypassMasks(null));
    }

    [Fact]
    public void BuildProxy_NoBypassList_ReturnsPlainProxy()
    {
        var settings = new AppSettings
        {
            ProxyEnabled = true,
            ProxyHost = "127.0.0.1",
            ProxyPort = 8888,
        };
        var proxy = ProxyHelper.BuildProxy(settings);
        Assert.NotNull(proxy);
        var target = new Uri("https://cdn.example.com/file.bin");
        Assert.Equal(new Uri("http://127.0.0.1:8888"), proxy!.GetProxy(target));
        Assert.False(proxy.IsBypassed(target));
    }

    [Fact]
    public void BuildProxy_BypassMask_RoutesMatchedHostDirect()
    {
        var settings = new AppSettings
        {
            ProxyEnabled = true,
            ProxyHost = "127.0.0.1",
            ProxyPort = 8888,
            ProxyBypassList = "*.example.com\n*.test/files/*",
        };
        var proxy = ProxyHelper.BuildProxy(settings);
        Assert.NotNull(proxy);

        var inside = new Uri("https://cdn.example.com/file.bin");
        Assert.Equal(inside, proxy!.GetProxy(inside));
        Assert.True(proxy.IsBypassed(inside));

        var pathOnly = new Uri("https://mirror.test/files/big.iso");
        Assert.Equal(pathOnly, proxy.GetProxy(pathOnly));

        var outside = new Uri("https://mirror.test/other.iso");
        Assert.Equal(new Uri("http://127.0.0.1:8888"), proxy.GetProxy(outside));
        Assert.False(proxy.IsBypassed(outside));
    }

    [Fact]
    public void BuildProxy_Disabled_ReturnsNull()
    {
        Assert.Null(ProxyHelper.BuildProxy(new AppSettings { ProxyEnabled = false }));
        Assert.Null(ProxyHelper.BuildProxy(new AppSettings { ProxyEnabled = true, ProxyHost = "" }));
    }

    [Fact]
    public void ApplyServerTimestamp_SetsFileDateWhenEnabled()
    {
        string path = Path.GetTempFileName();
        try
        {
            var task = new DownloadTask
            {
                SaveFolder = Path.GetDirectoryName(path)!,
                FileName = Path.GetFileName(path),
                LastModified = "Wed, 21 Oct 2026 07:28:00 GMT",
            };

            Assert.True(MainViewModel.ApplyServerTimestamp(task, enabled: true));
            Assert.Equal(new DateTime(2026, 10, 21, 7, 28, 0, DateTimeKind.Utc),
                File.GetLastWriteTimeUtc(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ApplyServerTimestamp_RespectsToggleAndBadInput()
    {
        string path = Path.GetTempFileName();
        try
        {
            var task = new DownloadTask
            {
                SaveFolder = Path.GetDirectoryName(path)!,
                FileName = Path.GetFileName(path),
                LastModified = "Wed, 21 Oct 2026 07:28:00 GMT",
            };

            Assert.False(MainViewModel.ApplyServerTimestamp(task, enabled: false));
            task.LastModified = "not a date";
            Assert.False(MainViewModel.ApplyServerTimestamp(task, enabled: true));
            task.LastModified = null;
            Assert.False(MainViewModel.ApplyServerTimestamp(task, enabled: true));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void CommandLineFlags_SilentSwitchAndUrlDetection()
    {
        Assert.True(App.IsSilentFlag("/s"));
        Assert.True(App.IsSilentFlag("--silent"));
        Assert.False(App.IsSilentFlag("/d"));
        Assert.True(App.ArgsContainUrl(new[] { "/d", "https://example.com/file.zip" }));
        Assert.False(App.ArgsContainUrl(new[] { "/d", "/s" }));
        Assert.Equal("https://example.com/file.zip",
            App.FirstDownloadLink(" https://example.com/file.zip "));
    }

    [Fact]
    public void QueueCompleteAction_DoesNotFireOnAnEmptyStartupQueue()
    {
        using var vm = new MainViewModel();
        vm.SuppressPersistence();
        vm.Tasks.Clear();
        bool exited = false;
        vm.ExitRequested += () => exited = true;

        vm.ArmQueueCompleteActionForSession(QueueCompleteAction.Exit);

        Assert.False(exited);
        Assert.Equal(QueueCompleteAction.Exit, vm.QueueCompleteAction);
    }
}
