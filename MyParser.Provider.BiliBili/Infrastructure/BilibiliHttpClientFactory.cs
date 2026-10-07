using System.Net;
using Shirobot.Plugin.MyParser.Parsing;

namespace MyParser.Provider.BiliBili.Infrastructure;

public static class BilibiliHttpClientFactory
{
    public static HttpMessageHandler CreateHandler()
    {
        return SafeHttpTransport.CreateHandler(cookieContainer: new CookieContainer());
    }

    public static TimeSpan GetTimeout(PluginConfig config)
    {
        var timeout = Math.Clamp(config.RequestTimeoutSeconds, 5, 120);
        return TimeSpan.FromSeconds(timeout);
    }

    public static HttpClient Create(PluginConfig config)
    {
        return new HttpClient(CreateHandler()) { Timeout = GetTimeout(config) };
    }
}
