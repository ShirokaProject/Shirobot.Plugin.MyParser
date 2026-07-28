using System.Net.Http.Json;
using System.Text.Json;
using MyParser.Provider.BiliBili.Infrastructure;
using MyParser.Provider.BiliBili.Models;
using MyParser.Provider.BiliBili.Parsing;
using MyParser.Provider.BiliBili.Utilities;

namespace MyParser.Provider.BiliBili.Services;

internal sealed class BilibiliCommentService(
    HttpClient http,
    Func<CancellationToken, Task<string>> getMixinKey)
{
    public async Task<List<BilibiliCommentInfo>> FetchAsync(
        BilibiliParseResult result,
        int maxCount,
        CancellationToken cancellationToken)
    {
        var comments = new List<BilibiliCommentInfo>(maxCount);
        var commentIds = new HashSet<string>(StringComparer.Ordinal);
        string? nextOffset = null;

        for (var page = 0; comments.Count < maxCount && page < 5; page++)
        {
            using var json = await FetchPageAsync(result, nextOffset, cancellationToken);
            var data = json.RootElement.GetPropertyOrDefault("data")
                       ?? throw new BilibiliParseException("B站评论接口未返回 data。");

            foreach (var item in data.GetPropertyOrDefault("replies").EnumerateArrayOrEmpty())
            {
                var comment = ParseComment(item, result.AuthorId);
                if (comment is null || !commentIds.Add(comment.CommentId))
                {
                    continue;
                }

                comments.Add(comment);
                if (comments.Count >= maxCount)
                {
                    break;
                }
            }

            var cursor = data.GetPropertyOrDefault("cursor");
            var newOffset = cursor?.GetPropertyOrDefault("pagination_reply")?.GetStringOrDefault("next_offset");
            if (cursor?.GetBoolOrDefault("is_end") == true
                || string.IsNullOrWhiteSpace(newOffset)
                || string.Equals(newOffset, nextOffset, StringComparison.Ordinal))
            {
                break;
            }

            nextOffset = newOffset;
            await Task.Delay(300, cancellationToken);
        }

        return comments;
    }

    private async Task<JsonDocument> FetchPageAsync(
        BilibiliParseResult result,
        string? nextOffset,
        CancellationToken cancellationToken)
    {
        var parameters = new Dictionary<string, object?>
        {
            ["oid"] = result.Aid,
            ["type"] = 1,
            ["mode"] = 3,
            ["pagination_str"] = JsonSerializer.Serialize(new { offset = nextOffset ?? string.Empty }),
            ["plat"] = 1,
            ["web_location"] = 1315875,
        };
        if (string.IsNullOrWhiteSpace(nextOffset))
        {
            parameters["seek_rpid"] = string.Empty;
        }

        var signed = BilibiliWbiSigner.Sign(parameters, await getMixinKey(cancellationToken));
        var query = string.Join("&", signed.Select(item =>
            $"{Uri.EscapeDataString(item.Key)}={Uri.EscapeDataString(item.Value)}"));
        using var request = new HttpRequestMessage(HttpMethod.Get, BilibiliConstants.CommentMainApi + "?" + query);
        request.Headers.TryAddWithoutValidation("User-Agent", BilibiliConstants.UserAgent);
        request.Headers.TryAddWithoutValidation("Referer", result.SourceUrl ?? $"https://www.bilibili.com/video/{result.Bvid}/");
        request.Headers.TryAddWithoutValidation("Origin", BilibiliConstants.Origin);
        request.Headers.TryAddWithoutValidation("Accept", "application/json, text/plain, */*");
        if (!string.IsNullOrWhiteSpace(MyParserRuntime.BilibiliCookie))
        {
            request.Headers.TryAddWithoutValidation("Cookie", MyParserRuntime.BilibiliCookie);
        }

        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadFromJsonAsync<JsonDocument>(cancellationToken: cancellationToken)
                   ?? throw new BilibiliParseException("B站评论接口返回空响应。");
        var code = json.RootElement.GetInt32OrDefault("code");
        if (code == 0)
        {
            return json;
        }

        var message = json.RootElement.GetStringOrDefault("message") ?? "未知错误";
        json.Dispose();
        throw new BilibiliParseException($"B站评论接口错误 {code}: {message}");
    }

    private static BilibiliCommentInfo? ParseComment(JsonElement item, string? authorId)
    {
        var content = item.GetPropertyOrDefault("content");
        var message = content?.GetStringOrDefault("message")?.Trim();
        if (string.IsNullOrWhiteSpace(message))
        {
            return null;
        }

        var commentId = item.GetStringOrDefault("rpid_str")
                        ?? item.GetInt64OrDefault("rpid").ToString();
        if (string.IsNullOrWhiteSpace(commentId) || commentId == "0")
        {
            return null;
        }

        var member = item.GetPropertyOrDefault("member");
        var memberId = member?.GetStringOrDefault("mid")
                       ?? member?.GetInt64OrDefault("mid").ToString();
        var firstPicture = content?.GetPropertyOrDefault("pictures").EnumerateArrayOrEmpty().FirstOrDefault();
        var replyControl = item.GetPropertyOrDefault("reply_control");
        return new BilibiliCommentInfo
        {
            CommentId = commentId,
            Message = message,
            UserName = member?.GetStringOrDefault("uname") ?? "未知用户",
            UserId = string.IsNullOrWhiteSpace(memberId) || memberId == "0" ? null : memberId,
            UserAvatarUrl = member?.GetStringOrDefault("avatar"),
            ImageUrl = firstPicture is { ValueKind: JsonValueKind.Object }
                ? firstPicture.Value.GetStringOrDefault("img_src")
                : null,
            IpLocation = replyControl?.GetStringOrDefault("location"),
            TimeDescription = replyControl?.GetStringOrDefault("time_desc"),
            LikeCount = item.GetInt64OrDefault("like"),
            ReplyCount = item.GetInt64OrDefault("rcount"),
            CreateTimeUnixSeconds = item.GetInt64OrDefault("ctime"),
            IsAuthor = !string.IsNullOrWhiteSpace(authorId)
                       && string.Equals(memberId, authorId, StringComparison.Ordinal),
        };
    }
}
