using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace Shirobot.Plugin.MyParser.Parsing;

/// <summary>Expands HTTP redirects in message text before a parser provider is selected.</summary>
public static partial class UrlRedirectResolver
{
    public const int MaximumRedirects = 10;

    private static readonly string[] SupportedLinkDomains =
    [
        "bilibili.com", "b23.tv", "bili2233.cn",
        "douyin.com", "iesdouyin.com",
        "xiaoheihe.cn", "heybox.cn", "maxjia.com",
        "music.163.com", "163cn.tv",
        "weixin.qq.com",
        "youtube.com", "youtu.be",
        "x.com", "twitter.com", "t.co",
    ];

    // 免跳转解析域名：解析器可直接从原始链接取作品 ID，无需先展开跳转。
    // 无谓的直连探测在被墙网络下必然失败，只会产生误导性告警并拖慢解析。
    private static readonly string[] NoRedirectDomains =
    [
        "x.com", "twitter.com",
    ];

    public static CookieContainer SharedCookies { get; } = new();
    private static readonly HttpClient Client = CreateClient();

    [GeneratedRegex(@"https?://[^\s<>""']+", RegexOptions.IgnoreCase)]
    private static partial Regex UrlRegex();

    public static async Task<string> ResolveTextAsync(string text, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text)) return text;

        var matches = UrlRegex().Matches(text).Cast<Match>().ToArray();
        foreach (var match in matches)
        {
            var rawUrl = match.Value.TrimEnd('.', ',', '，', '。', ')', '）', ']', '】');
            if (!Uri.TryCreate(rawUrl, UriKind.Absolute, out var uri)
                || !SafeHttpTransport.IsAllowedUri(uri)
                || !IsSupportedLinkHost(uri.Host)) continue;
            if (IsHostInDomains(uri.Host, NoRedirectDomains)) continue;
            try
            {
                if (!await SafeHttpTransport.IsPublicHttpTargetAsync(uri, cancellationToken).ConfigureAwait(false)) continue;
                var resolved = await ResolveAsync(uri, cancellationToken).ConfigureAwait(false);
                if (!string.Equals(resolved, rawUrl, StringComparison.Ordinal))
                    text = text.Replace(rawUrl, resolved, StringComparison.Ordinal);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or UriFormatException or SocketException)
            {
                ShiroBot.SDK.Abstractions.BotLog.Warning($"MyParser 链接跳转解析失败: url={rawUrl}, error={ex.GetType().Name}");
            }
        }

        return text;
    }

    private static async Task<string> ResolveAsync(Uri uri, CancellationToken cancellationToken)
    {
        var current = uri;
        for (var hop = 0; hop < MaximumRedirects; hop++)
        {
            if (!await SafeHttpTransport.IsPublicHttpTargetAsync(current, cancellationToken).ConfigureAwait(false))
            {
                return uri.ToString();
            }
            if (!IsSupportedLinkHost(current.Host)) return uri.ToString();

            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            using var response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            var statusCode = (int)response.StatusCode;
            if (statusCode is not (300 or 301 or 302 or 303 or 307 or 308) || response.Headers.Location is null)
            {
                return response.RequestMessage?.RequestUri?.ToString() ?? current.ToString();
            }

            var previous = current;
            current = response.Headers.Location.IsAbsoluteUri
                ? response.Headers.Location
                : new Uri(current, response.Headers.Location);
            if (!SafeHttpTransport.IsAllowedUri(current)
                || !IsSupportedLinkHost(current.Host)
                || (previous.Scheme == Uri.UriSchemeHttps && current.Scheme != Uri.UriSchemeHttps))
            {
                return uri.ToString();
            }
        }

        return current.ToString();
    }

    private static bool IsSupportedLinkHost(string host) => IsHostInDomains(host, SupportedLinkDomains);

    private static bool IsHostInDomains(string host, string[] domains) =>
        domains.Any(domain =>
            string.Equals(host, domain, StringComparison.OrdinalIgnoreCase)
            || host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase));

    private static HttpClient CreateClient()
    {
        var handler = SafeHttpTransport.CreateSocketsHandler(cookieContainer: SharedCookies);
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/125.0.0.0 Safari/537.36");
        client.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
        return client;
    }
}
