using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;

namespace WDM.Services;

/// <summary>SSRF guard: decides whether a URL points at non-public space
/// (loopback, LAN, link-local, multicast, cloud metadata, DNS-rebinding).
/// Moved verbatim from CaptureServer — the allow/block policy is a security
/// boundary shared by capture, embed resolving, title sync and the engine.
/// Do not loosen without review.</summary>
public static class NetworkGuard
{
    public static bool IsBlockedResolveTarget(string url)
    {
        try
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || string.IsNullOrWhiteSpace(uri.Host))
                return true;
            if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
                return true;
            string host = uri.Host.Trim().Trim('.').ToLowerInvariant();
            if (host == "localhost" || host.EndsWith(".local", StringComparison.Ordinal) ||
                host.EndsWith(".localhost", StringComparison.Ordinal) || host == "metadata.google.internal")
                return true;
            if (IPAddress.TryParse(host.Trim('[', ']'), out var ip))
            {
                // IPv4-mapped IPv6 (::ffff:127.0.0.1) must be judged as the
                // IPv4 it routes to — otherwise the v6 branch misses it.
                if (ip.IsIPv4MappedToIPv6)
                {
                    try { ip = ip.MapToIPv4(); }
                    catch { return true; }
                }
                if (IPAddress.IsLoopback(ip))
                    return true;
                if (ip.AddressFamily == AddressFamily.InterNetwork)
                {
                    byte[] b = ip.GetAddressBytes();
                    if (b[0] == 10) return true;
                    if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return true;
                    if (b[0] == 192 && b[1] == 168) return true;
                    if (b[0] == 169 && b[1] == 254) return true;
                    if (b[0] == 0 || b[0] >= 224) return true;
                }
                else
                {
                    byte[] b = ip.GetAddressBytes();
                    // Unspecified :: (all zeros) is not publicly routable — block.
                    if (b.Length == 16 && b.All(x => x == 0)) return true;
                    // Unique-local fc00::/7 (not covered by the obsolete
                    // SiteLocal flag), link-local fe80::/10, multicast ff00::/8.
                    if (b.Length == 16 && (b[0] & 0xFE) == 0xFC) return true;
                    if (ip.IsIPv6SiteLocal || ip.IsIPv6LinkLocal || ip.IsIPv6Multicast)
                        return true;
                }
            }
            else if (LooksLikeNumericIp(host))
            {
                // Non-canonical IPv4 the parser rejects but stacks still route
                // (0x7f.1, 2130706433, 0177.0.0.1): fail closed. Plain hostnames
                // contain letters/hyphens and never match this shape.
                return true;
            }
            else if (ResolvesToBlockedAddress(host))
            {
                // DNS-rebinding guard: a public hostname that resolves to
                // loopback/LAN/link-local space is blocked even though the
                // literal string looks innocent. DNS failures fail open here
                // (the downstream fetch will fail on its own).
                return true;
            }
            return false;
        }
        catch
        {
            return true;
        }
    }

    /// <summary>True when a hostname resolves to a blocked (non-public) address.
    /// Best-effort, fails open on DNS errors. Results are cached 5 minutes so
    /// per-redirect/per-hop checks don't pay a lookup each time.</summary>
    private static readonly object _dnsCacheLock = new();
    private static readonly Dictionary<string, (bool blocked, long tick)> _dnsCache = new(StringComparer.OrdinalIgnoreCase);
    private static bool ResolvesToBlockedAddress(string host)
    {
        lock (_dnsCacheLock)
        {
            if (_dnsCache.TryGetValue(host, out var e) && Environment.TickCount64 - e.tick < 5 * 60 * 1000)
                return e.blocked;
        }
        bool blocked = ResolvesToBlockedAddressSlow(host);
        lock (_dnsCacheLock)
        {
            if (_dnsCache.Count > 512)
                _dnsCache.Clear();
            _dnsCache[host] = (blocked, Environment.TickCount64);
        }
        return blocked;
    }

    private static bool ResolvesToBlockedAddressSlow(string host)
    {
        try
        {
            var addrs = Dns.GetHostAddresses(host);
            foreach (var a in addrs)
            {
                var ip = a;
                if (ip.IsIPv4MappedToIPv6)
                {
                    try { ip = ip.MapToIPv4(); }
                    catch { return true; }
                }
                if (IPAddress.IsLoopback(ip))
                    return true;
                if (ip.AddressFamily == AddressFamily.InterNetwork)
                {
                    byte[] b = ip.GetAddressBytes();
                    if (b[0] == 10) return true;
                    if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return true;
                    if (b[0] == 192 && b[1] == 168) return true;
                    if (b[0] == 169 && b[1] == 254) return true;
                    if (b[0] == 0 || b[0] >= 224) return true;
                }
                else if (ip.AddressFamily == AddressFamily.InterNetworkV6)
                {
                    byte[] b = ip.GetAddressBytes();
                    if (b.Length == 16 && b.All(x => x == 0)) return true;
                    if (b.Length == 16 && (b[0] & 0xFE) == 0xFC) return true;
                    if (ip.IsIPv6SiteLocal || ip.IsIPv6LinkLocal || ip.IsIPv6Multicast)
                        return true;
                }
            }
            return false;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>True for all-digit/dotted/hex IP spellings: dotted quads (incl.
    /// leading-zero octal), short forms Windows still routes (127.1, 10.1),
    /// bare decimal integers, and 0x-hex forms (incl. per-part 0x).</summary>
    private static bool LooksLikeNumericIp(string host)
    {
        string h = host.Trim('[', ']').ToLowerInvariant();
        if (string.IsNullOrEmpty(h))
            return false;
        if (h.StartsWith("0x", StringComparison.Ordinal))
            return h.Skip(2).All(c => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || c == '.' || c == ':' || c == 'x');
        if (h.All(char.IsDigit))
            return h.Length > 0;
        string[] parts = h.Split('.');
        // 1-4 dot-separated numeric-ish parts (decimal, leading-zero octal,
        // or 0x-hex): stacks route "127.1" and "0xc0.0xa8.1.1" to addresses.
        // Real hostnames contain letters (beyond a-f-only hex lookalikes with
        // an explicit 0x prefix) or hyphens and never match this shape.
        if (parts.Length >= 1 && parts.Length <= 4 && parts.All(IsNumericIpPart))
            return true;
        return false;
    }

    private static bool IsNumericIpPart(string p)
    {
        if (string.IsNullOrEmpty(p) || p.Length > 10)
            return false;
        if (p.StartsWith("0x", StringComparison.Ordinal))
            return p.Length > 2 && p.Skip(2).All(c => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'));
        return p.All(char.IsDigit);
    }
}
