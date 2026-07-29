using System.Net;

namespace MyParser.Provider.Douyin.Infrastructure;

public static class DouyinHttpClientFactory
{
    public static HttpClient Create(PluginConfig config)
    {
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            // Redirects are followed by DouyinParseService so every hop's Set-Cookie is observable.
            AllowAutoRedirect = false,
            UseCookies = true,
            CookieContainer = new CookieContainer(),
        };
        var timeout = Math.Clamp(config.RequestTimeoutSeconds, 5, 60);
        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(timeout) };
    }

}
