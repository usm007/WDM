using System;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Threading;

namespace WDM.Services;

/// <summary>Opt-in clipboard monitor for copied download links. Runs on the UI
/// dispatcher and never reads the clipboard unless the user enabled it in
/// Settings. It deliberately raises only one event per distinct URL and does
/// not touch the clipboard itself (no rewriting, no loops).</summary>
public sealed class ClipboardWatcher : IDisposable
{
    private static readonly Regex HttpUrl = new(
        @"https?://[^\s<>""']+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly char[] TrailingPunctuation = { '.', ',', ';', ':', '!', '?', ')', ']', '}', '>', '"', '\'' };

    private readonly DispatcherTimer _timer;
    private bool _enabled;
    private bool _disposed;
    private string? _lastText;
    private string? _lastUrl;
    private DateTimeOffset _lastRaised;

    public event Action<string>? UrlDetected;

    public ClipboardWatcher()
    {
        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(750),
        };
        _timer.Tick += OnTick;
    }

    public bool IsRunning => _enabled;

    /// <summary>Starts or stops polling without throwing; safe to call on every
    /// settings save.</summary>
    public void SetEnabled(bool enabled)
    {
        if (enabled) Start();
        else Stop();
    }

    public void Start()
    {
        if (_disposed || _enabled)
            return;
        _enabled = true;
        // Do not fire for whatever happened to be on the clipboard before the
        // user turned the feature on.
        _lastText = ReadClipboardText();
        _lastUrl = null;
        _timer.Start();
    }

    public void Stop()
    {
        _enabled = false;
        _timer.Stop();
    }

    private void OnTick(object? sender, EventArgs e) => Poll();

    private void Poll()
    {
        if (!_enabled || _disposed)
            return;

        string? text = ReadClipboardText();
        if (string.IsNullOrWhiteSpace(text))
            return;
        if (string.Equals(text, _lastText, StringComparison.Ordinal))
            return;
        _lastText = text;

        string? url = ExtractHttpUrl(text);
        if (url is null)
            return;

        // Copying the same link twice while it is still on the clipboard is
        // not a new intent; allow it again after a short pause.
        DateTimeOffset now = DateTimeOffset.UtcNow;
        if (string.Equals(url, _lastUrl, StringComparison.OrdinalIgnoreCase) &&
            now - _lastRaised < TimeSpan.FromSeconds(10))
            return;

        _lastUrl = url;
        _lastRaised = now;
        try { UrlDetected?.Invoke(url); }
        catch { /* a UI callback must never kill the timer */ }
    }

    private static string? ReadClipboardText()
    {
        try
        {
            return Clipboard.ContainsText() ? Clipboard.GetText(TextDataFormat.UnicodeText) : null;
        }
        catch
        {
            // Another process can hold the clipboard open; try again next tick.
            return null;
        }
    }

    /// <summary>Returns the first well-formed http(s) URL in arbitrary text
    /// (plain link, Markdown link, chat message), with trailing punctuation
    /// removed. Internal for deterministic tests.</summary>
    internal static string? ExtractHttpUrl(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        Match match;
        try { match = HttpUrl.Match(text); }
        catch { return null; }
        if (!match.Success)
            return null;

        string candidate = match.Value.Trim().TrimEnd(TrailingPunctuation);
        if (candidate.Length < "http://a".Length)
            return null;
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out Uri? uri))
            return null;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            return null;
        if (string.IsNullOrWhiteSpace(uri.Host))
            return null;
        return candidate;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        Stop();
        _timer.Tick -= OnTick;
    }
}
