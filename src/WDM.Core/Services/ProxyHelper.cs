using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;

namespace WDM.Services;

/// <summary>Manual HTTP/HTTPS proxy from settings (IDM-style).</summary>
public static class ProxyHelper
{
    /// <summary>Null unless enabled with a host. Tolerates a pasted
    /// "http://host:port" in the host box.</summary>
    public static IWebProxy? BuildProxy(AppSettings s)
    {
        try
        {
            if (s is null || !s.ProxyEnabled || string.IsNullOrWhiteSpace(s.ProxyHost))
                return null;
            string host = s.ProxyHost.Trim();
            int port = Math.Clamp(s.ProxyPort, 1, 65535);
            if (host.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                host.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                if (!Uri.TryCreate(host, UriKind.Absolute, out var pu))
                    return null;
                host = pu.Host;
                if (pu.Port > 0)
                    port = pu.Port;
            }
            var proxy = new WebProxy(new Uri($"http://{host}:{port}"))
            {
                BypassProxyOnLocal = true,
            };
            if (!string.IsNullOrWhiteSpace(s.ProxyUsername))
                proxy.Credentials = new NetworkCredential(s.ProxyUsername.Trim(), s.ProxyPassword ?? "");
            var masks = ParseBypassMasks(s.ProxyBypassList);
            return masks.Count == 0 ? proxy : new MaskedProxy(proxy, masks);
        }
        catch { return null; }
    }

    /// <summary>Parses the bypass text box into a clean mask list. Accepts one
    /// mask per line, or comma/semicolon separated lists.</summary>
    public static IReadOnlyList<string> ParseBypassMasks(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return Array.Empty<string>();
        return text
            .Split(new[] { '\r', '\n', ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(m => m.Trim())
            .Where(m => m.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>Routes matched URLs around the manual proxy. The match syntax
    /// comes from the IDM address-exception list (host wildcards and
    /// host/path wildcards), so proxy bypass and catch exceptions share one
    /// grammar.</summary>
    private sealed class MaskedProxy : IWebProxy
    {
        private readonly IWebProxy _inner;
        private readonly IReadOnlyList<string> _masks;

        public MaskedProxy(IWebProxy inner, IReadOnlyList<string> masks)
        {
            _inner = inner;
            _masks = masks;
        }

        public ICredentials? Credentials
        {
            get => _inner.Credentials;
            set => _inner.Credentials = value;
        }

        public Uri? GetProxy(Uri destination) =>
            Matches(destination) ? destination : _inner.GetProxy(destination);

        public bool IsBypassed(Uri host) =>
            Matches(host) || _inner.IsBypassed(host);

        private bool Matches(Uri uri) =>
            Redm.Core.AddressExceptions.IsExcluded(_masks, uri.AbsoluteUri);
    }

    /// <summary>http_proxy-style URL for child processes (yt-dlp --proxy,
    /// ffmpeg http_proxy env). Null when the proxy is off.</summary>
    public static string? ProxyUrlFor(AppSettings s)
    {
        try
        {
            if (s is null || !s.ProxyEnabled || string.IsNullOrWhiteSpace(s.ProxyHost))
                return null;
            string host = s.ProxyHost.Trim();
            int port = Math.Clamp(s.ProxyPort, 1, 65535);
            if (host.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                host.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                if (!Uri.TryCreate(host, UriKind.Absolute, out var pu))
                    return null;
                host = pu.Host;
                if (pu.Port > 0)
                    port = pu.Port;
            }
            if (host.Contains('/') || host.Contains(' ') || host.Contains('@'))
                return null;
            if (!string.IsNullOrWhiteSpace(s.ProxyUsername))
                return $"http://{Uri.EscapeDataString(s.ProxyUsername.Trim())}:{Uri.EscapeDataString(s.ProxyPassword ?? "")}@{host}:{port}";
            return $"http://{host}:{port}";
        }
        catch { return null; }
    }
}
