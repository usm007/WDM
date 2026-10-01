using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;

namespace WDM.Services;

/// <summary>User-triggered error reporting (free forever, zero third parties):
/// copies diagnostics to the clipboard and opens a pre-filled GitHub issue.
/// Nothing ever leaves the machine unless the user clicks "Report a problem"
/// and submits the issue themselves: consistent with WDM's no-telemetry stance.</summary>
public static class IssueReporter
{
    private const string RepoUrl = "https://github.com/usm007/WDM";
    private const int MaxLogChars = 6000;
    private const int MaxLogLines = 80;

    /// <summary>Copies diagnostics and opens the new-issue page. Safe to call
    /// from any UI thread; all failures degrade to a small friendly message.</summary>
    public static void Report(Window? owner)
    {
        string diagnostics;
        try
        {
            diagnostics = CollectDiagnostics();
        }
        catch (Exception ex)
        {
            try { App.LogException(ex); } catch { }
            diagnostics = $"WDM {UpdateChecker.CurrentVersion}: couldn't read the error log.";
        }

        try
        {
            Clipboard.SetText(diagnostics);
        }
        catch (Exception ex)
        {
            try { App.LogException(ex); } catch { }
            ErrorDialogs.ShowWarning(owner, "Couldn't copy details",
                "Your details couldn't be copied. Please describe the problem in the issue instead.");
        }

        string title = Uri.EscapeDataString("Problem: ");
        string body = Uri.EscapeDataString(
            "What happened?\n(Describe what you were doing when the problem occurred.)\n\n" +
            $"WDM version: {UpdateChecker.CurrentVersion}\n" +
            $"Windows: {Environment.OSVersion.Version}\n\n" +
            "Error details (already on your clipboard: paste them below):\n```\n```");
        try
        {
            Process.Start(new ProcessStartInfo($"{RepoUrl}/issues/new?title={title}&body={body}")
            {
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            try { App.LogException(ex); } catch { }
            ErrorDialogs.ShowWarning(owner, "Couldn't open browser",
                "Your details are copied: please paste them into a new issue at github.com/usm007/WDM.");
            return;
        }

        MessageBox.Show(owner,
            "Your error details are copied to the clipboard.\nPaste them into the GitHub issue to send the report.",
            "Report a problem", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private static string CollectDiagnostics()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"WDM {UpdateChecker.CurrentVersion}");
        sb.AppendLine($"Windows {Environment.OSVersion.Version} (64-bit: {Environment.Is64BitOperatingSystem})");
        sb.AppendLine($"Date (UTC): {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}");

        string logPath;
        try { logPath = Path.Combine(TaskStore.AppDir, "wdm_error.log"); }
        catch { logPath = ""; }

        if (!string.IsNullOrEmpty(logPath) && File.Exists(logPath))
        {
            try
            {
                string[] lines = File.ReadAllLines(logPath);
                string[] tail = lines.Skip(Math.Max(0, lines.Length - MaxLogLines)).ToArray();
                string log = RedactProfilePath(string.Join(Environment.NewLine, tail));
                if (log.Length > MaxLogChars)
                    log = log.Substring(log.Length - MaxLogChars);
                sb.AppendLine();
                sb.AppendLine("--- Recent errors ---");
                sb.Append(log);
            }
            catch
            {
                sb.AppendLine();
                sb.AppendLine("(Couldn't read the error log.)");
            }
        }
        else
        {
            sb.AppendLine();
            sb.AppendLine("(No errors logged yet.)");
        }

        return sb.ToString();
    }

    /// <summary>Stack traces and paths can contain the Windows user name —
    /// replace the profile folder with %USERPROFILE% before it leaves the machine.</summary>
    private static string RedactProfilePath(string text)
    {
        try
        {
            string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrWhiteSpace(profile))
                text = text.Replace(profile, "%USERPROFILE%", StringComparison.OrdinalIgnoreCase);
        }
        catch { }
        return text;
    }
}
