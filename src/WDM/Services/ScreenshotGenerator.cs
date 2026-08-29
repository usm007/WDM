using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using WDM.Models;
using WDM.ViewModels;

namespace WDM.Services;

public static class ScreenshotGenerator
{
    public static void Run()
    {
        CleanupLegacyFiles();

        string outputDir = @"E:\WDM-2\screenshots";
        string lightDir = Path.Combine(outputDir, "light");
        string darkDir = Path.Combine(outputDir, "dark");

        // Clear existing screenshot folders for fresh capture
        if (Directory.Exists(lightDir)) Directory.Delete(lightDir, true);
        if (Directory.Exists(darkDir)) Directory.Delete(darkDir, true);

        Directory.CreateDirectory(lightDir);
        Directory.CreateDirectory(darkDir);

        Console.WriteLine("==================================================");
        Console.WriteLine("   WDM Automated Screenshot Generator Starting   ");
        Console.WriteLine("==================================================");

        foreach (bool dark in new[] { false, true })
        {
            string themeFolder = dark ? "dark" : "light";
            string targetDir = Path.Combine(outputDir, themeFolder);
            Console.WriteLine($"\n--- Generating {themeFolder.ToUpperInvariant()} Mode Screenshots ({targetDir}) ---");

            ThemeService.Apply(AppTheme.Default, dark);

            var viewModel = new MainViewModel();
            viewModel.Settings.UseDarkTheme = dark;
            PopulateMockTasks(viewModel);

            // 1. MainWindow
            var mainWindow = new MainWindow { DataContext = viewModel, Width = 980, Height = 600 };
            SaveWindowScreenshot(mainWindow, Path.Combine(targetDir, "01_MainWindow.png"));

            // 2. AddDownloadDialog
            var addDialog = new AddDownloadDialog(viewModel, "https://releases.ubuntu.com/24.04/ubuntu-24.04-desktop-amd64.iso", "ubuntu-24.04-desktop-amd64.iso");
            SaveWindowScreenshot(addDialog, Path.Combine(targetDir, "02_AddDownloadDialog.png"));

            // 3. OptionsDialog (Main + All Tabs)
            CaptureOptionsDialogAllTabs(viewModel, targetDir);

            // 4. AboutDialog
            var aboutDialog = new AboutDialog();
            SaveWindowScreenshot(aboutDialog, Path.Combine(targetDir, "04_AboutDialog.png"));

            // 5. DownloadProgressDialog
            var activeTask = viewModel.Tasks.First(t => t.Status == TaskStatus.Downloading);
            var progressDialog = new DownloadProgressDialog(activeTask, viewModel);
            progressDialog.ChunkList.Clear();
            var chunkPercents = new[] { 94, 88, 79, 65, 52, 44, 31, 18 };
            for (int i = 0; i < chunkPercents.Length; i++)
            {
                progressDialog.ChunkList.Add(new ChunkVisualItem
                {
                    Index = i + 1,
                    ToolTip = $"Thread #{i + 1} — {chunkPercents[i]}%",
                    WidthPercent = chunkPercents[i]
                });
            }
            SaveWindowScreenshot(progressDialog, Path.Combine(targetDir, "05_DownloadProgressDialog.png"));

            // 6. DownloadCompleteDialog
            var completedTask = viewModel.Tasks.First(t => t.Status == TaskStatus.Completed);
            var completeDialog = new DownloadCompleteDialog(completedTask);
            SaveWindowScreenshot(completeDialog, Path.Combine(targetDir, "06_DownloadCompleteDialog.png"));

            // 7. DuplicateDownloadDialog
            var duplicateDialog = new DuplicateDownloadDialog("https://download.visualstudio.microsoft.com/download/pr/VisualStudioSetup.exe", "VisualStudioSetup.exe", "VisualStudioSetup (1).exe");
            SaveWindowScreenshot(duplicateDialog, Path.Combine(targetDir, "07_DuplicateDownloadDialog.png"));

            // 8. TaskPropertiesDialog
            var propsDialog = new TaskPropertiesDialog(activeTask);
            SaveWindowScreenshot(propsDialog, Path.Combine(targetDir, "08_TaskPropertiesDialog.png"));

            // 9. RefreshLinkDialog
            var refreshDialog = new RefreshLinkDialog(activeTask);
            SaveWindowScreenshot(refreshDialog, Path.Combine(targetDir, "09_RefreshLinkDialog.png"));

            // 10. DeleteConfirmDialog
            var deleteDialog = new DeleteConfirmDialog("Are you sure you want to delete 2 selected downloads from the list?", true);
            SaveWindowScreenshot(deleteDialog, Path.Combine(targetDir, "10_DeleteConfirmDialog.png"));

            // 11. BrowserExtensionDialog
            var extensionDialog = new BrowserExtensionDialog();
            SaveWindowScreenshot(extensionDialog, Path.Combine(targetDir, "11_BrowserExtensionDialog.png"));

            // 12. ExtensionReloadNoticeDialog
            var reloadNoticeDialog = new ExtensionReloadNoticeDialog("1.1.3", "1.1.4");
            SaveWindowScreenshot(reloadNoticeDialog, Path.Combine(targetDir, "12_ExtensionReloadNoticeDialog.png"));

            // 13. WelcomeWindow
            var welcomeWindow = new WelcomeWindow(viewModel.Settings);
            SaveWindowScreenshot(welcomeWindow, Path.Combine(targetDir, "13_WelcomeWindow.png"));

            // 14. UpdateAvailableDialog
            var releaseInfo = new ReleaseInfo(
                "v2.5.1",
                new Version(2, 5, 1),
                "v2.5.1",
                "https://github.com/usm007/WDM/releases/tag/v2.5.1",
                "What's new in v2.5.1:\n• High-DPI PerMonitorV2 support\n• Resumable HLS streaming downloads\n• Faster multi-chunk download speeds\n• Enhanced browser extension handshake",
                DateTime.Now,
                "https://github.com/usm007/WDM/releases/download/v2.5.1/WDM_Setup_2.5.1.0.exe");
            var updateDialog = new UpdateAvailableDialog(releaseInfo);
            SaveWindowScreenshot(updateDialog, Path.Combine(targetDir, "14_UpdateAvailableDialog.png"));

            // 15. CloudflareChallengeWindow
            try
            {
                var cfTask = new DownloadTask(Dispatcher.CurrentDispatcher) { Url = "https://protected-site.example/download.zip", FileName = "protected-download.zip" };
                var cfWindow = new CloudflareChallengeWindow(cfTask);
                SaveWindowScreenshot(cfWindow, Path.Combine(targetDir, "15_CloudflareChallengeWindow.png"));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[!] CloudflareChallengeWindow skipped: {ex.Message}");
            }

            // 16. YouTubeSignInWindow
            try
            {
                var ytWindow = new YouTubeSignInWindow();
                SaveWindowScreenshot(ytWindow, Path.Combine(targetDir, "16_YouTubeSignInWindow.png"));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[!] YouTubeSignInWindow skipped: {ex.Message}");
            }

            // 17. TrayProgressPanel
            var trayPanel = new TrayProgressPanel(viewModel);
            trayPanel.ShowPanel(activeTask);
            SaveWindowScreenshot(trayPanel, Path.Combine(targetDir, "17_TrayProgressPanel.png"));
            trayPanel.HidePanel();
        }

        Console.WriteLine("\n==================================================");
        Console.WriteLine("   All Screenshots Successfully Generated!        ");
        Console.WriteLine($"   Location: {outputDir}                          ");
        Console.WriteLine("==================================================");
    }

    private static void CleanupLegacyFiles()
    {
        string baseDir = @"E:\WDM-2\src\WDM";
        var filesToDelete = new[]
        {
            Path.Combine(baseDir, "WdmOriginalMainWindow.xaml"),
            Path.Combine(baseDir, "WdmOriginalMainWindow.xaml.cs"),
            Path.Combine(baseDir, "Themes", "WdmOriginal", "Palette.Dark.xaml"),
            Path.Combine(baseDir, "Themes", "WdmOriginal", "Palette.Light.xaml"),
            Path.Combine(baseDir, "Themes", "WdmOriginal", "Theme.xaml"),
        };

        foreach (var file in filesToDelete)
        {
            try
            {
                if (File.Exists(file))
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                    File.Delete(file);
                }
            }
            catch { }
        }

        try
        {
            string dir = Path.Combine(baseDir, "Themes", "WdmOriginal");
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, true);
            }
        }
        catch { }
    }

    private static void CaptureOptionsDialogAllTabs(MainViewModel viewModel, string targetDir)
    {
        var optionsDialog = new OptionsDialog(viewModel);
        SaveWindowScreenshot(optionsDialog, Path.Combine(targetDir, "03_OptionsDialog.png"), closeWindow: false);

        var tabs = new (string Tag, string FileName)[]
        {
            ("Connection", "03_OptionsDialog_01_Connection.png"),
            ("Folders", "03_OptionsDialog_02_Folders.png"),
            ("Browser", "03_OptionsDialog_03_Browser.png"),
            ("YouTube", "03_OptionsDialog_04_YouTube.png"),
            ("Behavior", "03_OptionsDialog_05_Behavior.png"),
            ("Appearance", "03_OptionsDialog_06_Appearance.png"),
            ("Updates", "03_OptionsDialog_07_Updates.png"),
        };

        foreach (var (tag, fileName) in tabs)
        {
            SwitchOptionsDialogTab(optionsDialog, tag);
            SaveWindowScreenshot(optionsDialog, Path.Combine(targetDir, fileName), closeWindow: false);
        }

        try { optionsDialog.Close(); } catch { }
    }

    private static void SwitchOptionsDialogTab(OptionsDialog dialog, string tag)
    {
        if (dialog.PanelConnection != null) dialog.PanelConnection.Visibility = tag == "Connection" ? Visibility.Visible : Visibility.Collapsed;
        if (dialog.PanelFolders != null) dialog.PanelFolders.Visibility = tag == "Folders" ? Visibility.Visible : Visibility.Collapsed;
        if (dialog.PanelBrowser != null) dialog.PanelBrowser.Visibility = tag == "Browser" ? Visibility.Visible : Visibility.Collapsed;
        if (dialog.PanelYouTube != null) dialog.PanelYouTube.Visibility = tag == "YouTube" ? Visibility.Visible : Visibility.Collapsed;
        if (dialog.PanelBehavior != null) dialog.PanelBehavior.Visibility = tag == "Behavior" ? Visibility.Visible : Visibility.Collapsed;
        if (dialog.PanelAppearance != null) dialog.PanelAppearance.Visibility = tag == "Appearance" ? Visibility.Visible : Visibility.Collapsed;
        if (dialog.PanelUpdates != null) dialog.PanelUpdates.Visibility = tag == "Updates" ? Visibility.Visible : Visibility.Collapsed;

        // Also check the radio button
        var radioButtons = FindVisualChildren<RadioButton>(dialog);
        foreach (var rb in radioButtons)
        {
            if (rb.Tag is string t && string.Equals(t, tag, StringComparison.OrdinalIgnoreCase))
            {
                rb.IsChecked = true;
            }
        }
    }

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject depObj) where T : DependencyObject
    {
        if (depObj == null) yield break;
        int count = VisualTreeHelper.GetChildrenCount(depObj);
        for (int i = 0; i < count; i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(depObj, i);
            if (child is T t)
            {
                yield return t;
            }
            foreach (T childOfChild in FindVisualChildren<T>(child))
            {
                yield return childOfChild;
            }
        }
    }

    private static void PopulateMockTasks(MainViewModel vm)
    {
        vm.Tasks.Clear();
        var ui = Dispatcher.CurrentDispatcher;

        var t1 = new DownloadTask(ui)
        {
            Url = "https://releases.ubuntu.com/24.04/ubuntu-24.04-desktop-amd64.iso",
            FileName = "ubuntu-24.04-desktop-amd64.iso",
            TotalBytes = 6_120_328_192,
            DownloadedBytes = 4_100_000_000,
            SpeedBps = 18_450_000,
            Status = TaskStatus.Downloading,
            Category = DownloadCategory.Compressed,
            ChunkCount = 8,
            Eta = "1m 45s",
            AddedAt = DateTime.Now.AddMinutes(-5)
        };

        var t2 = new DownloadTask(ui)
        {
            Url = "https://download.visualstudio.microsoft.com/download/pr/VisualStudioSetup.exe",
            FileName = "VisualStudioSetup.exe",
            TotalBytes = 3_820_000,
            DownloadedBytes = 3_820_000,
            Status = TaskStatus.Completed,
            Category = DownloadCategory.Program,
            CompletedAt = DateTime.Now.AddMinutes(-12),
            AddedAt = DateTime.Now.AddMinutes(-15)
        };

        var t3 = new DownloadTask(ui)
        {
            Url = "https://commondatastorage.googleapis.com/gtv-videos-bucket/sample/BigBuckBunny.mp4",
            FileName = "BigBuckBunny_4K_HDR.mp4",
            TotalBytes = 1_050_000_000,
            DownloadedBytes = 525_000_000,
            SpeedBps = 0,
            Status = TaskStatus.Paused,
            Category = DownloadCategory.Video,
            AddedAt = DateTime.Now.AddHours(-1)
        };

        var t4 = new DownloadTask(ui)
        {
            Url = "https://cdn.example.org/files/financial_report_q3_2026.pdf",
            FileName = "financial_report_q3_2026.pdf",
            TotalBytes = 14_200_000,
            DownloadedBytes = 0,
            Status = TaskStatus.Queued,
            Category = DownloadCategory.Document,
            AddedAt = DateTime.Now.AddMinutes(-2)
        };

        var t5 = new DownloadTask(ui)
        {
            Url = "https://files.freemusicarchive.org/track_09_synthwave_sunset.flac",
            FileName = "synthwave_sunset_master.flac",
            TotalBytes = 48_500_000,
            DownloadedBytes = 12_000_000,
            Status = TaskStatus.Failed,
            Error = "Connection timeout — 504 Gateway Error",
            Category = DownloadCategory.Music,
            AddedAt = DateTime.Now.AddHours(-2)
        };

        vm.Tasks.Add(t1);
        vm.Tasks.Add(t2);
        vm.Tasks.Add(t3);
        vm.Tasks.Add(t4);
        vm.Tasks.Add(t5);
    }

    private static void SaveWindowScreenshot(Window window, string filePath, bool closeWindow = true)
    {
        try
        {
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Left = -20000;
            window.Top = -20000;
            window.Show();

            DoEvents();

            double width = window.ActualWidth > 0 ? window.ActualWidth : (double.IsNaN(window.Width) || window.Width <= 0 ? 560 : window.Width);
            double height = window.ActualHeight > 0 ? window.ActualHeight : (double.IsNaN(window.Height) || window.Height <= 0 ? 420 : window.Height);

            window.Measure(new Size(width, height));
            window.Arrange(new Rect(0, 0, width, height));
            window.UpdateLayout();
            DoEvents();

            int pixelWidth = (int)Math.Ceiling(width * 1.5);
            int pixelHeight = (int)Math.Ceiling(height * 1.5);

            var rtb = new RenderTargetBitmap(pixelWidth, pixelHeight, 144, 144, PixelFormats.Pbgra32);
            rtb.Render(window);

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(rtb));
            using (var stream = File.Create(filePath))
            {
                encoder.Save(stream);
            }

            Console.WriteLine($"[+] Captured: {Path.GetFileName(filePath)}");

            if (closeWindow)
            {
                window.Close();
                DoEvents();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[!] Failed to capture {filePath}: {ex.Message}");
            if (closeWindow)
            {
                try { window.Close(); } catch { }
            }
        }
    }

    private static void DoEvents()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new DispatcherOperationCallback(f =>
        {
            ((DispatcherFrame)f).Continue = false;
            return null;
        }), frame);
        Dispatcher.PushFrame(frame);
    }
}
