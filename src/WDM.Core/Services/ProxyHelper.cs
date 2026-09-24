using System;
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
            return proxy;
        }
        catch { return null; }
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
