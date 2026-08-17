using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Win32;

namespace WDM.Setup;

public partial class MainWindow : Window
{
    private readonly string _installDir;

    public MainWindow()
    {
        InitializeComponent();
        _installDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WDM");
        InstallFolderBox.Text = _installDir;
    }

    private async void InstallClick(object sender, RoutedEventArgs e)
    {
        InstallButton.IsEnabled = false;
        CancelButton.IsEnabled = false;
        InstallFolderBox.IsEnabled = false;
        DesktopShortcutBox.IsEnabled = false;
        StartMenuShortcutBox.IsEnabled = false;
        LaunchAfterBox.IsEnabled = false;

        try
        {
            StatusText.Text = "Stopping existing WDM instances...";
            InstallProgress.Value = 10;
            await Task.Run(KillWdmProcess);

            StatusText.Text = "Preparing target installation directory...";
            InstallProgress.Value = 25;
            Directory.CreateDirectory(_installDir);

            StatusText.Text = "Extracting WDM application & extension files...";
            InstallProgress.Value = 45;
            await ExtractPayloadAsync(_installDir);

            StatusText.Text = "Creating Windows desktop & start menu shortcuts...";
            InstallProgress.Value = 75;
            string exePath = Path.Combine(_installDir, "WDM.exe");

            if (DesktopShortcutBox.IsChecked == true)
            {
                string desktopFolder = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                CreateShortcut(Path.Combine(desktopFolder, "Windows Download Manager.lnk"), exePath, "Windows Download Manager", exePath);
            }

            if (StartMenuShortcutBox.IsChecked == true)
            {
                string startMenu = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs");
                Directory.CreateDirectory(startMenu);
                CreateShortcut(Path.Combine(startMenu, "Windows Download Manager.lnk"), exePath, "Windows Download Manager", exePath);
            }

            StatusText.Text = "Registering system components...";
            InstallProgress.Value = 90;
            RegisterUninstaller(_installDir, exePath);

            InstallProgress.Value = 100;
            StatusText.Text = "Installation finished successfully!";

            if (LaunchAfterBox.IsChecked == true && File.Exists(exePath))
            {
                Process.Start(new ProcessStartInfo(exePath) { UseShellExecute = true });
            }

            MessageBox.Show(this, "Windows Download Manager (WDM) has been installed successfully!", "Installation Complete", MessageBoxButton.OK, MessageBoxImage.Information);
            Close();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Installation failed:\n{ex.Message}", "Setup Error", MessageBoxButton.OK, MessageBoxImage.Error);
            InstallButton.IsEnabled = true;
            CancelButton.IsEnabled = true;
            StatusText.Text = "Installation failed. Please try again.";
        }
    }

    private static void KillWdmProcess()
    {
        try
        {
            foreach (var proc in Process.GetProcessesByName("WDM"))
            {
                proc.Kill();
                proc.WaitForExit(2000);
            }
        }
        catch
        {
            // Best effort
        }
    }

    private static async Task ExtractPayloadAsync(string targetDir)
    {
        await Task.Run(() =>
        {
            var asm = Assembly.GetExecutingAssembly();
            using var stream = asm.GetManifestResourceStream("WDM.Setup.WDM_Payload.zip")
                ?? asm.GetManifestResourceStream("WDM_Payload.zip");

            if (stream is null)
            {
                // Try finding embedded resource by suffix
                foreach (string name in asm.GetManifestResourceNames())
                {
                    if (name.EndsWith("WDM_Payload.zip", StringComparison.OrdinalIgnoreCase))
                    {
                        using var s = asm.GetManifestResourceStream(name);
                        if (s is not null)
                        {
                            ExtractZipStream(s, targetDir);
                            return;
                        }
                    }
                }
                throw new FileNotFoundException("Embedded installer payload (WDM_Payload.zip) missing from binary.");
            }

            ExtractZipStream(stream, targetDir);
        });
    }

    private static void ExtractZipStream(Stream zipStream, string targetDir)
    {
        using var archive = new ZipArchive(zipStream, ZipArchiveMode.Read);
        foreach (var entry in archive.Entries)
        {
            string destinationPath = Path.GetFullPath(Path.Combine(targetDir, entry.FullName));
            if (!destinationPath.StartsWith(Path.GetFullPath(targetDir), StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException("Zip entry attempted directory traversal outside target.");
            }

            if (string.IsNullOrEmpty(entry.Name))
            {
                Directory.CreateDirectory(destinationPath);
            }
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
                entry.ExtractToFile(destinationPath, overwrite: true);
            }
        }
    }

    private static void CreateShortcut(string shortcutPath, string targetPath, string description, string iconPath)
    {
        try
        {
            string psScript = $"$s = (New-Object -COM WScript.Shell).CreateShortcut('{shortcutPath}'); $s.TargetPath = '{targetPath}'; $s.Description = '{description}'; $s.IconLocation = '{iconPath}'; $s.Save()";
            using var proc = Process.Start(new ProcessStartInfo("powershell", $"-NoProfile -ExecutionPolicy Bypass -Command \"{psScript}\"")
            {
                CreateNoWindow = true,
                UseShellExecute = false
            });
            proc?.WaitForExit();
        }
        catch
        {
            // Shortcut fallback
        }
    }

    private static void RegisterUninstaller(string installDir, string exePath)
    {
        try
        {
            const string keyPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\WDM";
            using var key = Registry.CurrentUser.CreateSubKey(keyPath);
            if (key is null) return;
            key.SetValue("DisplayName", "Windows Download Manager");
            key.SetValue("DisplayIcon", exePath);
            key.SetValue("DisplayVersion", "1.0.0");
            key.SetValue("Publisher", "WDM Team");
            key.SetValue("InstallLocation", installDir);
            key.SetValue("UninstallString", $"\"{exePath}\" --uninstall");
        }
        catch
        {
            // Registry fallback
        }
    }

    private void CancelClick(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
