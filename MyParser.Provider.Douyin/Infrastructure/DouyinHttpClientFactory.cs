using System.Net;
using Shirobot.Plugin.MyParser.Parsing;

namespace MyParser.Provider.Douyin.Infrastructure;

public static class DouyinHttpClientFactory
{
    public static HttpClient Create(PluginConfig config)
    {
        var handler = SafeHttpTransport.CreateHandler(cookieContainer: UrlRedirectResolver.SharedCookies);
        var timeout = Math.Clamp(config.RequestTimeoutSeconds, 5, 60);
        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(timeout) };
    }

}
