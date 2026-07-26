using Shirobot.Plugin.MyParser.Providers.Douyin.Infrastructure;
using Shirobot.Plugin.MyParser.Providers.Douyin.Models;
using System.Text.Json;
using System.Text.RegularExpressions;
using ShiroBot.SDK.Abstractions;
using Shirobot.Plugin.MyParser.Providers.Douyin.Abstractions;
using static Shirobot.Plugin.MyParser.Providers.Douyin.Utilities.DouyinAwemeExtractor;
using static Shirobot.Plugin.MyParser.Providers.Douyin.Utilities.DouyinCoverSelector;
using static Shirobot.Plugin.MyParser.Providers.Douyin.Infrastructure.DouyinRequestHeaders;
using static Shirobot.Plugin.MyParser.Providers.Douyin.Utilities.DouyinParseHelpers;
using static Shirobot.Plugin.MyParser.Providers.Douyin.Utilities.DouyinQueryBuilder;
using static Shirobot.Plugin.MyParser.Providers.Douyin.Utilities.DouyinUrlParser;

namespace Shirobot.Plugin.MyParser.Providers.Douyin.Impl.Services;

internal sealed class DouyinParseService(HttpClient http, IReadOnlyList<IDouyinWorkParser> workParsers)
{
    public async Task<DouyinParseResult> ParseAsync(string text, CancellationToken cancellationToken = default)
    {
        var inputUrl = ExtractDouyinUrl(text) ?? throw new DouyinParseException("未检测到抖音链接。请发送 v.douyin.com 或 douyin.com 链接。");
        var resolvedUrl = await ResolveUrlAsync(inputUrl, cancellationToken);
        var awemeId = ExtractAwemeId(resolvedUrl) ?? throw new DouyinParseException("未能从链接中提取作品 ID。可能不是公开视频/图集链接。");

        using var detail = await FetchAwemeDetailAsync(awemeId, resolvedUrl, cancellationToken);
        var result = ParseAwemeDetail(detail, awemeId, resolvedUrl);
        result = await TryApplyUserProfileAsync(result, cancellationToken);
        result = await TryApplyPublishCoverAsync(result, cancellationToken);
        return await TryApplySearchCoverAsync(result, cancellationToken);
    }

    private async Task<string> ResolveUrlAsync(string url, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        ApplyDefaultHeaders(request, DouyinConstants.HomeUrl);

        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is not null)
        {
            return MakeAbsolute(response.Headers.Location, new Uri(url)).ToString();
        }

        var finalUrl = response.RequestMessage?.RequestUri?.ToString() ?? url;
        if (ExtractAwemeId(finalUrl) is not null)
        {
            return finalUrl;
        }

        var html = await response.Content.ReadAsStringAsync(cancellationToken);
        var id = ExtractAwemeId(html);
        return id is null ? finalUrl : $"https://www.douyin.com/video/{id}";
    }

    private async Task<JsonDocument> FetchAwemeDetailAsync(string awemeId, string originalUrl, CancellationToken cancellationToken)
    {
        var referer = originalUrl.Contains("/note/", StringComparison.OrdinalIgnoreCase)
            ? $"https://www.douyin.com/note/{awemeId}"
            : $"https://www.douyin.com/video/{awemeId}";

        var query = BuildDetailQuery(awemeId);
        var unsignedUrl = "https://www.douyin.com/aweme/v1/web/aweme/detail/?" + query;
        var aBogus = ABogusSigner.Generate(query, DouyinConstants.UserAgent);
        var signedUrl = unsignedUrl + "&a_bogus=" + Uri.EscapeDataString(aBogus);

        var doc = await GetJsonAsync(signedUrl, referer, cancellationToken);
        if (TryGetAwemeDetail(doc.RootElement, out _))
        {
            return doc;
        }

        doc.Dispose();

        if (!referer.Contains("/note/", StringComparison.OrdinalIgnoreCase))
        {
            doc = await GetJsonAsync(signedUrl, $"https://www.douyin.com/note/{awemeId}", cancellationToken);
            if (TryGetAwemeDetail(doc.RootElement, out _))
            {
                return doc;
            }

            doc.Dispose();
        }

        var ssrDoc = await FetchSharePageDataAsync(awemeId, cancellationToken);
        if (TryGetAwemeDetail(ssrDoc.RootElement, out _))
        {
            return ssrDoc;
        }

        ssrDoc.Dispose();
        throw new DouyinParseException("抖音详情接口和分享页均未返回作品数据。请检查 Cookie 是否有效，或稍后重试。");
    }

    private async Task<JsonDocument> FetchSharePageDataAsync(string awemeId, CancellationToken cancellationToken)
    {
        var (_, doc) = await FetchSharePageHtmlAndDataAsync(awemeId, cancellationToken);
        return doc;
    }

    private async Task<(string Html, JsonDocument Doc)> FetchSharePageHtmlAndDataAsync(string awemeId, CancellationToken cancellationToken)
    {
        var shareUrl = $"https://www.iesdouyin.com/share/video/{awemeId}/";
        using var request = new HttpRequestMessage(HttpMethod.Get, shareUrl);
        ApplySharePageHeaders(request);

        using var response = await http.SendAsync(request, cancellationToken);
        var html = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new DouyinParseException($"抖音分享页请求失败：HTTP {(int)response.StatusCode}");
        }

        var match = Regex.Match(html, @"window\._ROUTER_DATA\s*=\s*(.*?)</script>", RegexOptions.Singleline);
        if (!match.Success)
        {
            throw new DouyinParseException("抖音分享页未找到 ROUTER_DATA。 ");
        }

        return (html, JsonDocument.Parse(match.Groups[1].Value.Trim()));
    }

    private async Task<DouyinParseResult> TryApplyUserProfileAsync(DouyinParseResult result, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(result.AuthorId) || !result.AuthorId.StartsWith("MS4w", StringComparison.Ordinal))
        {
            return result;
        }

        try
        {
            var query = BuildUserProfileQuery(result.AuthorId);
            var unsignedUrl = "https://www.douyin.com/aweme/v1/web/user/profile/other/?" + query;
            var aBogus = ABogusSigner.Generate(query, DouyinConstants.UserAgent);
            var signedUrl = unsignedUrl + "&a_bogus=" + Uri.EscapeDataString(aBogus);
            using var doc = await GetJsonAsync(signedUrl, "https://www.douyin.com/user/" + Uri.EscapeDataString(result.AuthorId), cancellationToken);
            var root = doc.RootElement;
            if (GetInt(root, "status_code") != 0 || !TryGetProperty(root, "user", out var user))
            {
                return result;
            }

            var followerCount = GetLong(user, "follower_count");
            var region = GetString(user, "ip_location") ?? GetString(user, "region");
            var avatar = ExtractAuthorAvatarUrl(user);
            BotLog.Info($"MyParser 抖音作者资料补全: aweme_id={result.AwemeId}, follower={followerCount}, region={region ?? ""}, avatar={(string.IsNullOrWhiteSpace(avatar) ? "" : "ok")}");
            return result with
            {
                AuthorFollowerCount = followerCount > 0 ? followerCount : result.AuthorFollowerCount,
                AuthorRegion = string.IsNullOrWhiteSpace(region) ? result.AuthorRegion : region,
                AuthorAvatarUrl = string.IsNullOrWhiteSpace(avatar) ? result.AuthorAvatarUrl : avatar,
            };
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or DouyinParseException or TaskCanceledException)
        {
            BotLog.Warning($"MyParser 抖音作者资料补全失败: aweme_id={result.AwemeId}, error={ex.Message}");
            return result;
        }
    }

    private async Task<DouyinParseResult> TryApplyPublishCoverAsync(DouyinParseResult result, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(result.AuthorId) || !result.AuthorId.StartsWith("MS4w", StringComparison.Ordinal))
        {
            BotLog.Info($"MyParser 抖音发布列表封面跳过: aweme_id={result.AwemeId}, reason=missing_sec_uid");
            return result;
        }

        try
        {
            var publishCover = await TryFetchPublishCoverUrlAsync(result.AuthorId, result.AwemeId, cancellationToken);
            if (string.IsNullOrWhiteSpace(publishCover))
            {
                BotLog.Info($"MyParser 抖音发布列表封面未命中: aweme_id={result.AwemeId}, sec_uid={result.AuthorId}");
                return result;
            }

            if (string.Equals(publishCover, result.CoverUrl, StringComparison.Ordinal))
            {
                BotLog.Info($"MyParser 抖音发布列表封面已是当前封面: aweme_id={result.AwemeId}, url={publishCover}");
                return result;
            }

            if (!string.IsNullOrWhiteSpace(result.CoverUrl))
            {
                BotLog.Info($"MyParser 抖音发布列表封面命中但保留详情封面: aweme_id={result.AwemeId}, keep={result.CoverUrl}, publish={publishCover}");
                return result with { CoverSource = "detail" };
            }

            BotLog.Info($"MyParser 抖音详情封面为空，使用发布列表封面: aweme_id={result.AwemeId}, new={publishCover}");
            return result with { CoverUrl = publishCover, CoverSource = "publish" };
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or DouyinParseException or TaskCanceledException)
        {
            BotLog.Warning($"MyParser 抖音发布列表封面获取失败: aweme_id={result.AwemeId}, error={ex.Message}");
            return result;
        }
    }

    private async Task<DouyinParseResult> TryApplySearchCoverAsync(DouyinParseResult result, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(result.Title))
        {
            return result;
        }

        if (!string.IsNullOrWhiteSpace(result.CoverSource) && result.CoverSource.StartsWith("publish", StringComparison.Ordinal))
        {
            BotLog.Info($"MyParser 抖音默认使用发布列表封面: aweme_id={result.AwemeId}, source={result.CoverSource}, skip_search_override=true, url={result.CoverUrl}");
            return result;
        }

        try
        {
            var searchCover = await TryFetchSearchCoverUrlAsync(result.Title, result.AwemeId, cancellationToken);
            if (string.IsNullOrWhiteSpace(searchCover))
            {
                BotLog.Info($"MyParser 抖音搜索封面未命中: aweme_id={result.AwemeId}");
                return result;
            }

            if (CoverUrlScore(searchCover) <= CoverUrlScore(result.CoverUrl))
            {
                BotLog.Info($"MyParser 抖音搜索封面未优于当前封面: aweme_id={result.AwemeId}, current_score={CoverUrlScore(result.CoverUrl)}, search_score={CoverUrlScore(searchCover)}");
                return result;
            }

            BotLog.Info($"MyParser 抖音封面使用搜索高清图: aweme_id={result.AwemeId}, old={result.CoverUrl}, new={searchCover}");
            return result with { CoverUrl = searchCover, CoverSource = "search" };
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or DouyinParseException or TaskCanceledException)
        {
            BotLog.Warning($"MyParser 抖音搜索封面获取失败: aweme_id={result.AwemeId}, error={ex.Message}");
            return result;
        }
    }

    private async Task<string?> TryFetchSearchCoverUrlAsync(string title, string awemeId, CancellationToken cancellationToken)
    {
        var keyword = BuildSearchKeyword(title);
        if (string.IsNullOrWhiteSpace(keyword))
        {
            return null;
        }

        var query = BuildSearchItemQuery(keyword);
        var unsignedUrl = "https://www.douyin.com/aweme/v1/web/search/item/?" + query;
        var aBogus = ABogusSigner.Generate(query, DouyinConstants.UserAgent);
        var signedUrl = unsignedUrl + "&a_bogus=" + Uri.EscapeDataString(aBogus);

        using var doc = await GetJsonAsync(signedUrl, "https://www.douyin.com/search/" + Uri.EscapeDataString(keyword) + "?type=video", cancellationToken);
        var root = doc.RootElement;
        var statusCode = GetInt(root, "status_code");
        if (statusCode != 0)
        {
            BotLog.Warning($"MyParser 抖音搜索封面接口异常: aweme_id={awemeId}, status_code={statusCode}, status_msg={GetString(root, "status_msg") ?? GetString(root, "message") ?? string.Empty}");
            return null;
        }

        if (!TryGetProperty(root, "data", out var data) || data.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var item in data.EnumerateArray())
        {
            if (!TryGetProperty(item, "aweme_info", out var aweme) || aweme.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            if (!string.Equals(GetString(aweme, "aweme_id"), awemeId, StringComparison.Ordinal))
            {
                continue;
            }

            var cover = ExtractSearchCoverUrl(aweme);
            BotLog.Info($"MyParser 抖音搜索封面命中: aweme_id={awemeId}, keyword={keyword}, cover={cover ?? string.Empty}");
            return cover;
        }

        return null;
    }

    private async Task<string?> TryFetchPublishCoverUrlAsync(string secUserId, string awemeId, CancellationToken cancellationToken)
    {
        long maxCursor = 0;
        for (var page = 1; page <= 3; page++)
        {
            var query = BuildUserPostQuery(secUserId, awemeId, maxCursor);
            var unsignedUrl = "https://www.douyin.com/aweme/v1/web/aweme/post/?" + query;
            var aBogus = ABogusSigner.Generate(query, DouyinConstants.UserAgent);
            var signedUrl = unsignedUrl + "&a_bogus=" + Uri.EscapeDataString(aBogus);
            var referer = $"https://www.douyin.com/user/{Uri.EscapeDataString(secUserId)}?vid={Uri.EscapeDataString(awemeId)}";

            using var doc = await GetJsonAsync(signedUrl, referer, cancellationToken);
            var root = doc.RootElement;
            var statusCode = GetInt(root, "status_code");
            if (statusCode != 0)
            {
                BotLog.Warning($"MyParser 抖音发布列表封面接口异常: aweme_id={awemeId}, page={page}, status_code={statusCode}, status_msg={GetString(root, "status_msg") ?? GetString(root, "message") ?? string.Empty}");
                return null;
            }

            if (TryGetProperty(root, "aweme_list", out var awemeList) && awemeList.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in awemeList.EnumerateArray())
                {
                    var itemId = GetString(item, "aweme_id");
                    if (!string.Equals(itemId, awemeId, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    var publishCover = ExtractPublishCoverUrl(item);
                    BotLog.Info($"MyParser 抖音发布列表封面命中: aweme_id={awemeId}, page={page}, cover={publishCover ?? string.Empty}");
                    return publishCover;
                }
            }

            var hasMore = GetBool(root, "has_more");
            var nextCursor = GetLong(root, "max_cursor");
            if (!hasMore || nextCursor <= 0 || nextCursor == maxCursor)
            {
                break;
            }

            maxCursor = nextCursor;
        }

        return null;
    }

    private async Task<JsonDocument> GetJsonAsync(string url, string referer, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        ApplyDefaultHeaders(request, referer);
        if (!string.IsNullOrWhiteSpace(MyParserRuntime.DouyinCookie))
        {
            request.Headers.TryAddWithoutValidation("Cookie", MyParserRuntime.DouyinCookie);
        }

        using var response = await http.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new DouyinParseException($"抖音接口请求失败：HTTP {(int)response.StatusCode}");
        }

        try
        {
            return JsonDocument.Parse(body);
        }
        catch (JsonException ex)
        {
            throw new DouyinParseException("抖音接口返回的不是有效 JSON：" + ex.Message);
        }
    }

    private DouyinParseResult ParseAwemeDetail(JsonDocument doc, string fallbackAwemeId, string sourceUrl)
    {
        if (!TryGetAwemeDetail(doc.RootElement, out var aweme))
        {
            throw new DouyinParseException("响应中缺少 aweme_detail。 ");
        }

        var parser = workParsers.FirstOrDefault(i => i.CanParse(aweme))
            ?? throw new DouyinParseException("暂不支持的抖音作品类型。");
        var result = parser.Parse(aweme, fallbackAwemeId, sourceUrl);
        BotLog.Info($"MyParser 抖音作品类型解析: aweme_id={result.AwemeId}, parser={parser.GetType().Name}, is_video={result.IsVideo}, is_gallery={result.IsGallery}");
        return result;
    }

    private static string BuildSearchKeyword(string title)
    {
        var keyword = Regex.Replace(title.ReplaceLineEndings(" "), @"#\S+", " ").Trim();
        keyword = Regex.Replace(keyword, @"\s+", " ");
        return keyword.Length <= 80 ? keyword : keyword[..80];
    }
}
