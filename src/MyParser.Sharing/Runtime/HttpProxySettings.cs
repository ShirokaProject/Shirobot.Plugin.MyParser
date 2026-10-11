using System.Net;
using Shirobot.Plugin.MyParser.Parsing;

namespace Shirobot.Plugin.MyParser.Utility;

public static class HttpProxySettings
{
    public static WebProxy? Create(string? address)
    {
        if (string.IsNullOrWhiteSpace(address)) return null;
        if (!Uri.TryCreate(address.Trim(), UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https")
            || string.IsNullOrWhiteSpace(uri.Host))
            throw new ArgumentException("http_proxy 必须是有效的 http:// 或 https:// 代理地址。");

        return new WebProxy(uri);
    }

    public static HttpMessageHandler CreateHandler(
        string? address,
        DecompressionMethods decompression = DecompressionMethods.All,
        bool allowAutoRedirect = true)
    {
        var proxy = Create(address);
        if (proxy is not null)
        {
            return new HttpClientHandler
            {
                Proxy = proxy,
                UseProxy = true,
                AutomaticDecompression = decompression,
                AllowAutoRedirect = allowAutoRedirect,
            };
        }

        return SafeHttpTransport.CreateHandler(decompression);
    }
}
