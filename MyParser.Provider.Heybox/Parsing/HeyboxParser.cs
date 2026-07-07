using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MyParser.Provider.Heybox.Models;

namespace MyParser.Provider.Heybox.Parsing;

public sealed partial class HeyboxParser(PluginConfig config) : IDisposable
{
    private const string ApiLinkTree = "https://api.xiaoheihe.cn/bbs/app/link/tree";
    private const string HeyboxWebOrigin = "https://www.xiaoheihe.cn/";
    private const string HkeyAlphabet = "AB45STUVWZEFGJ6CH01D237IXYPQRKLMN89";
    private static readonly Lazy<string> StableDeviceId = new(LoadOrCreateDeviceId, LazyThreadSafetyMode.ExecutionAndPublication);
    private readonly HttpClient _http = CreateHttpClient(config);

    [GeneratedRegex(@"https?://[A-Za-z0-9._~:/?#\[\]@!$&()*+,;=%-]+?(?:\.mp4|\.m3u8)(?:[A-Za-z0-9._~:/?#\[\]@!$&()*+,;=%-]*)", RegexOptions.IgnoreCase)]
    private static partial Regex VideoUrlRegex();

    [GeneratedRegex(@"https?://[A-Za-z0-9._~:/?#\[\]@!$&()*+,;=%-]+?(?:\.jpg|\.jpeg|\.png|\.webp)(?:[A-Za-z0-9._~:/?#\[\]@!$&()*+,;=%-]*)", RegexOptions.IgnoreCase)]
    private static partial Regex ImageUrlRegex();

    [GeneratedRegex(@"<meta\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex MetaTagRegex();

    [GeneratedRegex("<title[^>]*>(.*?)</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex TitleTagRegex();

    [GeneratedRegex("([:\\w-]+)\\s*=\\s*(['\"])(.*?)\\2", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex HtmlAttributeRegex();

    [GeneratedRegex("<[^>]+>", RegexOptions.Singleline)]
    private static partial Regex HtmlTagRegex();

    [GeneratedRegex("<(p|div|h[1-6]|blockquote|li|pre|code)([^>]*)>(.*?)</\\1>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex HtmlBlockRegex();

    [GeneratedRegex("<br\\s*/?>", RegexOptions.IgnoreCase)]
    private static partial Regex BrRegex();

    [GeneratedRegex("</(p|div|h[1-6]|li|blockquote)>", RegexOptions.IgnoreCase)]
    private static partial Regex BlockEndRegex();

    [GeneratedRegex("\\n{3,}", RegexOptions.Compiled)]
    private static partial Regex MultiBlankLineRegex();

    [GeneratedRegex("\\s+", RegexOptions.Singleline)]
    private static partial Regex WhitespaceRegex();

    public static bool ContainsHeyboxUrl(string text) => HeyboxUrlParser.ContainsHeyboxUrl(text);

    public static bool LooksLikeCookie(string cookie)
    {
        return !string.IsNullOrWhiteSpace(cookie)
               && cookie.Contains('=')
               && (cookie.Contains(';')
                   || cookie.Contains("heybox", StringComparison.OrdinalIgnoreCase)
                   || cookie.Contains("hkey", StringComparison.OrdinalIgnoreCase));
    }

    public async Task<HeyboxParseResult> ParseAsync(string text, CancellationToken cancellationToken = default)
    {
        var sourceUrl = HeyboxUrlParser.ExtractHeyboxUrl(text) ?? throw new HeyboxParseException("未找到小黑盒链接。");
        var initialLinkId = HeyboxUrlParser.ExtractLinkId(sourceUrl);
        BotLog.Info($"MyParser 小黑盒解析开始: url={sourceUrl}, link_id={initialLinkId ?? "-"}");

        if (!string.IsNullOrWhiteSpace(initialLinkId))
        {
            var apiFirst = new HeyboxAggregate(sourceUrl, sourceUrl, initialLinkId)
            {
                SourceKind = "api",
            };
            await TryFetchPostApiAsync(apiFirst, cancellationToken);
            try
            {
                return apiFirst.ToResult();
            }
            catch (HeyboxParseException ex)
            {
                BotLog.Warning($"MyParser 小黑盒 API-first 结果为空，尝试页面兜底: link_id={initialLinkId}, reason={ex.Message}");
            }
        }

        BotLog.Info($"MyParser 小黑盒页面请求开始: url={sourceUrl}");
        using var pageRequest = new HttpRequestMessage(HttpMethod.Get, sourceUrl);
        ApplyBrowserHeaders(pageRequest, null);
        using var pageResponse = await _http.SendAsync(pageRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var pageText = await pageResponse.Content.ReadAsStringAsync(cancellationToken);
        var resolvedUrl = pageResponse.RequestMessage?.RequestUri?.ToString() ?? sourceUrl;
        var linkId = HeyboxUrlParser.ExtractLinkId(sourceUrl, resolvedUrl, pageText)
                     ?? throw new HeyboxParseException("未能从小黑盒链接中提取帖子 link_id。");
        BotLog.Info($"MyParser 小黑盒页面请求完成: link_id={linkId}, status={(int)pageResponse.StatusCode}, resolved={resolvedUrl}");

        var aggregate = new HeyboxAggregate(sourceUrl, resolvedUrl, linkId)
        {
            SourceKind = "page",
        };
        MergeHtml(aggregate, pageText);

        await TryFetchPostApiAsync(aggregate, cancellationToken);

        return aggregate.ToResult();
    }

    private async Task TryFetchPostApiAsync(HeyboxAggregate aggregate, CancellationToken cancellationToken)
    {
        var uri = BuildPostApiUri(aggregate);
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        ApplyApiHeaders(request, aggregate.ResolvedUrl ?? aggregate.SourceUrl);
        try
        {
            BotLog.Info($"MyParser 小黑盒帖子 API 请求开始: link_id={aggregate.LinkId}, uri={uri}");
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(12));
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token);
            BotLog.Info($"MyParser 小黑盒帖子 API 响应: link_id={aggregate.LinkId}, status={(int)response.StatusCode}, content_type={response.Content.Headers.ContentType}");
            if (!response.IsSuccessStatusCode)
            {
                BotLog.Warning($"MyParser 小黑盒帖子 API 请求失败: link_id={aggregate.LinkId}, status={(int)response.StatusCode}");
                return;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(timeoutCts.Token);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: timeoutCts.Token);
            if (document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("status", out var status)
                && string.Equals(status.GetString(), "show_captcha", StringComparison.OrdinalIgnoreCase))
            {
                BotLog.Warning($"MyParser 小黑盒帖子 API 返回验证码/风控: link_id={aggregate.LinkId}");
                throw new HeyboxParseException("小黑盒返回验证码/风控，暂时无法自动解析该帖子。");
            }

            aggregate.SourceKind = "api";
            MergeJson(aggregate, document.RootElement);
            BotLog.Info($"MyParser 小黑盒帖子 API 解析完成: link_id={aggregate.LinkId}, title={aggregate.Title ?? "-"}, blocks={aggregate.Blocks.Count}, images={aggregate.ImageCount}, videos={aggregate.VideoCount}");
        }
        catch (HeyboxParseException)
        {
            throw;
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            BotLog.Warning($"MyParser 小黑盒帖子 API 请求超时: link_id={aggregate.LinkId}, error={ex.GetType().Name}: {ex.Message}");
        }
        catch (Exception ex)
        {
            BotLog.Warning($"MyParser 小黑盒帖子 API 解析失败: link_id={aggregate.LinkId}, error={ex.GetType().Name}: {ex.Message}");
        }
    }

    private static Uri BuildPostApiUri(HeyboxAggregate aggregate)
    {
        var parameters = new Dictionary<string, string?>
        {
            ["os_type"] = "web",
            ["app"] = "heybox",
            ["client_type"] = "web",
            ["version"] = "999.0.4",
            ["web_version"] = "2.5",
            ["x_client_type"] = "web",
            ["x_app"] = "heybox_website",
            ["heybox_id"] = string.Empty,
            ["x_os_type"] = "Windows",
            ["device_info"] = "Chrome",
            ["device_id"] = StableDeviceId.Value,
            ["link_id"] = aggregate.LinkId,
            ["is_first"] = "1",
            ["page"] = "1",
            ["index"] = "1",
            ["limit"] = "20",
            ["owner_only"] = "0",
        };

        foreach (var key in new[] { "h_src", "h_camp", "h_session_id" })
        {
            var value = TryGetQueryValue(aggregate.SourceUrl, key) ?? TryGetQueryValue(aggregate.ResolvedUrl, key);
            if (!string.IsNullOrWhiteSpace(value))
            {
                parameters[key] = value;
            }
        }

        // Real web requests include h_src even when it is empty for plain /app/bbs/link/{id}?htk=... URLs.
        parameters.TryAdd("h_src", string.Empty);

        foreach (var item in HeyboxWebSignature("/bbs/app/link/tree"))
        {
            parameters[item.Key] = item.Value;
        }

        var query = string.Join("&", parameters
            .Where(i => i.Value is not null)
            .Select(i => Uri.EscapeDataString(i.Key) + "=" + Uri.EscapeDataString(i.Value ?? string.Empty)));
        return new Uri(ApiLinkTree + "?" + query);
    }

    private static string? TryGetQueryValue(string? url, string key)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return null;
        }

        var query = uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries);
        foreach (var part in query)
        {
            var index = part.IndexOf('=');
            var name = index < 0 ? part : part[..index];
            if (!string.Equals(Uri.UnescapeDataString(name), key, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return index < 0 ? string.Empty : Uri.UnescapeDataString(part[(index + 1)..]);
        }

        return null;
    }

    private static void MergeHtml(HeyboxAggregate aggregate, string html)
    {
        foreach (Match match in VideoUrlRegex().Matches(html))
        {
            aggregate.AddVideo(match.Value);
        }

        foreach (Match match in ImageUrlRegex().Matches(html))
        {
            aggregate.AddImage(match.Value);
        }

        foreach (Match tag in MetaTagRegex().Matches(html))
        {
            var attrs = ParseHtmlAttributes(tag.Value);
            var key = (attrs.GetValueOrDefault("property") ?? attrs.GetValueOrDefault("name") ?? string.Empty).ToLowerInvariant();
            var content = attrs.GetValueOrDefault("content");
            if (string.IsNullOrWhiteSpace(content))
            {
                continue;
            }

            if (key is "og:title" or "twitter:title" or "title")
            {
                aggregate.SetTitle(CleanText(content, 120));
            }
            else if (key is "description" or "og:description" or "twitter:description")
            {
                aggregate.SetDescription(CleanText(content, 300));
            }
            else if (key is "og:image" or "twitter:image")
            {
                aggregate.AddImage(content);
                aggregate.CoverUrl ??= content;
            }
        }

        if (string.IsNullOrWhiteSpace(aggregate.Title))
        {
            var title = TitleTagRegex().Match(html);
            if (title.Success)
            {
                aggregate.SetTitle(WebUtility.HtmlDecode(title.Groups[1].Value));
            }
        }
    }

    private static Dictionary<string, string> ParseHtmlAttributes(string tag)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in HtmlAttributeRegex().Matches(tag))
        {
            result[match.Groups[1].Value] = WebUtility.HtmlDecode(match.Groups[3].Value);
        }

        return result;
    }

    private static void MergeJson(HeyboxAggregate aggregate, JsonElement root)
    {
        var primary = FindPrimaryContainer(root);
        if (primary is { ValueKind: JsonValueKind.Object })
        {
            MergeJsonObject(aggregate, primary.Value, allowBodyText: true);
        }

        WalkJson(root, element =>
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                    MergeJsonObject(aggregate, element, allowBodyText: false);
                    break;
                case JsonValueKind.String:
                    var value = element.GetString();
                    if (string.IsNullOrWhiteSpace(value))
                    {
                        break;
                    }

                    foreach (Match match in VideoUrlRegex().Matches(value))
                    {
                        aggregate.AddVideo(match.Value);
                    }

                    foreach (Match match in ImageUrlRegex().Matches(value))
                    {
                        aggregate.AddImage(match.Value);
                    }
                    break;
            }
        });
    }

    private static JsonElement? FindPrimaryContainer(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var key in new[] { "link", "post", "article", "link_info" })
        {
            if (root.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Object)
            {
                return value;
            }
        }

        foreach (var key in new[] { "result", "data" })
        {
            if (root.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Object)
            {
                var nested = FindPrimaryContainer(value);
                if (nested is not null)
                {
                    return nested;
                }
            }
        }

        return null;
    }

    private static void MergeJsonObject(HeyboxAggregate aggregate, JsonElement item, bool allowBodyText)
    {
        aggregate.SetTitle(GetFirstString(item, "title", "share_title", "link_title", "post_title"));
        aggregate.SetDescription(GetFirstString(item, allowBodyText
            ? ["description", "desc", "summary", "brief", "share_desc", "content", "text"]
            : ["description", "desc", "summary", "brief", "share_desc"]));
        aggregate.AuthorName ??= GetFirstString(item, "username", "nickname", "author_name", "user_name");
        aggregate.AuthorId ??= GetFirstString(item, "userid", "user_id", "author_id");
        aggregate.AuthorAvatarUrl ??= GetFirstString(item, "avatar", "avartar", "avatar_url", "face");
        aggregate.CoverUrl ??= GetFirstString(item, "thumb", "cover", "cover_url", "image", "img", "share_img", "share_image");
        aggregate.ShareUrl ??= GetFirstString(item, "share_url", "url", "link_url");
        aggregate.ViewCount ??= GetFirstLong(item, "click", "view", "view_count", "read_count");
        aggregate.LikeCount ??= GetFirstLong(item, "link_award_num", "like", "like_count", "award_num", "up");
        aggregate.CommentCount ??= GetFirstLong(item, "comment_num", "comment", "comment_count");
        aggregate.FavoriteCount ??= GetFirstLong(item, "favour_count", "favorite", "favorite_count", "fav_count");
        aggregate.ShareCount ??= GetFirstLong(item, "forward_num", "share", "share_count");
        aggregate.IsArticle = aggregate.IsArticle || GetFirstLong(item, "is_article") == 1;
        var createAt = GetFirstLong(item, "create_at", "publish_time", "pub_ts");
        if (aggregate.PublishTime is null && createAt is > 0)
        {
            aggregate.PublishTime = DateTimeOffset.FromUnixTimeSeconds(createAt.Value).ToLocalTime();
        }

        if (item.TryGetProperty("user", out var user) && user.ValueKind == JsonValueKind.Object)
        {
            aggregate.AuthorName ??= GetFirstString(user, "username", "nickname", "name");
            aggregate.AuthorId ??= GetFirstString(user, "userid", "user_id", "uid");
            aggregate.AuthorAvatarUrl ??= GetFirstString(user, "avatar", "avartar", "avatar_url", "face");
        }

        if (item.TryGetProperty("topics", out var topics) && topics.ValueKind == JsonValueKind.Array)
        {
            foreach (var topic in topics.EnumerateArray())
            {
                aggregate.AddTopic(GetFirstString(topic, "name", "topic_name"));
            }
        }

        if (!string.IsNullOrWhiteSpace(aggregate.CoverUrl))
        {
            aggregate.AddImage(aggregate.CoverUrl!);
        }

        if (allowBodyText)
        {
            MergeArticleBody(aggregate, item);
        }
    }

    private static void MergeArticleBody(HeyboxAggregate aggregate, JsonElement item)
    {
        if (!item.TryGetProperty("text", out var textElement))
        {
            return;
        }

        if (textElement.ValueKind != JsonValueKind.String)
        {
            return;
        }

        var raw = textElement.GetString();
        if (string.IsNullOrWhiteSpace(raw))
        {
            return;
        }

        if (!TryMergeArticleBlocksFromJsonString(aggregate, raw))
        {
            MergeArticleHtml(aggregate, raw);
        }

        aggregate.SetDescription(TrimText(aggregate.PlainText, 260));
    }

    private static bool TryMergeArticleBlocksFromJsonString(HeyboxAggregate aggregate, string raw)
    {
        var trimmed = raw.Trim();
        if (!trimmed.StartsWith('[') && !trimmed.StartsWith('{'))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(trimmed);
            if (document.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var element in document.RootElement.EnumerateArray())
                {
                    MergeArticleContentElement(aggregate, element);
                }
            }
            else
            {
                MergeArticleContentElement(aggregate, document.RootElement);
            }

            return aggregate.Blocks.Count > 0;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static void MergeArticleContentElement(HeyboxAggregate aggregate, JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        var type = GetElementString(element, "type");
        if (string.Equals(type, "img", StringComparison.OrdinalIgnoreCase)
            || string.Equals(type, "image", StringComparison.OrdinalIgnoreCase))
        {
            var url = GetElementString(element, "url")
                      ?? GetElementString(element, "src")
                      ?? GetElementString(element, "original")
                      ?? GetElementString(element, "data-original");
            if (!string.IsNullOrWhiteSpace(url))
            {
                aggregate.AddImage(url);
                aggregate.AddBlock(new HeyboxArticleBlock
                {
                    Type = HeyboxArticleBlockType.Image,
                    Url = url,
                    Caption = CleanText(GetElementString(element, "caption") ?? GetElementString(element, "desc"), 180) ?? string.Empty,
                });
            }

            return;
        }

        if (string.Equals(type, "video", StringComparison.OrdinalIgnoreCase))
        {
            var url = GetElementString(element, "url") ?? GetElementString(element, "src") ?? GetElementString(element, "video_url");
            if (!string.IsNullOrWhiteSpace(url))
            {
                aggregate.AddVideo(url);
                aggregate.AddBlock(new HeyboxArticleBlock
                {
                    Type = HeyboxArticleBlockType.Video,
                    Url = url,
                    Caption = CleanText(GetElementString(element, "caption") ?? GetElementString(element, "desc"), 180) ?? string.Empty,
                });
            }

            return;
        }

        var html = GetElementString(element, "text")
                   ?? GetElementString(element, "content")
                   ?? GetElementString(element, "html");
        if (!string.IsNullOrWhiteSpace(html))
        {
            MergeArticleHtml(aggregate, html);
        }
    }

    private static void MergeArticleHtml(HeyboxAggregate aggregate, string html)
    {
        var added = false;
        foreach (Match image in ImageUrlRegex().Matches(html))
        {
            aggregate.AddImage(image.Value);
        }

        foreach (Match video in VideoUrlRegex().Matches(html))
        {
            aggregate.AddVideo(video.Value);
        }

        foreach (Match match in HtmlBlockRegex().Matches(html))
        {
            var tag = match.Groups[1].Value.ToLowerInvariant();
            var attrs = match.Groups[2].Value;
            var inner = match.Groups[3].Value;
            foreach (var imageUrl in ExtractImageUrlsFromHtml(inner))
            {
                aggregate.AddImage(imageUrl);
                aggregate.AddBlock(new HeyboxArticleBlock
                {
                    Type = HeyboxArticleBlockType.Image,
                    Url = imageUrl,
                    Caption = string.Equals(tag, "h4", StringComparison.OrdinalIgnoreCase) && attrs.Contains("img-desc", StringComparison.OrdinalIgnoreCase)
                        ? NormalizeHtmlText(inner)
                        : string.Empty,
                });
                added = true;
            }

            var text = NormalizeHtmlText(inner);
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            if (string.Equals(tag, "h4", StringComparison.OrdinalIgnoreCase) && attrs.Contains("img-desc", StringComparison.OrdinalIgnoreCase))
            {
                aggregate.AttachCaptionToLastImage(text);
                continue;
            }

            var style = tag switch
            {
                "h1" or "h2" or "h3" or "h4" or "h5" or "h6" => HeyboxArticleTextStyle.Heading,
                "blockquote" => HeyboxArticleTextStyle.Quote,
                "li" => HeyboxArticleTextStyle.ListItem,
                _ when attrs.Contains("blockquote", StringComparison.OrdinalIgnoreCase) => HeyboxArticleTextStyle.Quote,
                _ => HeyboxArticleTextStyle.Normal,
            };
            var headingLevel = style == HeyboxArticleTextStyle.Heading ? ExtractHeadingLevel(tag) : 0;
            if (style == HeyboxArticleTextStyle.ListItem && !text.TrimStart().StartsWith('•'))
            {
                text = "• " + text;
            }

            aggregate.AddBlock(new HeyboxArticleBlock
            {
                Type = HeyboxArticleBlockType.Text,
                TextStyle = style,
                HeadingLevel = headingLevel,
                IsBold = headingLevel > 0 || ContainsBoldMarkup(inner),
                Text = text,
            });
            added = true;
        }

        if (!added)
        {
            var plain = HtmlToPlainText(html);
            if (!string.IsNullOrWhiteSpace(plain))
            {
                aggregate.AddBlock(new HeyboxArticleBlock
                {
                    Type = HeyboxArticleBlockType.Text,
                    Text = plain,
                });
            }
        }
    }

    private static IEnumerable<string> ExtractImageUrlsFromHtml(string html)
    {
        foreach (Match tag in Regex.Matches(html, "<img\\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.Singleline))
        {
            var attrs = ParseHtmlAttributes(tag.Value);
            var url = attrs.GetValueOrDefault("data-original")
                      ?? attrs.GetValueOrDefault("data-src")
                      ?? attrs.GetValueOrDefault("src")
                      ?? attrs.GetValueOrDefault("url");
            if (!string.IsNullOrWhiteSpace(url))
            {
                yield return WebUtility.HtmlDecode(url);
            }
        }
    }

    private static string NormalizeHtmlText(string html)
    {
        var text = BrRegex().Replace(html, "\n");
        text = HtmlTagRegex().Replace(text, string.Empty);
        text = WebUtility.HtmlDecode(text);
        text = Regex.Replace(text, "[ \t\u00A0]+", " ");
        text = Regex.Replace(text, "\\n{3,}", "\n\n").Trim();
        return text;
    }

    private static string? HtmlToPlainText(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return null;
        }

        var text = BrRegex().Replace(html, "\n");
        text = BlockEndRegex().Replace(text, "\n");
        text = HtmlTagRegex().Replace(text, string.Empty);
        text = WebUtility.HtmlDecode(text);
        text = MultiBlankLineRegex().Replace(text, "\n\n").Trim();
        return text;
    }

    private static int ExtractHeadingLevel(string tag)
    {
        return tag.Length == 2 && tag[0] == 'h' && char.IsDigit(tag[1])
            ? Math.Clamp(tag[1] - '0', 1, 6)
            : 2;
    }

    private static bool ContainsBoldMarkup(string html)
    {
        return Regex.IsMatch(html, "<(strong|b)\\b", RegexOptions.IgnoreCase)
               || Regex.IsMatch(html, "font-weight\\s*:\\s*(bold|[6-9]00)", RegexOptions.IgnoreCase);
    }

    private static string? TrimText(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        value = value.ReplaceLineEndings(" ").Trim();
        return value.Length <= maxLength ? value : value[..maxLength] + "…";
    }

    private static string? GetElementString(JsonElement item, string key)
    {
        return item.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static string? GetFirstString(JsonElement item, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (!item.TryGetProperty(key, out var value))
            {
                continue;
            }

            var text = value.ValueKind switch
            {
                JsonValueKind.String => value.GetString(),
                JsonValueKind.Number => value.GetRawText(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                _ => null,
            };
            text = CleanText(text, key.Contains("content", StringComparison.OrdinalIgnoreCase) || key == "text" ? 600 : 180);
            if (!string.IsNullOrWhiteSpace(text))
            {
                return text;
            }
        }

        return null;
    }

    private static long? GetFirstLong(JsonElement item, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (!item.TryGetProperty(key, out var value))
            {
                continue;
            }

            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number))
            {
                return number;
            }

            if (value.ValueKind == JsonValueKind.String && long.TryParse(value.GetString(), out number))
            {
                return number;
            }
        }

        return null;
    }

    private static void WalkJson(JsonElement element, Action<JsonElement> visit)
    {
        visit(element);
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    if (property.NameEquals("comment") || property.NameEquals("comments") || property.NameEquals("reply_list"))
                    {
                        continue;
                    }

                    WalkJson(property.Value, visit);
                }
                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    WalkJson(item, visit);
                }
                break;
        }
    }

    private static string? CleanText(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var text = WebUtility.HtmlDecode(value);
        text = HtmlTagRegex().Replace(text, " ");
        text = WhitespaceRegex().Replace(text, " ").Trim();
        if (text.Length == 0 || IsGenericTitle(text))
        {
            return null;
        }

        return text.Length <= maxLength ? text : text[..maxLength] + "…";
    }

    private static bool IsGenericTitle(string value)
    {
        return value is "小黑盒" or "高能玩家聚集地 - 小黑盒";
    }

    private static Dictionary<string, string> HeyboxWebSignature(string path)
    {
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var nonce = Md5Hex(timestamp + Guid.NewGuid().ToString("N")).ToUpperInvariant();
        return new Dictionary<string, string>
        {
            ["hkey"] = HkeyOv(path, timestamp + 1, nonce),
            ["_time"] = timestamp.ToString(),
            ["nonce"] = nonce,
        };
    }

    private static string LoadOrCreateDeviceId()
    {
        var stateDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Shirobot.Plugin.MyParser",
            "heybox");
        var statePath = Path.Combine(stateDirectory, "device_id.txt");
        try
        {
            if (File.Exists(statePath))
            {
                var existing = File.ReadAllText(statePath).Trim();
                if (existing.Length == 32 && existing.All(Uri.IsHexDigit))
                {
                    return existing.ToLowerInvariant();
                }
            }

            Directory.CreateDirectory(stateDirectory);
            var deviceId = Guid.NewGuid().ToString("N");
            File.WriteAllText(statePath, deviceId);
            return deviceId;
        }
        catch (Exception ex)
        {
            BotLog.Warning($"MyParser 小黑盒 device_id 状态读写失败，使用进程内临时值: {ex.GetType().Name}: {ex.Message}");
            return Guid.NewGuid().ToString("N");
        }
    }

    private static string Md5Hex(string value)
    {
        var hash = MD5.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static int HkeyMix(int value) => (value & 128) != 0 ? ((value << 1) ^ 27) & 255 : value << 1;
    private static int HkeyQm(int value) => HkeyMix(value) ^ value;
    private static int HkeyDollar(int value) => HkeyQm(HkeyMix(value));
    private static int HkeyYm(int value) => HkeyDollar(HkeyQm(HkeyMix(value)));
    private static int HkeyGm(int value) => HkeyYm(value) ^ HkeyDollar(value) ^ HkeyQm(value);

    private static int[] HkeyKm(IReadOnlyList<int> source)
    {
        var values = source.ToList();
        while (values.Count < 4)
        {
            values.Add(0);
        }

        var mixed = new[]
        {
            HkeyGm(values[0]) ^ HkeyYm(values[1]) ^ HkeyDollar(values[2]) ^ HkeyQm(values[3]),
            HkeyQm(values[0]) ^ HkeyGm(values[1]) ^ HkeyYm(values[2]) ^ HkeyDollar(values[3]),
            HkeyDollar(values[0]) ^ HkeyQm(values[1]) ^ HkeyGm(values[2]) ^ HkeyYm(values[3]),
            HkeyYm(values[0]) ^ HkeyDollar(values[1]) ^ HkeyQm(values[2]) ^ HkeyGm(values[3]),
        };
        return mixed.Concat(values.Skip(4)).ToArray();
    }

    private static string HkeyAv(string value, int endOffset)
    {
        var alphabet = endOffset < 0 ? HkeyAlphabet[..^Math.Abs(endOffset)] : HkeyAlphabet[..endOffset];
        return string.Concat(value.Select(ch => alphabet[ch % alphabet.Length]));
    }

    private static string HkeySv(string value) => string.Concat(value.Select(ch => HkeyAlphabet[ch % HkeyAlphabet.Length]));

    private static string Interleave(params string[] values)
    {
        var builder = new StringBuilder(values.Sum(i => i.Length));
        for (var index = 0; index < values.Max(i => i.Length); index++)
        {
            foreach (var value in values)
            {
                if (index < value.Length)
                {
                    builder.Append(value[index]);
                }
            }
        }

        return builder.ToString();
    }

    private static string HkeyOv(string path, long timestamp, string nonce)
    {
        var normalizedPath = "/" + string.Join('/', path.Split('/', StringSplitOptions.RemoveEmptyEntries)) + "/";
        var seed = Interleave(HkeyAv(timestamp.ToString(), -2), HkeySv(normalizedPath), HkeySv(nonce));
        seed = seed.Length <= 20 ? seed : seed[..20];
        var digest = Md5Hex(seed);
        var checkValues = digest[^6..].Select(ch => (int)ch).ToArray();
        var check = (HkeyKm(checkValues).Sum() % 100).ToString().PadLeft(2, '0');
        var prefix = HkeyAv(digest[..5], -4);
        return prefix + check;
    }

    private const string BrowserUserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/125.0.0.0 Safari/537.36";

    private static HttpClient CreateHttpClient(PluginConfig config)
    {
        var http = new HttpClient(new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            AllowAutoRedirect = true,
        });
        http.Timeout = TimeSpan.FromSeconds(Math.Clamp(config.RequestTimeoutSeconds, 5, 60));
        http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", BrowserUserAgent);
        http.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language", "zh-CN,zh;q=0.9,en;q=0.8");
        return http;
    }

    private static void ApplyBrowserHeaders(HttpRequestMessage request, string? referer)
    {
        request.Headers.TryAddWithoutValidation("User-Agent", BrowserUserAgent);
        request.Headers.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,application/json;q=0.8,*/*;q=0.7");
        request.Headers.TryAddWithoutValidation("Accept-Language", "zh-CN,zh;q=0.9,en;q=0.8");
        request.Headers.TryAddWithoutValidation("Cache-Control", "no-cache");
        request.Headers.TryAddWithoutValidation("Pragma", "no-cache");
        if (!string.IsNullOrWhiteSpace(referer))
        {
            request.Headers.TryAddWithoutValidation("Referer", referer);
        }

        ApplyCookieHeader(request);
    }

    private static void ApplyApiHeaders(HttpRequestMessage request, string referer)
    {
        request.Headers.TryAddWithoutValidation("User-Agent", BrowserUserAgent);
        request.Headers.TryAddWithoutValidation("Accept", "application/json, text/plain, */*");
        request.Headers.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9");
        request.Headers.TryAddWithoutValidation("Origin", "https://www.xiaoheihe.cn");
        request.Headers.TryAddWithoutValidation("Referer", HeyboxWebOrigin);
        request.Headers.TryAddWithoutValidation("Sec-Fetch-Dest", "empty");
        request.Headers.TryAddWithoutValidation("Sec-Fetch-Mode", "cors");
        request.Headers.TryAddWithoutValidation("Sec-Fetch-Site", "same-site");
        request.Headers.TryAddWithoutValidation("sec-ch-ua-platform", "\"Windows\"");
        request.Headers.TryAddWithoutValidation("sec-ch-ua", "\"Chromium\";v=\"146\", \"Not-A.Brand\";v=\"24\", \"Google Chrome\";v=\"146\"");
        request.Headers.TryAddWithoutValidation("sec-ch-ua-mobile", "?0");
        ApplyCookieHeader(request);
    }

    private static void ApplyCookieHeader(HttpRequestMessage request)
    {
        var cookie = NormalizeCookie(MyParserRuntime.HeyboxCookie);
        if (!string.IsNullOrWhiteSpace(cookie))
        {
            request.Headers.TryAddWithoutValidation("Cookie", cookie);
        }
    }

    private static string NormalizeCookie(string cookie)
    {
        cookie = cookie.Trim();
        return cookie.StartsWith("Cookie:", StringComparison.OrdinalIgnoreCase) ? cookie[7..].Trim() : cookie;
    }

    public void Dispose() => _http.Dispose();

    private sealed class HeyboxAggregate(string sourceUrl, string? resolvedUrl, string linkId)
    {
        private readonly List<string> _images = [];
        private readonly List<string> _videos = [];

        public string SourceUrl { get; } = sourceUrl;
        public string? ResolvedUrl { get; } = resolvedUrl;
        public string LinkId { get; } = linkId;
        public string? Title { get; private set; }
        public string? Description { get; private set; }
        public string? AuthorName { get; set; }
        public string? AuthorId { get; set; }
        public string? AuthorAvatarUrl { get; set; }
        public string? CoverUrl { get; set; }
        public long? ViewCount { get; set; }
        public long? LikeCount { get; set; }
        public long? CommentCount { get; set; }
        public long? FavoriteCount { get; set; }
        public long? ShareCount { get; set; }
        public DateTimeOffset? PublishTime { get; set; }
        public string? ShareUrl { get; set; }
        public string? SourceKind { get; set; }
        public bool IsArticle { get; set; }
        public IReadOnlyList<HeyboxArticleBlock> Blocks => _blocks;
        public int ImageCount => _images.Count;
        public int VideoCount => _videos.Count;
        public string? PlainText => string.Join("\n\n", _blocks.Where(i => i.Type == HeyboxArticleBlockType.Text).Select(i => i.Text).Where(i => !string.IsNullOrWhiteSpace(i))).Trim();

        private readonly List<HeyboxArticleBlock> _blocks = [];
        private readonly List<string> _topics = [];

        public void SetTitle(string? value) => Title ??= value;
        public void SetDescription(string? value) => Description ??= value;

        public void AddImage(string? url)
        {
            AddUrl(_images, url);
        }

        public void AddVideo(string? url)
        {
            AddUrl(_videos, url);
        }

        public void AddTopic(string? topic)
        {
            if (string.IsNullOrWhiteSpace(topic))
            {
                return;
            }

            topic = topic.Trim();
            if (!_topics.Contains(topic, StringComparer.OrdinalIgnoreCase))
            {
                _topics.Add(topic);
            }
        }

        public void AddBlock(HeyboxArticleBlock block)
        {
            if (block.Type == HeyboxArticleBlockType.Text && string.IsNullOrWhiteSpace(block.Text))
            {
                return;
            }

            if (block.Type is HeyboxArticleBlockType.Image or HeyboxArticleBlockType.Video && string.IsNullOrWhiteSpace(block.Url))
            {
                return;
            }

            if (block.Type == HeyboxArticleBlockType.Image)
            {
                AddImage(block.Url);
            }
            else if (block.Type == HeyboxArticleBlockType.Video)
            {
                AddVideo(block.Url);
            }

            _blocks.Add(block);
        }

        public void AttachCaptionToLastImage(string caption)
        {
            if (string.IsNullOrWhiteSpace(caption))
            {
                return;
            }

            for (var index = _blocks.Count - 1; index >= 0; index--)
            {
                if (_blocks[index].Type != HeyboxArticleBlockType.Image)
                {
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(_blocks[index].Caption))
                {
                    return;
                }

                _blocks[index] = _blocks[index] with { Caption = caption.Trim() };
                return;
            }
        }

        private static void AddUrl(List<string> list, string? url)
        {
            if (string.IsNullOrWhiteSpace(url) || !url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            url = WebUtility.HtmlDecode(url).Trim();
            if (!list.Contains(url, StringComparer.OrdinalIgnoreCase))
            {
                list.Add(url);
            }
        }

        public HeyboxParseResult ToResult()
        {
            if (string.IsNullOrWhiteSpace(Title) && string.IsNullOrWhiteSpace(Description) && _images.Count == 0 && _videos.Count == 0)
            {
                throw new HeyboxParseException("小黑盒帖子解析结果为空，可能需要登录或触发了风控。");
            }

            return new HeyboxParseResult
            {
                LinkId = LinkId,
                SourceUrl = SourceUrl,
                ResolvedUrl = ResolvedUrl,
                Title = Title,
                Description = Description,
                PlainText = PlainText,
                Blocks = _blocks,
                AuthorName = AuthorName,
                AuthorId = AuthorId,
                AuthorAvatarUrl = AuthorAvatarUrl,
                CoverUrl = CoverUrl ?? _images.FirstOrDefault(),
                ImageUrls = _images,
                VideoUrls = _videos,
                ViewCount = ViewCount,
                LikeCount = LikeCount,
                CommentCount = CommentCount,
                FavoriteCount = FavoriteCount,
                ShareCount = ShareCount,
                PublishTime = PublishTime,
                ShareUrl = ShareUrl,
                SourceKind = SourceKind,
                IsArticle = IsArticle || _blocks.Count > 0,
                Topics = _topics,
            };
        }
    }
}
