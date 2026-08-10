using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Threading;

namespace WDM.Services;

/// <summary>
/// Watches the clipboard for URLs and raises UrlCopied when a new one appears.
/// This replicates IDM's "monitor clipboard for download links" behavior.
/// </summary>
public sealed partial class ClipboardMonitor
{
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _timer;
    private string _lastText = "";
    private bool _enabled;

    public event Action<string>? UrlCopied;

    public ClipboardMonitor()
    {
        _dispatcher = Dispatcher.CurrentDispatcher;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
        _timer.Tick += (_, _) => CheckClipboard();
    }

    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (_enabled == value)
                return;
            _enabled = value;
            if (value)
            {
                _lastText = "";
                _timer.Start();
            }
            else
            {
                _timer.Stop();
            }
        }
    }

    private void CheckClipboard()
    {
        string text;
        try
        {
            text = Clipboard.GetText();
        }
        catch
        {
            return; // Clipboard busy.
        }

        if (string.IsNullOrEmpty(text) || text == _lastText)
            return;
        _lastText = text;

        foreach (Match match in UrlRegex().Matches(text))
        {
            string url = match.Value;
            if (!UrlCopiedFilter(url))
                continue;
            UrlCopied?.Invoke(url);
            break;
        }
    }

    private static bool UrlCopiedFilter(string url) =>
        url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
        url.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex(@"https?://[^\s<>""']+", RegexOptions.IgnoreCase)]
    private static partial Regex UrlRegex();
}
