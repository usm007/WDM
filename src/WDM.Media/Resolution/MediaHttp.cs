using System;
using System.Net.Http;
using WDM.Services;

namespace WDM.Media;

/// <summary>HttpClient factory for resolution traffic: settings proxy, the
/// shared Chrome/125 UA (Cloudflare-sensitive, decision #9), sane timeouts.</summary>
internal static class MediaHttp
{
    internal const string ChromeUa = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/125.0 Safari/537.36";

    internal static HttpClient CreateClient(TimeSpan? timeout = null)
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            UseCookies = false,
            Proxy = ProxyHelper.BuildProxy(MediaSettings.Current),
            UseProxy = true,
        };
        var http = new HttpClient(handler) { Timeout = timeout ?? TimeSpan.FromSeconds(15) };
        http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", ChromeUa);
        http.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US,en;q=0.9");
        return http;
    }
}
