using Microsoft.Win32;

namespace WDM.Services;

/// <summary>
/// Decides whether this launch is a first-ever run (show onboarding Welcome)
/// or an update/relaunch (show the extension reload notice instead).
///
/// Earlier logic looked only at data files (tasks.json/settings.json), so an
/// updater whose data had been wiped was misclassified as a new user: they got
/// the Welcome screen and never saw the reload notice. Install evidence —
/// Velopack state, leftover app bits in the legacy install root, or the Inno
/// uninstall key — survives data loss and breaks the tie.
/// </summary>
public static class InstallState
{
    private const string InnoUninstallKey =
        @"Software\Microsoft\Windows\CurrentVersion\Uninstall\{4F3B2C0A-8D2E-4B7A-9C1E-6A5B4D3E2F10}_is1";

    /// <summary>True when user data files exist in the current or legacy home.</summary>
    public static bool HasUserData()
    {
        try
        {
            return File.Exists(Path.Combine(TaskStore.AppDir, "tasks.json"))
                || File.Exists(Path.Combine(TaskStore.AppDir, "settings.json"))
                || File.Exists(Path.Combine(TaskStore.LegacyAppDir, "tasks.json"))
                || File.Exists(Path.Combine(TaskStore.LegacyAppDir, "settings.json"));
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// True when there is evidence WDM was installed before, even if all user
    /// data is gone: a Velopack install, leftover binaries in the legacy
    /// install root, or the Inno uninstall registry key.
    /// </summary>
    public static bool HasInstallEvidence()
    {
        try
        {
            // Leftover app bits in the legacy install root (%LocalAppData%\WDM).
            // Data files alone don't count — they are checked by HasUserData().
            string root = TaskStore.LegacyAppDir;
            if (Directory.Exists(root))
            {
                if (File.Exists(Path.Combine(root, "Update.exe"))
                    || File.Exists(Path.Combine(root, "WDM.exe"))
                    || File.Exists(Path.Combine(root, "unins000.exe"))
                    || File.Exists(Path.Combine(root, "current"))
                    || Directory.Exists(Path.Combine(root, "packages")))
                    return true;
                try
                {
                    if (Directory.GetDirectories(root, "app-*").Length > 0)
                        return true;
                }
                catch { }
            }

            // Inno Setup uninstall registration (per-user or machine-wide).
            try
            {
                if (Registry.GetValue(@"HKEY_CURRENT_USER\" + InnoUninstallKey, "DisplayName", null) is not null)
                    return true;
                if (Registry.GetValue(@"HKEY_LOCAL_MACHINE\" + InnoUninstallKey, "DisplayName", null) is not null)
                    return true;
            }
            catch { }

            // Velopack-managed install (most expensive check last; guarded inside).
            try
            {
                if (VelopackUpdateService.IsVelopackInstalled)
                    return true;
            }
            catch { }
        }
        catch { }
        return false;
    }

    /// <summary>True only for a genuine first launch: no version record, no user
    /// data anywhere, never prompted — and no trace of a previous install.</summary>
    public static bool IsFirstEverRun(AppSettings settings)
    {
        try
        {
            return string.IsNullOrWhiteSpace(settings.LastRunVersion)
                && !settings.HasPromptedExtensionInstall
                && !HasUserData()
                && !HasInstallEvidence();
        }
        catch
        {
            return false;
        }
    }

    /// <summary>True when the app version changed since the last run, or the
    /// version stamp is missing but this machine was clearly used before
    /// (data files or install evidence exist).</summary>
    public static bool IsUpdate(AppSettings settings, string currentVersion)
    {
        try
        {
            string? lastVer = settings.LastRunVersion;
            if (!string.IsNullOrWhiteSpace(lastVer))
                return !string.Equals(lastVer, currentVersion, StringComparison.OrdinalIgnoreCase);
            return HasUserData() || HasInstallEvidence();
        }
        catch
        {
            return false;
        }
    }
}
