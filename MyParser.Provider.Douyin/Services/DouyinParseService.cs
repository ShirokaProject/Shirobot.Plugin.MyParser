using MyParser.Provider.Douyin.Infrastructure;
using MyParser.Provider.Douyin.Models;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ShiroBot.SDK.Abstractions;
using MyParser.Provider.Douyin.Abstractions;
using static MyParser.Provider.Douyin.Utilities.DouyinAwemeExtractor;
using static MyParser.Provider.Douyin.Utilities.DouyinCoverSelector;
using static MyParser.Provider.Douyin.Infrastructure.DouyinRequestHeaders;
using static MyParser.Provider.Douyin.Utilities.DouyinParseHelpers;
using static MyParser.Provider.Douyin.Utilities.DouyinQueryBuilder;
using static MyParser.Provider.Douyin.Utilities.DouyinUrlParser;

namespace MyParser.Provider.Douyin.Services;

public sealed class DouyinParseService(HttpClient http, IReadOnlyList<IDouyinWorkParser> workParsers, PluginConfig config)
{
    private readonly DouyinMsTokenProvider _msTokenProvider = new(http);
    private readonly DouyinGuestSession _guestSession = new(MyParserRuntime.DouyinCookie);
    public async Task<DouyinParseResult> ParseAsync(string text, CancellationToken cancellationToken = default)
    {
        var entryStopwatch = System.Diagnostics.Stopwatch.StartNew();
        var inputUrl = ExtractDouyinUrl(text) ?? throw new DouyinParseException("未检测到抖音链接。请发送 v.douyin.com 或 douyin.com 链接。");
        var resolvedUrl = await ResolveUrlAsync(inputUrl, cancellationToken);
        BotLog.Info($"MyParser 抖音入口短链展开完成: endpoint={SafeEndpoint(resolvedUrl)}, elapsed_ms={entryStopwatch.ElapsedMilliseconds}");
        if (IsLiveUrl(resolvedUrl))
        {
            return DouyinParseResult.IgnoredLive(resolvedUrl);
        }

        var awemeId = ExtractAwemeId(resolvedUrl) ?? throw new DouyinParseException("未能从链接中提取作品 ID。可能不是公开视频/图集链接。");

        JsonDocument detail;
        var phases = new List<string>();
        try
        {
            detail = await FetchSharePageDataAsync(awemeId, resolvedUrl, cancellationToken);
            phases.Add("share=ok");
        }
        catch (DouyinParseException ex)
        {
            phases.Add("share=" + ex.Message);
            await _guestSession.EnsureRegisteredAsync(http, cancellationToken);
            try
            {
                detail = await FetchSharePageDataAsync(awemeId, resolvedUrl, cancellationToken);
                phases.Add("guest-share=ok");
            }
            catch (DouyinParseException guestEx)
            {
                phases.Add("guest-share=" + guestEx.Message);
                detail = await FetchAwemeDetailAsync(awemeId, resolvedUrl, phases, cancellationToken);
            }
        }
        using (detail)
        {
            BotLog.Info($"MyParser 抖音入口详情获取完成: aweme_id={awemeId}, phases={string.Join(",", phases)}, elapsed_ms={entryStopwatch.ElapsedMilliseconds}");
            var result = ParseAwemeDetail(detail, awemeId, resolvedUrl);
            result = await TryApplyUserProfileAsync(result, cancellationToken);
            result = await TryApplyPublishCoverAsync(result, cancellationToken);
            result = await TryApplySearchCoverAsync(result, cancellationToken);
            return await TryApplyCommentsAsync(result, cancellationToken);
        }
    }

    private static DouyinParseResult MergeGalleryMediaDetail(
        DouyinParseResult original,
        DouyinParseResult enriched,
        string source)
    {
        var images = enriched.Images.Count > 0 ? enriched.Images : original.Images;
        var livePhotoCount = images.Count(image => !string.IsNullOrWhiteSpace(image.LivePhotoUrl));
        BotLog.Info($"MyParser 抖音图文媒体详情补全: aweme_id={original.AwemeId}, source={source}, live_photos={livePhotoCount}, music={!string.IsNullOrWhiteSpace(enriched.MusicUrl)}");
        return original with
        {
            Images = images,
            MusicUrl = enriched.MusicUrl ?? original.MusicUrl,
            MusicTitle = enriched.MusicTitle ?? original.MusicTitle,
            MusicAuthor = enriched.MusicAuthor ?? original.MusicAuthor,
            DurationMilliseconds = enriched.DurationMilliseconds > 0
                ? enriched.DurationMilliseconds
                : original.DurationMilliseconds,
        };
    }

    private async Task<DouyinParseResult> TryApplyCommentsAsync(
        DouyinParseResult result,
        CancellationToken cancellationToken)
    {
        var requestedCount = Math.Clamp(config.DouyinCommentCount, 0, 50);
        var hasCookie = !string.IsNullOrWhiteSpace(MyParserRuntime.DouyinCookie);
        BotLog.Info($"MyParser 抖音评论解析检查: aweme_id={result.AwemeId}, enabled={config.DouyinFetchComments}, requested={requestedCount}, cookie_present={hasCookie}");
        if (!config.DouyinFetchComments)
        {
            BotLog.Info($"MyParser 抖音评论解析跳过: aweme_id={result.AwemeId}, reason=disabled");
            return result;
        }

        if (requestedCount == 0)
        {
            BotLog.Info($"MyParser 抖音评论解析跳过: aweme_id={result.AwemeId}, reason=count_zero");
            return result;
        }

        if (!hasCookie)
        {
            BotLog.Info($"MyParser 抖音评论解析跳过: aweme_id={result.AwemeId}, reason=missing_cookie");
            return result;
        }

        try
        {
            var commentService = new DouyinCommentService(http, _guestSession, _msTokenProvider);
            var comments = await commentService.FetchAsync(result, requestedCount, cancellationToken);
            BotLog.Info($"MyParser 抖音评论解析完成: aweme_id={result.AwemeId}, requested={requestedCount}, parsed={comments.Count}, with_images={comments.Count(comment => comment.ImageUrls.Count > 0)}, total_images={comments.Sum(comment => comment.ImageUrls.Count)}");
            return result with { Comments = comments };
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested
                                   && ex is HttpRequestException or IOException or JsonException or DouyinParseException or TaskCanceledException)
        {
            BotLog.Warning($"MyParser 抖音评论获取失败，继续发送作品: aweme_id={result.AwemeId}, error={ex.Message}");
            return result;
        }
    }

    private static bool IsLiveUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return false;
        }

        return string.Equals(uri.Host, "live.douyin.com", StringComparison.OrdinalIgnoreCase)
               || string.Equals(uri.Host, "webcast.amemv.com", StringComparison.OrdinalIgnoreCase)
               || uri.AbsolutePath.Contains("/webcast/", StringComparison.OrdinalIgnoreCase)
               || uri.AbsolutePath.Contains("/douyin/webcast/", StringComparison.OrdinalIgnoreCase)
               || uri.Query.Contains("enter_from=live", StringComparison.OrdinalIgnoreCase)
               || uri.Query.Contains("share_previous_page=live", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<string> ResolveUrlAsync(string url, CancellationToken cancellationToken)
    {
        var nextUrl = url;
        HttpResponseMessage? response = null;
        for (var hop = 0; hop < 10; hop++)
        {
            response?.Dispose();
            using var request = new HttpRequestMessage(HttpMethod.Get, nextUrl);
            ApplyDefaultHeaders(request, DouyinConstants.HomeUrl);
            AddGuestCookies(request);
            response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            _guestSession.Capture(response);
            if ((int)response.StatusCode is < 300 or >= 400 || response.Headers.Location is null) break;
            nextUrl = MakeAbsolute(response.Headers.Location, new Uri(nextUrl)).ToString();
            if (ExtractAwemeId(nextUrl) is not null)
            {
                return nextUrl;
            }
        }
        using (response)
        {
        var finalUrl = response?.RequestMessage?.RequestUri?.ToString() ?? nextUrl;
        if (ExtractAwemeId(finalUrl) is not null)
        {
            return finalUrl;
        }

        var html = await response!.Content.ReadAsStringAsync(cancellationToken);
        _guestSession.CaptureHtml(html);
        var id = ExtractAwemeId(html);
        return id is null ? finalUrl : $"https://www.douyin.com/video/{id}";
    }
    }

    private async Task<JsonDocument> FetchAwemeDetailAsync(string awemeId, string originalUrl, List<string> phases, CancellationToken cancellationToken)
    {
        var referer = originalUrl.Contains("/note/", StringComparison.OrdinalIgnoreCase)
            ? $"https://www.douyin.com/note/{awemeId}"
            : $"https://www.douyin.com/video/{awemeId}";

        var ttwid = _guestSession.Get("ttwid") ?? TryGetCookieValue("ttwid");
        var msToken = TryGetCookieValue("msToken")
                      ?? await _msTokenProvider.GetAsync(ttwid, cancellationToken);
        _guestSession.Set("msToken", msToken);
        var verifyFp = TryGetCookieValue("s_v_web_id") ?? TryGetCookieValue("verifyFp") ?? TryGetCookieValue("fp");
        if (string.IsNullOrWhiteSpace(msToken))
        {
            BotLog.Warning($"MyParser 抖音动态 msToken 不可用，使用随机回退: has_ttwid={!string.IsNullOrWhiteSpace(ttwid)}, has_verifyFp={!string.IsNullOrWhiteSpace(verifyFp)}");
        }

        var query = BuildHjDetailQuery(
            awemeId,
            msToken,
            await GetWebIdAsync(referer, cancellationToken) ?? _guestSession.Get("user_unique_id") ?? _guestSession.Get("webid") ?? GenerateNumericWebId(),
            verifyFp ?? _guestSession.Get("s_v_web_id") ?? _guestSession.Get("verifyFp"));
        var unsignedUrl = "https://www.douyin.com/aweme/v1/web/aweme/detail/?" + query;
        var aBogus = ABogusSigner.Sign(query, DouyinConstants.UserAgent);
        var signedUrl = unsignedUrl + "&a_bogus=" + Uri.EscapeDataString(aBogus);
        var hjSignedUrl = signedUrl.Replace("https://www.douyin.com/", "https://www-hj.douyin.com/", StringComparison.OrdinalIgnoreCase);

        var doc = await TryGetAwemeDetailJsonAsync(signedUrl, referer, awemeId, "detail-video", cancellationToken);
        if (doc is not null)
        {
            return doc;
        }

        doc = await TryGetAwemeDetailJsonAsync(hjSignedUrl, referer, awemeId, "detail-video-hj", cancellationToken);
        if (doc is not null)
        {
            return doc;
        }

        if (!referer.Contains("/note/", StringComparison.OrdinalIgnoreCase))
        {
            var noteReferer = $"https://www.douyin.com/note/{awemeId}";
            doc = await TryGetAwemeDetailJsonAsync(signedUrl, noteReferer, awemeId, "detail-note", cancellationToken);
            if (doc is not null)
            {
                return doc;
            }

            doc = await TryGetAwemeDetailJsonAsync(hjSignedUrl, noteReferer, awemeId, "detail-note-hj", cancellationToken);
            if (doc is not null)
            {
                return doc;
            }
        }

        try
        {
            var ssrDoc = await FetchSharePageDataAsync(awemeId, originalUrl, cancellationToken);
            if (TryGetAwemeDetail(ssrDoc.RootElement, out _))
            {
                return ssrDoc;
            }

            ssrDoc.Dispose();
        }
        catch (DouyinParseException ex)
        {
            BotLog.Warning($"MyParser 抖音分享页备用解析失败: aweme_id={awemeId}, error={ex.Message}");
        }

        throw new DouyinParseException("抖音游客解析失败：" + string.Join("; ", phases) + "; detail=未返回作品数据。请稍后重试或配置有效 DouyinCookie。");
    }

    private async Task<JsonDocument?> TryGetAwemeDetailJsonAsync(string signedUrl, string referer, string awemeId, string source, CancellationToken cancellationToken)
    {
        try
        {
            var doc = await GetJsonAsync(signedUrl, referer, cancellationToken);
            if (TryGetAwemeDetail(doc.RootElement, out _))
            {
                return doc;
            }

            BotLog.Warning($"MyParser 抖音详情接口响应缺少作品数据: aweme_id={awemeId}, source={source}");
            doc.Dispose();
        }
        catch (DouyinParseException ex)
        {
            BotLog.Warning($"MyParser 抖音详情接口失败，尝试备用解析: aweme_id={awemeId}, source={source}, error={ex.Message}");
        }

        return null;
    }

    private async Task<JsonDocument> FetchSharePageDataAsync(string awemeId, string originalUrl, CancellationToken cancellationToken)
    {
        var (_, doc) = await FetchSharePageHtmlAndDataAsync(awemeId, originalUrl, cancellationToken);
        return doc;
    }

    private async Task<(string Html, JsonDocument Doc)> FetchSharePageHtmlAndDataAsync(string awemeId, string originalUrl, CancellationToken cancellationToken)
    {
        var videoShareUrl = $"https://www.iesdouyin.com/share/video/{awemeId}/";
        var noteShareUrl = $"https://www.iesdouyin.com/share/note/{awemeId}/";
        var urls = (originalUrl.Contains("/note/", StringComparison.OrdinalIgnoreCase)
                ? new[] { noteShareUrl, videoShareUrl, originalUrl }
                : new[] { videoShareUrl, noteShareUrl, originalUrl })
            .Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var shareUrl in urls)
        {
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            using var request = new HttpRequestMessage(HttpMethod.Get, shareUrl);
            ApplySharePageHeaders(request);
            AddGuestCookies(request);
            using var response = await http.SendAsync(request, cancellationToken);
            _guestSession.Capture(response);
            var html = await response.Content.ReadAsStringAsync(cancellationToken);
            _guestSession.CaptureHtml(html);
            if (response.IsSuccessStatusCode && TryExtractShareAweme(html, awemeId, out var doc))
            {
                BotLog.Info($"MyParser 抖音分享页命中: aweme_id={awemeId}, endpoint={SafeEndpoint(shareUrl)}, elapsed_ms={stopwatch.ElapsedMilliseconds}");
                return (html, doc);
            }

            BotLog.Info($"MyParser 抖音分享页未命中: aweme_id={awemeId}, endpoint={SafeEndpoint(shareUrl)}, http={(int)response.StatusCode}, elapsed_ms={stopwatch.ElapsedMilliseconds}");
        }
        throw new DouyinParseException("分享页未命中作品数据");
    }

    private static bool TryFindAwemeDetail(JsonElement value, out JsonElement aweme)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in value.EnumerateObject())
            {
                if ((string.Equals(property.Name, "aweme_detail", StringComparison.OrdinalIgnoreCase) || string.Equals(property.Name, "awemeDetail", StringComparison.OrdinalIgnoreCase)) && property.Value.ValueKind == JsonValueKind.Object)
                {
                    aweme = property.Value;
                    return true;
                }
                if (TryFindAwemeDetail(property.Value, out aweme)) return true;
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray()) if (TryFindAwemeDetail(item, out aweme)) return true;
        }
        else if (value.ValueKind == JsonValueKind.String)
        {
            var text = value.GetString();
            if (!string.IsNullOrWhiteSpace(text) && text.TrimStart().StartsWith('{'))
            {
                try { using var nested = JsonDocument.Parse(text); if (TryFindAwemeDetail(nested.RootElement, out var nestedAweme)) { aweme = nestedAweme.Clone(); return true; } } catch (JsonException) { }
            }
        }
        aweme = default;
        return false;
    }

    private static bool TryExtractJsonValue(string source, int start, out string json)
    {
        while (start < source.Length && char.IsWhiteSpace(source[start])) start++;
        if (start >= source.Length || (source[start] != '{' && source[start] != '[')) { json = string.Empty; return false; }
        var depth = 0; var quoted = false; var escaped = false;
        for (var i = start; i < source.Length; i++)
        {
            var ch = source[i];
            if (quoted) { if (escaped) escaped = false; else if (ch == '\\') escaped = true; else if (ch == '"') quoted = false; continue; }
            if (ch == '"') quoted = true;
            else if (ch is '{' or '[') depth++;
            else if (ch is '}' or ']') { if (--depth == 0) { json = source[start..(i + 1)]; return true; } }
        }
        json = string.Empty;
        return false;
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
            var aBogus = ABogusSigner.Sign(query, DouyinConstants.UserAgent);
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
            var publishAweme = await TryFetchPublishAwemeAsync(result.AuthorId, result.AwemeId, cancellationToken);
            if (publishAweme is null)
            {
                BotLog.Info($"MyParser 抖音发布列表封面未命中: aweme_id={result.AwemeId}, sec_uid={result.AuthorId}");
                return result;
            }

            var mergedResult = result;
            var publishParser = workParsers.FirstOrDefault(candidate => candidate.CanParse(publishAweme.Value));
            if (publishParser is not null)
            {
                var publishResult = publishParser.Parse(
                    publishAweme.Value,
                    result.AwemeId,
                    result.SourceUrl ?? $"https://www.douyin.com/note/{result.AwemeId}");
                if (result.IsGallery)
                {
                    mergedResult = MergeGalleryMediaDetail(result, publishResult, "publish-list");
                }
            }

            var publishCover = ExtractPublishCoverUrl(publishAweme.Value);
            if (string.IsNullOrWhiteSpace(publishCover))
            {
                return mergedResult;
            }

            if (string.Equals(publishCover, mergedResult.CoverUrl, StringComparison.Ordinal))
            {
                BotLog.Info($"MyParser 抖音发布列表封面已是当前封面: aweme_id={result.AwemeId}, url={publishCover}");
                return mergedResult;
            }

            if (!string.IsNullOrWhiteSpace(mergedResult.CoverUrl))
            {
                BotLog.Info($"MyParser 抖音发布列表封面命中但保留详情封面: aweme_id={result.AwemeId}, keep={mergedResult.CoverUrl}, publish={publishCover}");
                return mergedResult with { CoverSource = "detail" };
            }

            BotLog.Info($"MyParser 抖音详情封面为空，使用发布列表封面: aweme_id={result.AwemeId}, new={publishCover}");
            return mergedResult with { CoverUrl = publishCover, CoverSource = "publish" };
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

    private static string? TryGetCookieValue(string name)
    {
        if (string.IsNullOrWhiteSpace(MyParserRuntime.DouyinCookie))
        {
            return null;
        }

        foreach (var part in MyParserRuntime.DouyinCookie.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var index = part.IndexOf('=');
            if (index <= 0)
            {
                continue;
            }

            if (string.Equals(part[..index].Trim(), name, StringComparison.OrdinalIgnoreCase))
            {
                return part[(index + 1)..].Trim();
            }
        }

        return null;
    }

    private async Task<string?> GetWebIdAsync(string referer, CancellationToken cancellationToken)
    {
        var cookieWebId = TryGetCookieValue("webid") ?? TryGetCookieValue("ttwid");
        if (!string.IsNullOrWhiteSpace(cookieWebId) && cookieWebId.All(char.IsDigit))
        {
            return cookieWebId;
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "https://mcs.zijieapi.com/webid?aid=6383&sdk_version=5.1.24_dy");
            request.Headers.TryAddWithoutValidation("User-Agent", DouyinConstants.UserAgent);
            request.Headers.TryAddWithoutValidation("Accept", "application/json, text/plain, */*");
            request.Headers.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9");
            request.Headers.TryAddWithoutValidation("Referer", DouyinConstants.HomeUrl);
            request.Headers.TryAddWithoutValidation("sec-ch-ua", "\"Chromium\";v=\"146\", \"Not-A.Brand\";v=\"24\", \"Google Chrome\";v=\"146\"");
            request.Headers.TryAddWithoutValidation("sec-ch-ua-mobile", "?0");
            request.Headers.TryAddWithoutValidation("sec-ch-ua-platform", "\"Windows\"");
            request.Content = new StringContent(JsonSerializer.Serialize(new
            {
                app_id = 6383,
                url = referer,
                user_agent = DouyinConstants.UserAgent,
                referer = string.Empty,
                user_unique_id = string.Empty,
            }), Encoding.UTF8, "application/json");

            using var response = await http.SendAsync(request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(body);
            if (TryGetProperty(doc.RootElement, "web_id", out var webId) && webId.ValueKind == JsonValueKind.String)
            {
                var value = webId.GetString();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    BotLog.Info($"MyParser 抖音 webid 获取成功: webid={value}");
                    return value;
                }
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or TaskCanceledException)
        {
            BotLog.Warning($"MyParser 抖音 webid 获取失败: error={ex.Message}");
        }

        return null;
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
        var aBogus = ABogusSigner.Sign(query, DouyinConstants.UserAgent);
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

    private async Task<JsonElement?> TryFetchPublishAwemeAsync(string secUserId, string awemeId, CancellationToken cancellationToken)
    {
        long maxCursor = 0;
        for (var page = 1; page <= 3; page++)
        {
            var query = BuildUserPostQuery(secUserId, awemeId, maxCursor);
            var unsignedUrl = "https://www.douyin.com/aweme/v1/web/aweme/post/?" + query;
            var aBogus = ABogusSigner.Sign(query, DouyinConstants.UserAgent);
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
                    BotLog.Info($"MyParser 抖音发布列表作品命中: aweme_id={awemeId}, page={page}, cover={publishCover ?? string.Empty}");
                    return item.Clone();
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
        var requestCookie = _guestSession.BuildCookieHeader();
        if (!string.IsNullOrWhiteSpace(requestCookie))
        {
            request.Headers.TryAddWithoutValidation("Cookie", requestCookie);
        }

        using var response = await http.SendAsync(request, cancellationToken);
        _guestSession.Capture(response);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new DouyinParseException($"抖音接口请求失败：HTTP {(int)response.StatusCode}");
        }

        var contentType = response.Content.Headers.ContentType?.ToString() ?? string.Empty;
        var whaleAbortData = TryGetHeaderValue(response, "x-whale-throughput-abort-data");
        var whaleAbortText = TryDecodeBase64Utf8(whaleAbortData);
        if (body.Length == 0)
        {
            LogNonJsonResponse(url, referer, response, contentType, whaleAbortText, whaleAbortData, body);
            if (IsForceLoginAbort(whaleAbortText) || IsForceLoginAbort(whaleAbortData))
            {
                throw new DouyinParseException(BuildForceLoginMessage());
            }

            throw new DouyinParseException("抖音接口返回 HTTP 200 空响应，可能被风控拦截。请配置或更新有效 DouyinCookie 后重试。");
        }

        try
        {
            return JsonDocument.Parse(body);
        }
        catch (JsonException ex)
        {
            LogNonJsonResponse(url, referer, response, contentType, whaleAbortText, whaleAbortData, body);

            if (IsForceLoginAbort(whaleAbortText) || IsForceLoginAbort(whaleAbortData))
            {
                throw new DouyinParseException(BuildForceLoginMessage());
            }

            throw new DouyinParseException("抖音接口返回的不是有效 JSON：" + ex.Message);
        }
    }

    private static void LogNonJsonResponse(string url, string referer, HttpResponseMessage response, string contentType, string? whaleAbortText, string? whaleAbortData, string body)
    {
        var forceLogin = IsForceLoginAbort(whaleAbortText) || IsForceLoginAbort(whaleAbortData);
        BotLog.Warning(
            "MyParser 抖音接口返回非 JSON: "
            + $"endpoint={SafeEndpoint(url)}, referer={SafeEndpoint(referer)}, http={(int)response.StatusCode}, content_type={contentType}, body_length={body.Length}, whale_abort_present={!string.IsNullOrWhiteSpace(whaleAbortText) || !string.IsNullOrWhiteSpace(whaleAbortData)}, whale_force_login={forceLogin}");
    }

    private static string BuildRequestCookieWithoutMsToken()
    {
        if (string.IsNullOrWhiteSpace(MyParserRuntime.DouyinCookie)) return string.Empty;
        return string.Join("; ", MyParserRuntime.DouyinCookie.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(part => !part.StartsWith("msToken=", StringComparison.OrdinalIgnoreCase)));
    }

    private void AddGuestCookies(HttpRequestMessage request)
    {
        var cookie = _guestSession.BuildCookieHeader();
        if (!string.IsNullOrWhiteSpace(cookie)) request.Headers.TryAddWithoutValidation("Cookie", cookie);
    }

    private static string GenerateNumericWebId()
        => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString() + Random.Shared.Next(100000, 999999).ToString();

    private static string SafeEndpoint(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)) return "invalid";
        return uri.Host + uri.AbsolutePath;
    }

    private static string? TryGetHeaderValue(HttpResponseMessage response, string name)
    {
        return response.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;
    }

    private static string? TryDecodeBase64Utf8(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        try
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(value));
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static bool IsForceLoginAbort(string? whaleAbortText)
    {
        return !string.IsNullOrWhiteSpace(whaleAbortText)
               && whaleAbortText.Contains("anonymous", StringComparison.OrdinalIgnoreCase)
               && (whaleAbortText.Contains("\"id\":53", StringComparison.OrdinalIgnoreCase)
                   || whaleAbortText.Contains("\"id\": 53", StringComparison.OrdinalIgnoreCase)
                   || whaleAbortText.Contains("\"id\":296", StringComparison.OrdinalIgnoreCase)
                   || whaleAbortText.Contains("\"id\": 296", StringComparison.OrdinalIgnoreCase)
                   || whaleAbortText.Contains("强制登录", StringComparison.OrdinalIgnoreCase)
                   || whaleAbortText.Contains("强登", StringComparison.OrdinalIgnoreCase));
    }

    private static string BuildForceLoginMessage()
    {
        var hasTtwid = TryGetCookieValue("ttwid") is not null;
        var hasVerifyFp = TryGetCookieValue("s_v_web_id") is not null || TryGetCookieValue("verifyFp") is not null || TryGetCookieValue("fp") is not null;
        if (!hasTtwid || !hasVerifyFp)
        {
            return $"抖音接口触发强制登录/风控模型。当前游客 Cookie 安全态不完整：has_ttwid={hasTtwid}, has_s_v_web_id_or_verifyFp={hasVerifyFp}。请从浏览器 Network 请求头复制完整 Cookie（至少包含 ttwid、s_v_web_id/verifyFp）后重试。";
        }

        return "抖音接口要求登录后才能解析（服务端返回强制登录/风控模型）。请配置或更新有效 DouyinCookie 后重试。";
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
