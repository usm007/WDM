using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using WDM.Services;

namespace WDM;

public partial class ExtensionReloadNoticeControl : UserControl
{
    public event EventHandler? CloseRequested;

    public ExtensionReloadNoticeControl()
    {
        InitializeComponent();
    }

    private static string ShortNotes(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return "";
        var lines = body.Split('\n').Select(l => l.Trim().TrimStart('-', '*', '•', ' ').Trim()).Where(l => !string.IsNullOrWhiteSpace(l)).Take(2).ToArray();
        var s = string.Join(" • ", lines);
        if (string.IsNullOrWhiteSpace(s)) s = body.Trim().Replace("\r", " ").Replace("\n", " ").Trim();
        if (s.Length > 140) s = s.Substring(0, 137) + "...";
        return s;
    }

    public void Initialize(string oldVersion, string newVersion, string? releaseNotes = null)
    {
        TitleText.Text = $"Extension Updated (v{oldVersion} → v{newVersion})";
        SubtitleText.Text = $"The WDM browser extension has been updated to version {newVersion}. Please reload it in your browser to activate all improvements.";
        if (!string.IsNullOrWhiteSpace(releaseNotes))
        {
            var shortNotes = ShortNotes(releaseNotes);
            if (!string.IsNullOrWhiteSpace(shortNotes))
            {
                ReleaseNotesText.Text = $"What's new: {shortNotes}";
                ReleaseNotesText.Visibility = Visibility.Visible;
            }
        }
    }

    private void CopyPath_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string extDir = BrowserIntegration.DeployDir;
            Clipboard.SetText(extDir);
            FeedbackText.Text = "Extension path copied to clipboard!";
        }
        catch (Exception ex)
        {
            FeedbackText.Text = "Could not copy: " + ex.Message;
        }
    }

    private void OpenExtensions_Click(object sender, RoutedEventArgs e)
    {
        BrowserIntegration.OpenExtensionsPage();
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }
}
