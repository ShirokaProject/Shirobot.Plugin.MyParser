using System.Net;
using Shirobot.Plugin.MyParser.Utility;

namespace MyParser.Provider.X.Infrastructure;

public static class XHttpClientFactory
{
    private const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0 Safari/537.36";

    public static HttpClient Create(PluginConfig config)
    {
        var proxy = HttpProxySettings.Create(config.HttpProxy);
        var handler = proxy is not null
            ? (HttpMessageHandler)new HttpClientHandler
            {
                Proxy = proxy,
                UseProxy = true,
                AutomaticDecompression = DecompressionMethods.All,
            }
            : SafeHttpTransport.CreateHandler();

        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(Math.Clamp(config.RequestTimeoutSeconds, 5, 60)) };
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", UserAgent);
        client.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/json,text/plain,text/html,*/*");
        return client;
    }
}
