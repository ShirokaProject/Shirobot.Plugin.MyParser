using System.Net;
using System.Text;
using System.Text.Json;
using MyParser.Provider.WeixinChannels.Infrastructure;
using MyParser.Provider.WeixinChannels.Models;
using MyParser.Provider.WeixinChannels.Utilities;

namespace MyParser.Provider.WeixinChannels.Parsing;

public sealed class WeixinChannelsParser : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly PluginConfig _config;
    private readonly HttpClient _http;

    public WeixinChannelsParser(PluginConfig config)
    {
        _config = config;
        _http = new HttpClient(new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            AllowAutoRedirect = true,
        })
        {
            Timeout = TimeSpan.FromSeconds(Math.Max(5, config.RequestTimeoutSeconds)),
        };
    }

    public static bool ContainsWeixinChannelsUrl(string text) => WeixinChannelsUrlParser.ContainsWeixinChannelsUrl(text);

    public async Task<WeixinChannelsParseResult> ParseAsync(string text, CancellationToken cancellationToken = default)
    {
        if (!WeixinChannelsUrlParser.TryExtractShareUrl(text, out var shareUrl))
        {
            throw new WeixinChannelsParseException("无法从输入中提取微信视频号分享链接。");
        }

        var yuanbaoCookie = GetYuanbaoCookie();
        if (string.IsNullOrWhiteSpace(yuanbaoCookie))
        {
            throw new WeixinChannelsParseException("未配置腾讯元宝 Cookie，无法调用元宝接口解析视频号分享链接。请私信机器人发送 #wx-cookie <Cookie> 写入。 ");
        }

        var sphId = WeixinChannelsUrlParser.ExtractSphId(shareUrl);
        var parse = await ParseShareUrlAsync(shareUrl, yuanbaoCookie, cancellationToken).ConfigureAwait(false);
        var playableUrl = parse.Data?.PlayableUrl;
        var token = string.Empty;
        var exportId = string.Empty;
        if (!string.IsNullOrWhiteSpace(playableUrl) && Uri.TryCreate(playableUrl, UriKind.Absolute, out var playableUri))
        {
            token = playableUri.QueryValue("token");
            // get_feed_info 需要 finder-preview playable_url 里的 eid；wx_export_id 有时不是这个接口可用的 exportId。
            exportId = playableUri.QueryValue("eid");
        }

        exportId = FirstNonEmpty(exportId, parse.Data?.WxExportId) ?? string.Empty;

        if (string.IsNullOrWhiteSpace(exportId))
        {
            throw new WeixinChannelsParseException("元宝接口没有返回 exportId，无法继续获取视频详情。");
        }

        if (string.IsNullOrWhiteSpace(token))
        {
            throw new WeixinChannelsParseException("元宝接口没有返回 generalToken，无法继续获取视频详情。");
        }

        var feed = await GetFeedInfoAsync(exportId, token, cancellationToken).ConfigureAwait(false);
        if (feed.ErrCode != 0)
        {
            throw new WeixinChannelsParseException($"微信视频号接口返回错误：{feed.ErrCode} {feed.ErrMsg}");
        }

        var info = feed.Data?.FeedInfo ?? throw new WeixinChannelsParseException(
            $"微信视频号接口没有返回 feedInfo。errCode={feed.ErrCode}, errMsg={feed.ErrMsg ?? string.Empty}, exportId={exportId}, token={(string.IsNullOrWhiteSpace(token) ? "empty" : "ok")}");
        var author = feed.Data?.AuthorInfo;
        var videoUrl = FirstNonEmpty(info.H265VideoInfo?.VideoUrl, info.H264VideoInfo?.VideoUrl, info.VideoUrl);
        if (string.IsNullOrWhiteSpace(videoUrl))
        {
            throw new WeixinChannelsParseException("微信视频号接口没有返回可下载视频地址。");
        }

        return new WeixinChannelsParseResult
        {
            ShareUrl = shareUrl,
            SphId = sphId,
            ExportId = exportId,
            Title = FirstNonEmpty(info.Description, parse.Data?.Desc),
            Description = FirstNonEmpty(info.Description, parse.Data?.Desc),
            AuthorName = FirstNonEmpty(author?.Nickname, parse.Data?.Author),
            AuthorAvatarUrl = FirstNonEmpty(author?.HeadImgUrl, parse.Data?.AuthorIcon),
            CoverUrl = FirstNonEmpty(info.CoverUrl, parse.Data?.CoverUrl),
            VideoUrl = videoUrl,
            H264VideoUrl = info.H264VideoInfo?.VideoUrl,
            H265VideoUrl = info.H265VideoInfo?.VideoUrl,
            OriginVideoUrl = CleanVideoUrl(videoUrl),
            DurationSeconds = info.VideoPlayLen ?? 0,
            FileSize = info.FileSize,
            PublishTime = info.CreateTime > 0 ? DateTimeOffset.FromUnixTimeSeconds(info.CreateTime) : null,
            LikeCountText = info.LikeCountFmt,
            FavoriteCountText = info.FavCountFmt,
            ForwardCountText = info.ForwardCountFmt,
            CommentCountText = info.CommentCountFmt,
        };
    }

    private async Task<WeixinChannelsParseResponse> ParseShareUrlAsync(string shareUrl, string cookie, CancellationToken cancellationToken)
    {
        var body = JsonSerializer.Serialize(new { type = "video_channel_url", url = shareUrl, scene = 1 });
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://yuanbao.tencent.com/api/weixin/get_parse_result")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        ApplyYuanbaoHeaders(request, cookie);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        var responseText = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new WeixinChannelsParseException($"元宝接口请求失败：{(int)response.StatusCode} {response.ReasonPhrase} {ProviderTextUtilities.TrimLine(responseText, 160)}");
        }

        var result = JsonSerializer.Deserialize<WeixinChannelsParseResponse>(responseText, JsonOptions)
                     ?? throw new WeixinChannelsParseException("元宝接口返回空响应。");
        if (result.Code != 0)
        {
            throw new WeixinChannelsParseException($"元宝接口返回错误：{result.Code} {result.Msg}");
        }

        return result;
    }

    private async Task<WeixinChannelsFeedResponse> GetFeedInfoAsync(string exportId, string generalToken, CancellationToken cancellationToken)
    {
        var rid = GenerateRid();
        var pageUrl = "https:%2F%2Fchannels.weixin.qq.com%2Ffinder-preview%2Fpages%2Ffeed";
        var apiUrl = $"https://channels.weixin.qq.com/finder-preview/api/feed/get_feed_info?_rid={Uri.EscapeDataString(rid)}&_pageUrl={pageUrl}";
        var body = JsonSerializer.Serialize(new
        {
            baseReq = new { generalToken },
            exportId,
        });
        using var request = new HttpRequestMessage(HttpMethod.Post, apiUrl)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        ApplyChannelsHeaders(request, exportId, generalToken);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        var responseText = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new WeixinChannelsParseException($"微信视频号接口请求失败：{(int)response.StatusCode} {response.ReasonPhrase} {ProviderTextUtilities.TrimLine(responseText, 160)}");
        }

        return JsonSerializer.Deserialize<WeixinChannelsFeedResponse>(responseText, JsonOptions)
               ?? throw new WeixinChannelsParseException("微信视频号接口返回空响应。");
    }

    private string GetYuanbaoCookie()
    {
        return MyParserRuntime.WeixinChannelsYuanbaoCookie;
    }

    public static bool LooksLikeYuanbaoCookie(string cookie)
    {
        return !string.IsNullOrWhiteSpace(cookie)
               && cookie.Contains('=')
               && (cookie.Contains(';') || cookie.Contains("hy_user", StringComparison.OrdinalIgnoreCase) || cookie.Contains("t_uid", StringComparison.OrdinalIgnoreCase) || cookie.Contains("uid", StringComparison.OrdinalIgnoreCase));
    }

    private static void ApplyYuanbaoHeaders(HttpRequestMessage request, string cookie)
    {
        request.Headers.TryAddWithoutValidation("Accept", "application/json, text/plain, */*");
        request.Headers.TryAddWithoutValidation("Accept-Language", "zh-CN,zh;q=0.9,en;q=0.8");
        request.Headers.TryAddWithoutValidation("Origin", WeixinChannelsConstants.YuanbaoOrigin);
        request.Headers.TryAddWithoutValidation("Referer", "https://yuanbao.tencent.com/");
        request.Headers.TryAddWithoutValidation("User-Agent", WeixinChannelsConstants.UserAgent);
        request.Headers.TryAddWithoutValidation("x-source", "web");
        request.Headers.TryAddWithoutValidation("x-web-third-source", "main");
        request.Headers.TryAddWithoutValidation("x-requested-with", "XMLHttpRequest");
        request.Headers.TryAddWithoutValidation("x-language", "zh-CN");
        request.Headers.TryAddWithoutValidation("Cookie", cookie);
    }

    private static void ApplyChannelsHeaders(HttpRequestMessage request, string exportId, string generalToken)
    {
        request.Headers.TryAddWithoutValidation("Accept", "application/json, text/plain, */*");
        request.Headers.TryAddWithoutValidation("Accept-Language", "zh-CN,zh;q=0.9,en;q=0.8");
        request.Headers.TryAddWithoutValidation("Origin", WeixinChannelsConstants.Origin);
        request.Headers.TryAddWithoutValidation("Referer", $"https://channels.weixin.qq.com/finder-preview/pages/feed?entry_card_type=48&comment_scene=39&appid=0&token={Uri.EscapeDataString(generalToken)}&entry_scene=0&eid={Uri.EscapeDataString(exportId)}");
        request.Headers.TryAddWithoutValidation("User-Agent", WeixinChannelsConstants.UserAgent);
    }

    internal static HttpRequestMessage CreateVideoRequest(HttpMethod method, string url, WeixinChannelsParseResult result, string? range)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.TryAddWithoutValidation("User-Agent", WeixinChannelsConstants.UserAgent);
        request.Headers.TryAddWithoutValidation("Referer", result.ShareUrl);
        request.Headers.TryAddWithoutValidation("Accept", "video/webm,video/mp4,video/*;q=0.9,*/*;q=0.8");
        if (!string.IsNullOrWhiteSpace(range))
        {
            request.Headers.TryAddWithoutValidation("Range", range);
        }

        return request;
    }

    private static string GenerateRid()
    {
        return DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString("x") + "-" + Random.Shared.Next(0, int.MaxValue).ToString("x8");
    }

    private static string? FirstNonEmpty(params string?[] values)
    {
        return values.FirstOrDefault(i => !string.IsNullOrWhiteSpace(i));
    }

    private static string? CleanVideoUrl(string? videoUrl)
    {
        if (string.IsNullOrWhiteSpace(videoUrl) || !Uri.TryCreate(videoUrl, UriKind.Absolute, out var uri))
        {
            return null;
        }

        var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
        var encFileKey = query.Get("encfilekey");
        var token = query.Get("token");
        if (string.IsNullOrWhiteSpace(encFileKey) || string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        return uri.GetLeftPart(UriPartial.Path) + "?encfilekey=" + Uri.EscapeDataString(encFileKey) + "&token=" + Uri.EscapeDataString(token);
    }

    public void Dispose() => _http.Dispose();
}

file static class UriExtensions
{
    public static string QueryValue(this Uri uri, string key)
    {
        return System.Web.HttpUtility.ParseQueryString(uri.Query).Get(key) ?? string.Empty;
    }
}
