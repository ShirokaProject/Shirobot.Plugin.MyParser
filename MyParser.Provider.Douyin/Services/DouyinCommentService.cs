using System.Text.Json;
using MyParser.Provider.Douyin.Infrastructure;
using MyParser.Provider.Douyin.Models;
using ShiroBot.SDK.Abstractions;
using static MyParser.Provider.Douyin.Infrastructure.DouyinRequestHeaders;
using static MyParser.Provider.Douyin.Utilities.DouyinParseHelpers;
using static MyParser.Provider.Douyin.Utilities.DouyinQueryBuilder;

namespace MyParser.Provider.Douyin.Services;

internal sealed class DouyinCommentService(
    HttpClient http,
    DouyinGuestSession guestSession,
    DouyinMsTokenProvider msTokenProvider)
{
    public async Task<List<DouyinCommentInfo>> FetchAsync(
        DouyinParseResult result,
        int maxCount,
        CancellationToken cancellationToken)
    {
        var referer = result.SourceUrl
            ?? (result.IsGallery
                ? $"https://www.douyin.com/note/{result.AwemeId}"
                : $"https://www.douyin.com/video/{result.AwemeId}");
        var ttwid = guestSession.Get("ttwid") ?? GetConfiguredCookie("ttwid");
        var msToken = GetConfiguredCookie("msToken")
                      ?? await msTokenProvider.GetAsync(ttwid, cancellationToken);
        guestSession.Set("msToken", msToken);
        var webId = guestSession.Get("user_unique_id")
                    ?? guestSession.Get("webid")
                    ?? GenerateNumericWebId();
        var verifyFp = GetConfiguredCookie("s_v_web_id")
                       ?? GetConfiguredCookie("verifyFp")
                       ?? GetConfiguredCookie("fp")
                       ?? guestSession.Get("s_v_web_id")
                       ?? guestSession.Get("verifyFp");
        var comments = new List<DouyinCommentInfo>(maxCount);
        var commentIds = new HashSet<string>(StringComparer.Ordinal);
        long cursor = 0;
        var pageCount = 0;
        BotLog.Info($"MyParser 抖音评论接口请求开始: aweme_id={result.AwemeId}, max_count={maxCount}, ms_token_present={!string.IsNullOrWhiteSpace(msToken)}, web_id={webId}");

        while (comments.Count < maxCount && pageCount++ < 5)
        {
            using var doc = await FetchPageAsync(
                result.AwemeId,
                cursor,
                Math.Min(20, maxCount - comments.Count),
                msToken,
                webId,
                verifyFp,
                referer,
                cancellationToken);
            var root = doc.RootElement;
            TryGetProperty(root, "comments", out var items);
            var responseCount = items.ValueKind == JsonValueKind.Array ? items.GetArrayLength() : 0;
            BotLog.Info($"MyParser 抖音评论接口分页返回: aweme_id={result.AwemeId}, page={pageCount}, cursor={cursor}, response_count={responseCount}, has_more={GetBool(root, "has_more")}, next_cursor={GetLong(root, "cursor")}");

            foreach (var item in items.EnumerateArray())
            {
                var comment = ParseComment(item, result.AuthorId);
                if (comment is not null
                    && (string.IsNullOrWhiteSpace(comment.CommentId) || commentIds.Add(comment.CommentId)))
                {
                    BotLog.Info($"MyParser 抖音评论模型解析: aweme_id={result.AwemeId}, comment_id={comment.CommentId}, user={comment.UserName}, images={comment.ImageUrls.Count}, likes={comment.LikeCount}, replies={comment.ReplyCount}, create_time={comment.CreateTimeUnixSeconds}");
                    comments.Add(comment);
                    if (comments.Count >= maxCount)
                    {
                        break;
                    }
                }
            }

            var nextCursor = GetLong(root, "cursor");
            if (!GetBool(root, "has_more") || nextCursor <= cursor)
            {
                break;
            }

            cursor = nextCursor;
            await Task.Delay(350, cancellationToken);
        }

        return comments;
    }

    private async Task<JsonDocument> FetchPageAsync(
        string awemeId,
        long cursor,
        int count,
        string? msToken,
        string webId,
        string? verifyFp,
        string referer,
        CancellationToken cancellationToken)
    {
        var query = BuildCommentListQuery(awemeId, cursor, count, msToken, webId, verifyFp);
        var aBogus = ABogusSigner.Sign(query, DouyinConstants.UserAgent);
        var path = "aweme/v1/web/comment/list/?" + query + "&a_bogus=" + Uri.EscapeDataString(aBogus);
        try
        {
            return await GetValidatedPageAsync("https://www.douyin.com/" + path, referer, cancellationToken);
        }
        catch (DouyinParseException)
        {
            return await GetValidatedPageAsync("https://www-hj.douyin.com/" + path, referer, cancellationToken);
        }
    }

    private async Task<JsonDocument> GetValidatedPageAsync(
        string url,
        string referer,
        CancellationToken cancellationToken)
    {
        var doc = await GetJsonAsync(url, referer, cancellationToken);
        var root = doc.RootElement;
        if (GetInt(root, "status_code") == 0
            && TryGetProperty(root, "comments", out var comments)
            && comments.ValueKind == JsonValueKind.Array)
        {
            return doc;
        }

        var statusCode = GetInt(root, "status_code");
        doc.Dispose();
        throw new DouyinParseException($"评论接口返回无效业务数据：status_code={statusCode}");
    }

    private async Task<JsonDocument> GetJsonAsync(
        string url,
        string referer,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        ApplyDefaultHeaders(request, referer);
        var cookie = BuildRequestCookie();
        if (!string.IsNullOrWhiteSpace(cookie))
        {
            request.Headers.TryAddWithoutValidation("Cookie", cookie);
        }

        using var response = await http.SendAsync(request, cancellationToken);
        guestSession.Capture(response);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new DouyinParseException($"评论接口请求失败：HTTP {(int)response.StatusCode}");
        }

        if (string.IsNullOrWhiteSpace(body))
        {
            throw new DouyinParseException("评论接口返回空响应，可能需要更新 DouyinCookie。");
        }

        try
        {
            return JsonDocument.Parse(body);
        }
        catch (JsonException ex)
        {
            throw new DouyinParseException("评论接口返回的不是有效 JSON：" + ex.Message);
        }
    }

    private static DouyinCommentInfo? ParseComment(JsonElement item, string? authorId)
    {
        var text = GetString(item, "text")?.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        TryGetProperty(item, "user", out var user);
        var userId = user.ValueKind == JsonValueKind.Object
            ? GetString(user, "uid") ?? GetString(user, "sec_uid")
            : null;
        var userSecId = user.ValueKind == JsonValueKind.Object ? GetString(user, "sec_uid") : null;
        var avatarUrl = user.ValueKind == JsonValueKind.Object
                        && TryGetProperty(user, "avatar_thumb", out var avatar)
            ? EnumerateUrlList(avatar).FirstOrDefault()
            : null;
        return new DouyinCommentInfo
        {
            CommentId = GetString(item, "cid") ?? string.Empty,
            Text = text,
            UserName = user.ValueKind == JsonValueKind.Object
                ? GetString(user, "nickname") ?? "未知用户"
                : "未知用户",
            UserId = userId,
            DisplayUserId = user.ValueKind == JsonValueKind.Object
                ? GetString(user, "unique_id") ?? GetString(user, "short_id") ?? GetString(user, "uid")
                : null,
            UserAvatarUrl = avatarUrl,
            ImageUrls = ExtractCommentImageUrls(item),
            IpLabel = GetString(item, "ip_label"),
            LikeCount = GetLong(item, "digg_count"),
            ReplyCount = GetLong(item, "reply_comment_total"),
            CreateTimeUnixSeconds = GetLong(item, "create_time"),
            IsAuthor = !string.IsNullOrWhiteSpace(authorId)
                       && string.Equals(userSecId, authorId, StringComparison.Ordinal),
        };
    }

    private static List<string> ExtractCommentImageUrls(JsonElement item)
    {
        var urls = new List<string>();
        if (!TryGetProperty(item, "image_list", out var images) || images.ValueKind != JsonValueKind.Array)
        {
            return urls;
        }

        foreach (var image in images.EnumerateArray())
        {
            foreach (var propertyName in new[]
                     {
                         "origin_url", "medium_url", "label_large", "download_url", "image_url", "url_default",
                     })
            {
                if (!TryGetProperty(image, propertyName, out var resource)) continue;
                foreach (var url in EnumerateCommentImageUrls(resource))
                {
                    urls.Add(url);
                }
            }

            foreach (var url in EnumerateCommentImageUrls(image))
            {
                urls.Add(url);
            }
        }

        var result = urls.Distinct(StringComparer.Ordinal).ToList();
        if (images.GetArrayLength() > 0 && result.Count == 0)
        {
            var shapes = images.EnumerateArray()
                .Select(image => image.ValueKind == JsonValueKind.Object
                    ? string.Join(',', image.EnumerateObject().Select(property => property.Name))
                    : image.ValueKind.ToString());
            BotLog.Warning($"MyParser 抖音评论附图字段存在但 URL 未命中: image_count={images.GetArrayLength()}, image_shapes={string.Join('|', shapes)}");
        }

        return result;
    }

    private static IEnumerable<string> EnumerateCommentImageUrls(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.String)
        {
            var text = value.GetString();
            if (Uri.TryCreate(text, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
            {
                yield return uri.ToString();
            }

            yield break;
        }

        if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray())
            foreach (var url in EnumerateCommentImageUrls(item))
                yield return url;
            yield break;
        }

        if (value.ValueKind != JsonValueKind.Object) yield break;
        foreach (var property in value.EnumerateObject())
        {
            if (property.Name.Contains("url", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var url in EnumerateCommentImageUrls(property.Value)) yield return url;
            }
        }
    }

    private static string? GetConfiguredCookie(string name)
    {
        foreach (var part in MyParserRuntime.DouyinCookie.Split(
                     ';',
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = part.IndexOf('=');
            if (separator > 0 && string.Equals(part[..separator], name, StringComparison.OrdinalIgnoreCase))
            {
                return part[(separator + 1)..];
            }
        }

        return null;
    }

    private string BuildRequestCookie()
    {
        var configured = MyParserRuntime.DouyinCookie.Split(
                ';',
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(part => !part.StartsWith("msToken=", StringComparison.OrdinalIgnoreCase))
            .ToList();
        var configuredNames = configured
            .Select(part => part.Split('=', 2)[0])
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        configured.AddRange(guestSession.BuildCookieHeader()
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(part => !configuredNames.Contains(part.Split('=', 2)[0])));
        return string.Join("; ", configured);
    }

    private static string GenerateNumericWebId()
        => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString()
           + Random.Shared.Next(100000, 999999);
}
