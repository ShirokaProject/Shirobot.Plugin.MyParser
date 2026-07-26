using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Shirobot.Plugin.MyParser.Providers.Bilibili.Facade;
using Shirobot.Plugin.MyParser.Providers.Bilibili.Infrastructure;
using Shirobot.Plugin.MyParser.Providers.Bilibili.Models;
using Shirobot.Plugin.MyParser.Providers.Bilibili.Utilities;

namespace Shirobot.Plugin.MyParser.Providers.Bilibili.Impl.Services;

internal sealed partial class BilibiliArticleParser(HttpClient http, PluginConfig config)
{
    public async Task<BilibiliArticleParseResult> ParseAsync(string text, CancellationToken cancellationToken = default)
    {
        if (BilibiliUrlParser.ExtractOpusId(text) is { } opusId)
        {
            return await ParseOpusAsync(opusId, cancellationToken);
        }

        var cvid = BilibiliUrlParser.ExtractCvid(text);
        if (cvid is null && BilibiliUrlParser.ExtractB23Url(text) is { } shortUrl)
        {
            var resolved = await ResolveArticleIdFromShortUrlAsync(shortUrl, cancellationToken);
            if (resolved.OpusId is not null)
            {
                return await ParseOpusAsync(resolved.OpusId, cancellationToken);
            }

            cvid = resolved.Cvid;
        }

        if (cvid is null)
        {
            throw new BilibiliParseException("无法从输入中提取专栏 cv 号或 opus 图文 ID。");
        }

        using var json = await GetArticleJsonAsync(cvid.Value, cancellationToken);
        var data = json.RootElement.GetPropertyOrDefault("data") ?? throw new BilibiliParseException("B站专栏接口未返回 data。");
        var author = data.GetPropertyOrDefault("author");
        var stats = data.GetPropertyOrDefault("stats");

        var publishTs = data.GetInt64OrDefault("publish_time");
        var imageUrls = data.GetStringArrayOrEmpty("origin_image_urls");
        if (imageUrls.Count == 0)
        {
            imageUrls = data.GetStringArrayOrEmpty("image_urls");
        }

        var bannerUrl = data.GetStringOrDefault("banner_url");
        if (string.IsNullOrWhiteSpace(bannerUrl))
        {
            bannerUrl = imageUrls.FirstOrDefault();
        }

        var content = data.GetStringOrDefault("content");
        var opusContent = data.GetPropertyOrDefault("opus")?.GetPropertyOrDefault("content");
        var blocks = opusContent is { ValueKind: JsonValueKind.Object }
            ? BuildArticleBlocksFromOpusContent(opusContent.Value, imageUrls)
            : BuildArticleBlocks(content, imageUrls);
        var plainText = string.Join("\n\n", blocks.Where(i => i.Type == BilibiliArticleBlockType.Text).Select(i => i.Text));
        if (string.IsNullOrWhiteSpace(plainText))
        {
            plainText = HtmlToPlainText(content);
        }
        var categories = new List<string>();
        var categoryArray = data.GetPropertyOrDefault("categories").EnumerateArrayOrEmpty();
        foreach (var item in categoryArray)
        {
            var name = item.GetStringOrDefault("name");
            if (!string.IsNullOrWhiteSpace(name))
            {
                categories.Add(name);
            }
        }

        return new BilibiliArticleParseResult
        {
            Cvid = cvid.Value,
            IsOpus = false,
            SourceUrl = $"https://www.bilibili.com/read/cv{cvid.Value}/",
            Title = data.GetStringOrDefault("title"),
            Summary = data.GetStringOrDefault("summary"),
            ContentHtml = content,
            PlainText = plainText,
            BannerUrl = bannerUrl,
            ImageUrls = imageUrls,
            Blocks = blocks,
            AuthorName = author?.GetStringOrDefault("name"),
            AuthorId = author?.GetInt64OrDefault("mid").ToString(),
            AuthorAvatarUrl = author?.GetStringOrDefault("face"),
            AuthorFans = author?.GetInt64OrDefault("fans") ?? 0,
            ViewCount = stats?.GetInt64OrDefault("view") ?? 0,
            LikeCount = stats?.GetInt64OrDefault("like") ?? 0,
            CoinCount = stats?.GetInt64OrDefault("coin") ?? 0,
            FavoriteCount = stats?.GetInt64OrDefault("favorite") ?? 0,
            ShareCount = stats?.GetInt64OrDefault("share") ?? 0,
            ReplyCount = stats?.GetInt64OrDefault("reply") ?? 0,
            Words = data.GetInt64OrDefault("words"),
            PublishTime = publishTs > 0 ? DateTimeOffset.FromUnixTimeSeconds(publishTs).ToLocalTime() : null,
            Categories = categories,
        };
    }

    private async Task<JsonDocument> GetArticleJsonAsync(long cvid, CancellationToken cancellationToken)
    {
        var url = BilibiliConstants.ArticleViewApi + "?id=" + cvid + "&gaia_source=main_web";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        ApplyHeaders(request, $"https://www.bilibili.com/read/cv{cvid}/");
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadFromJsonAsync<JsonDocument>(cancellationToken: cancellationToken)
                   ?? throw new BilibiliParseException("B站专栏接口返回空响应。");
        var code = json.RootElement.GetInt32OrDefault("code");
        if (code != 0)
        {
            var message = json.RootElement.GetStringOrDefault("message") ?? "未知错误";
            throw new BilibiliParseException($"B站专栏接口错误 {code}: {message}");
        }

        return json;
    }

    private async Task<BilibiliArticleParseResult> ParseOpusAsync(string opusId, CancellationToken cancellationToken)
    {
        using var json = await GetOpusJsonAsync(opusId, cancellationToken);
        var data = json.RootElement.GetPropertyOrDefault("data") ?? throw new BilibiliParseException("B站图文接口未返回 data。");
        var fallback = data.GetPropertyOrDefault("fallback");
        if (fallback is { ValueKind: JsonValueKind.Object } fb && fb.GetInt32OrDefault("type") == 2 && fb.GetInt64OrDefault("id") > 0)
        {
            return await ParseAsync("cv" + fb.GetInt64OrDefault("id"), cancellationToken);
        }

        var item = data.GetPropertyOrDefault("item") ?? throw new BilibiliParseException("B站图文接口未返回 item。");
        var basic = item.GetPropertyOrDefault("basic");
        var modules = item.GetPropertyOrDefault("modules").EnumerateArrayOrEmpty().ToArray();

        string? title = basic?.GetStringOrDefault("title");
        string? authorName = null;
        string? authorId = basic?.GetInt64OrDefault("uid").ToString();
        string? avatar = null;
        long pubTs = 0;
        long like = 0, coin = 0, favorite = 0, share = 0, reply = 0;
        var imageUrls = new List<string>();
        var textParts = new List<string>();
        var blocks = new List<BilibiliArticleBlock>();

        foreach (var module in modules)
        {
            var moduleType = module.GetStringOrDefault("module_type");
            if (moduleType == "MODULE_TYPE_TITLE")
            {
                title = module.GetPropertyOrDefault("module_title")?.GetStringOrDefault("text") ?? title;
            }
            else if (moduleType == "MODULE_TYPE_AUTHOR")
            {
                var author = module.GetPropertyOrDefault("module_author");
                authorName = author?.GetStringOrDefault("name") ?? authorName;
                authorId = author?.GetInt64OrDefault("mid").ToString() ?? authorId;
                avatar = author?.GetStringOrDefault("face") ?? avatar;
                pubTs = author?.GetInt64OrDefault("pub_ts") ?? pubTs;
            }
            else if (moduleType == "MODULE_TYPE_STAT")
            {
                var stat = module.GetPropertyOrDefault("module_stat");
                like = stat?.GetPropertyOrDefault("like")?.GetInt64OrDefault("count") ?? like;
                coin = stat?.GetPropertyOrDefault("coin")?.GetInt64OrDefault("count") ?? coin;
                favorite = stat?.GetPropertyOrDefault("favorite")?.GetInt64OrDefault("count") ?? favorite;
                share = stat?.GetPropertyOrDefault("forward")?.GetInt64OrDefault("count") ?? share;
                reply = stat?.GetPropertyOrDefault("comment")?.GetInt64OrDefault("count") ?? reply;
            }
            else if (moduleType == "MODULE_TYPE_CONTENT")
            {
                ParseOpusContent(module.GetPropertyOrDefault("module_content"), textParts, imageUrls, blocks);
            }
        }

        var plainText = string.Join("\n\n", textParts.Where(i => !string.IsNullOrWhiteSpace(i))).Trim();
        var cvid = long.TryParse(basic?.GetStringOrDefault("rid_str"), out var rid) ? rid : 0;
        return new BilibiliArticleParseResult
        {
            Cvid = cvid,
            OpusId = opusId,
            IsOpus = true,
            SourceUrl = $"https://www.bilibili.com/opus/{opusId}",
            Title = title,
            Summary = TrimText(plainText, 180),
            PlainText = plainText,
            BannerUrl = imageUrls.FirstOrDefault(),
            ImageUrls = imageUrls.Distinct().ToList(),
            Blocks = blocks,
            AuthorName = authorName,
            AuthorId = authorId,
            AuthorAvatarUrl = avatar,
            LikeCount = like,
            CoinCount = coin,
            FavoriteCount = favorite,
            ShareCount = share,
            ReplyCount = reply,
            Words = string.IsNullOrWhiteSpace(plainText) ? 0 : plainText.Length,
            PublishTime = pubTs > 0 ? DateTimeOffset.FromUnixTimeSeconds(pubTs).ToLocalTime() : null,
            Categories = ["图文"],
        };
    }

    private async Task<JsonDocument> GetOpusJsonAsync(string opusId, CancellationToken cancellationToken)
    {
        var parameters = new Dictionary<string, string>
        {
            ["id"] = opusId,
            ["timezone_offset"] = "-480",
            ["features"] = BilibiliConstants.OpusDetailFeatures,
        };
        var url = BilibiliConstants.OpusDetailApi + "?" + string.Join("&", parameters.Select(i => $"{Uri.EscapeDataString(i.Key)}={Uri.EscapeDataString(i.Value)}"));
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        ApplyHeaders(request, $"https://www.bilibili.com/opus/{opusId}");
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadFromJsonAsync<JsonDocument>(cancellationToken: cancellationToken)
                   ?? throw new BilibiliParseException("B站图文接口返回空响应。");
        var code = json.RootElement.GetInt32OrDefault("code");
        if (code != 0)
        {
            var message = json.RootElement.GetStringOrDefault("message") ?? "未知错误";
            throw new BilibiliParseException($"B站图文接口错误 {code}: {message}。该接口通常需要 Cookie 中存在有效 buvid3/登录态，可先使用 #bili-login。");
        }

        return json;
    }

    private static void ParseOpusContent(JsonElement? moduleContent, List<string> textParts, List<string> imageUrls, List<BilibiliArticleBlock> blocks)
    {
        foreach (var paragraph in moduleContent?.GetPropertyOrDefault("paragraphs").EnumerateArrayOrEmpty() ?? [])
        {
            var paraType = paragraph.GetInt32OrDefault("para_type");
            if (paraType is 1 or 4 or 6)
            {
                var info = ExtractOpusNodesInfo(paragraph.GetPropertyOrDefault("text")?.GetPropertyOrDefault("nodes"));
                if (!string.IsNullOrWhiteSpace(info.Text))
                {
                    var listFormat = paragraph.GetPropertyOrDefault("format")?.GetPropertyOrDefault("list_format");
                    var indentLevel = Math.Clamp((listFormat?.GetInt32OrDefault("level") ?? 1) - 1, 0, 6);
                    var listOrder = listFormat?.GetInt32OrDefault("order") ?? 0;
                    var style = paraType == 4
                        ? BilibiliArticleTextStyle.Quote
                        : paraType == 6
                            ? BilibiliArticleTextStyle.ListItem
                            : InferOpusTextStyle(info);
                    var text = style == BilibiliArticleTextStyle.ListItem
                        ? (listOrder > 0 ? $"{listOrder}. {info.Text}" : "• " + info.Text)
                        : info.Text;

                    textParts.Add(text);
                    blocks.Add(new BilibiliArticleBlock
                    {
                        Type = BilibiliArticleBlockType.Text,
                        TextStyle = style,
                        HeadingLevel = info.HeadingLevel,
                        IndentLevel = style == BilibiliArticleTextStyle.ListItem ? indentLevel : 0,
                        IsBold = info.IsBold,
                        TextColor = info.Color,
                        Text = text,
                    });
                }
            }
            else if (paraType == 2)
            {
                foreach (var pic in paragraph.GetPropertyOrDefault("pic")?.GetPropertyOrDefault("pics").EnumerateArrayOrEmpty() ?? [])
                {
                    var url = pic.GetStringOrDefault("url");
                    if (!string.IsNullOrWhiteSpace(url))
                    {
                        imageUrls.Add(url);
                        blocks.Add(new BilibiliArticleBlock { Type = BilibiliArticleBlockType.Image, Url = url, Caption = pic.GetStringOrDefault("alt") ?? string.Empty });
                    }
                }
            }
            else if (paraType == 5)
            {
                foreach (var item in paragraph.GetPropertyOrDefault("list")?.GetPropertyOrDefault("items").EnumerateArrayOrEmpty() ?? [])
                {
                    var info = ExtractOpusNodesInfo(item.GetPropertyOrDefault("nodes"));
                    if (!string.IsNullOrWhiteSpace(info.Text))
                    {
                        var itemText = "• " + info.Text;
                        textParts.Add(itemText);
                        blocks.Add(new BilibiliArticleBlock
                        {
                            Type = BilibiliArticleBlockType.Text,
                            TextStyle = BilibiliArticleTextStyle.ListItem,
                            HeadingLevel = info.HeadingLevel,
                            IndentLevel = Math.Clamp(item.GetInt32OrDefault("level"), 0, 6),
                            IsBold = info.IsBold,
                            TextColor = info.Color,
                            Text = itemText,
                        });
                    }
                }
            }
            else if (paraType == 7)
            {
                var code = paragraph.GetPropertyOrDefault("code")?.GetStringOrDefault("content");
                if (!string.IsNullOrWhiteSpace(code))
                {
                    textParts.Add(code);
                    blocks.Add(new BilibiliArticleBlock { Type = BilibiliArticleBlockType.Text, TextStyle = BilibiliArticleTextStyle.Code, Text = code });
                }
            }
        }
    }

    private static OpusTextInfo ExtractOpusNodesInfo(JsonElement? nodes)
    {
        var parts = new List<string>();
        var fontSize = 0;
        var isBold = false;
        string? color = null;
        string? fontLevel = null;

        foreach (var node in nodes.EnumerateArrayOrEmpty())
        {
            var type = node.GetStringOrDefault("type");
            if (type == "TEXT_NODE_TYPE_WORD" || node.GetInt32OrDefault("node_type") == 1)
            {
                var word = node.GetPropertyOrDefault("word");
                parts.Add(word?.GetStringOrDefault("words") ?? string.Empty);
                fontSize = Math.Max(fontSize, word?.GetInt32OrDefault("font_size") ?? 0);
                fontLevel ??= word?.GetStringOrDefault("font_level");
                color ??= NormalizeHexColor(word?.GetStringOrDefault("color"));
                isBold |= word?.GetPropertyOrDefault("style")?.GetBoolOrDefault("bold") ?? false;
            }
            else if (type == "TEXT_NODE_TYPE_RICH")
            {
                var rich = node.GetPropertyOrDefault("rich");
                parts.Add(rich?.GetStringOrDefault("text") ?? rich?.GetStringOrDefault("orig_text") ?? string.Empty);
            }
            else if (type == "TEXT_NODE_TYPE_FORMULA")
            {
                parts.Add(node.GetPropertyOrDefault("formula")?.GetStringOrDefault("latex_content") ?? string.Empty);
            }
        }

        var text = string.Concat(parts).Trim();
        var headingLevel = InferHeadingLevel(fontSize, fontLevel, isBold, text);
        return new OpusTextInfo(text, headingLevel, isBold, color);
    }

    private static BilibiliArticleTextStyle InferOpusTextStyle(OpusTextInfo info)
    {
        return info.HeadingLevel > 0 ? BilibiliArticleTextStyle.Heading : BilibiliArticleTextStyle.Normal;
    }

    private static int InferHeadingLevel(int fontSize, string? fontLevel, bool isBold, string text)
    {
        if (fontSize >= 24)
        {
            return 1;
        }

        if (fontSize >= 21 || string.Equals(fontLevel, "xLarge", StringComparison.OrdinalIgnoreCase))
        {
            return 2;
        }

        if (fontSize >= 19 || string.Equals(fontLevel, "large", StringComparison.OrdinalIgnoreCase))
        {
            return 3;
        }

        return isBold && text.Length is > 0 and <= 36 ? 4 : 0;
    }

    private sealed record OpusTextInfo(string Text, int HeadingLevel, bool IsBold, string? Color);

    private async Task<(long? Cvid, string? OpusId)> ResolveArticleIdFromShortUrlAsync(string shortUrl, CancellationToken cancellationToken)
    {
        var finalUrl = await new BilibiliParser(config, http).ResolveBilibiliRedirectUrlAsync(shortUrl, cancellationToken);
        var cvid = BilibiliUrlParser.ExtractCvid(finalUrl);
        var opusId = BilibiliUrlParser.ExtractOpusId(finalUrl);
        if (cvid is not null || opusId is not null)
        {
            return (cvid, opusId);
        }

        return (null, null);
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

    private static List<BilibiliArticleBlock> BuildArticleBlocksFromOpusContent(JsonElement opusContent, List<string> fallbackImageUrls)
    {
        var textParts = new List<string>();
        var imageUrls = new List<string>();
        var blocks = new List<BilibiliArticleBlock>();
        ParseOpusContent(opusContent, textParts, imageUrls, blocks);
        AppendMissingImageBlocks(blocks, fallbackImageUrls);
        return blocks;
    }

    private static List<BilibiliArticleBlock> BuildArticleBlocks(string? content, List<string> imageUrls)
    {
        if (TryBuildQuillBlocks(content, imageUrls, out var quillBlocks))
        {
            return quillBlocks;
        }

        var blocks = new List<BilibiliArticleBlock>();
        if (!string.IsNullOrWhiteSpace(content))
        {
            foreach (Match match in HtmlBlockRegex().Matches(content))
            {
                var tag = match.Groups[1].Value.ToLowerInvariant();
                var attrs = match.Groups[2].Value;
                var inner = match.Groups[3].Value;
                var text = NormalizeHtmlText(inner);
                if (string.IsNullOrWhiteSpace(text))
                {
                    continue;
                }

                var style = tag switch
                {
                    "h1" or "h2" or "h3" or "h4" or "h5" or "h6" => BilibiliArticleTextStyle.Heading,
                    "blockquote" => BilibiliArticleTextStyle.Quote,
                    "pre" or "code" => BilibiliArticleTextStyle.Code,
                    "li" => BilibiliArticleTextStyle.ListItem,
                    _ when attrs.Contains("blockquote", StringComparison.OrdinalIgnoreCase) => BilibiliArticleTextStyle.Quote,
                    _ => BilibiliArticleTextStyle.Normal,
                };
                var indent = ExtractIndentLevel(attrs);
                if (style == BilibiliArticleTextStyle.ListItem && !text.TrimStart().StartsWith('•'))
                {
                    text = "• " + text;
                }

                var headingLevel = style == BilibiliArticleTextStyle.Heading ? ExtractHeadingLevel(tag, attrs) : 0;
                var textColor = ExtractCssColor(attrs) ?? ExtractInnerColor(inner);
                var isBold = headingLevel > 0 || ContainsBoldMarkup(inner);
                blocks.Add(new BilibiliArticleBlock
                {
                    Type = BilibiliArticleBlockType.Text,
                    TextStyle = style,
                    HeadingLevel = headingLevel,
                    IndentLevel = indent,
                    IsBold = isBold,
                    TextColor = textColor,
                    Text = text,
                });
            }
        }

        if (blocks.Count == 0)
        {
            var plain = HtmlToPlainText(content);
            if (!string.IsNullOrWhiteSpace(plain))
            {
                foreach (var chunk in SplitText(plain, 900))
                {
                    blocks.Add(new BilibiliArticleBlock { Type = BilibiliArticleBlockType.Text, Text = chunk });
                }
            }
        }

        AppendMissingImageBlocks(blocks, imageUrls);
        return blocks;
    }

    private static bool TryBuildQuillBlocks(string? content, List<string> fallbackImageUrls, out List<BilibiliArticleBlock> blocks)
    {
        blocks = [];
        if (string.IsNullOrWhiteSpace(content) || !content.TrimStart().StartsWith('{'))
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(content);
            var ops = doc.RootElement.GetPropertyOrDefault("ops");
            if (ops is not { ValueKind: JsonValueKind.Array })
            {
                return false;
            }

            var pendingText = string.Empty;
            foreach (var op in ops.Value.EnumerateArray())
            {
                var insert = op.GetPropertyOrDefault("insert");
                var attrs = op.GetPropertyOrDefault("attributes");
                if (insert is null)
                {
                    continue;
                }

                if (insert.Value.ValueKind == JsonValueKind.String)
                {
                    var value = insert.Value.GetString() ?? string.Empty;
                    if (value.Contains('\n'))
                    {
                        var parts = value.Split('\n');
                        for (var i = 0; i < parts.Length; i++)
                        {
                            pendingText += parts[i];
                            if (i < parts.Length - 1)
                            {
                                AddQuillTextBlock(blocks, pendingText, attrs);
                                pendingText = string.Empty;
                            }
                        }
                    }
                    else
                    {
                        pendingText += value;
                    }
                }
                else if (insert.Value.ValueKind == JsonValueKind.Object)
                {
                    FlushPendingQuillText(blocks, ref pendingText);
                    var image = insert.Value.GetPropertyOrDefault("native-image") ?? insert.Value.GetPropertyOrDefault("image");
                    var url = image?.GetStringOrDefault("url") ?? image?.GetString();
                    if (!string.IsNullOrWhiteSpace(url))
                    {
                        blocks.Add(new BilibiliArticleBlock
                        {
                            Type = BilibiliArticleBlockType.Image,
                            Url = url,
                            Caption = image?.GetStringOrDefault("alt") ?? string.Empty,
                        });
                    }
                }
            }

            FlushPendingQuillText(blocks, ref pendingText);
            AppendMissingImageBlocks(blocks, fallbackImageUrls);
            return blocks.Count > 0;
        }
        catch (JsonException)
        {
            blocks = [];
            return false;
        }
    }

    private static void AddQuillTextBlock(List<BilibiliArticleBlock> blocks, string text, JsonElement? attrs)
    {
        text = WebUtility.HtmlDecode(text).Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        var header = attrs?.GetInt32OrDefault("header") ?? 0;
        var isBlockquote = attrs?.GetBoolOrDefault("blockquote") ?? false;
        var list = attrs?.GetStringOrDefault("list");
        var style = header > 0
            ? BilibiliArticleTextStyle.Heading
            : isBlockquote
                ? BilibiliArticleTextStyle.Quote
                : !string.IsNullOrWhiteSpace(list)
                    ? BilibiliArticleTextStyle.ListItem
                    : BilibiliArticleTextStyle.Normal;
        var normalized = style == BilibiliArticleTextStyle.ListItem && !text.TrimStart().StartsWith('•')
            ? "• " + text
            : text;

        blocks.Add(new BilibiliArticleBlock
        {
            Type = BilibiliArticleBlockType.Text,
            TextStyle = style,
            HeadingLevel = header,
            IndentLevel = Math.Clamp(attrs?.GetInt32OrDefault("indent") ?? 0, 0, 6),
            IsBold = (attrs?.GetBoolOrDefault("bold") ?? false) || header > 0,
            TextColor = NormalizeHexColor(attrs?.GetStringOrDefault("color")),
            Text = normalized,
        });
    }

    private static void FlushPendingQuillText(List<BilibiliArticleBlock> blocks, ref string pendingText)
    {
        if (!string.IsNullOrWhiteSpace(pendingText))
        {
            AddQuillTextBlock(blocks, pendingText, null);
        }

        pendingText = string.Empty;
    }

    private static void AppendMissingImageBlocks(List<BilibiliArticleBlock> blocks, IEnumerable<string> imageUrls)
    {
        var existing = blocks.Where(i => i.Type == BilibiliArticleBlockType.Image).Select(i => i.Url).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var url in imageUrls)
        {
            if (existing.Add(url))
            {
                blocks.Add(new BilibiliArticleBlock { Type = BilibiliArticleBlockType.Image, Url = url });
            }
        }
    }

    private static string NormalizeHtmlText(string html)
    {
        var text = BrRegex().Replace(html, "\n");
        text = TagRegex().Replace(text, string.Empty);
        text = WebUtility.HtmlDecode(text);
        text = Regex.Replace(text, "[ \t\u00A0]+", " ");
        text = Regex.Replace(text, "\\n{3,}", "\n\n").Trim();
        return text;
    }

    private static int ExtractHeadingLevel(string tag, string attrs)
    {
        if (tag.Length == 2 && tag[0] == 'h' && char.IsDigit(tag[1]))
        {
            return Math.Clamp(tag[1] - '0', 1, 6);
        }

        var qlHeader = Regex.Match(attrs, "ql-(?:header|size)-(\\d+)", RegexOptions.IgnoreCase);
        if (qlHeader.Success && int.TryParse(qlHeader.Groups[1].Value, out var level))
        {
            return Math.Clamp(level, 1, 6);
        }

        return 2;
    }

    private static bool ContainsBoldMarkup(string html)
    {
        return Regex.IsMatch(html, "<(strong|b)\\b", RegexOptions.IgnoreCase)
               || Regex.IsMatch(html, "font-weight\\s*:\\s*(bold|[6-9]00)", RegexOptions.IgnoreCase);
    }

    private static string? ExtractInnerColor(string html)
    {
        foreach (Match match in Regex.Matches(html, "color\\s*:\\s*(#[0-9a-fA-F]{3,8})", RegexOptions.IgnoreCase))
        {
            var color = NormalizeHexColor(match.Groups[1].Value);
            if (color is not null)
            {
                return color;
            }
        }

        return null;
    }

    private static string? ExtractCssColor(string attrs)
    {
        var styleColor = Regex.Match(attrs, "color\\s*:\\s*(#[0-9a-fA-F]{3,8})", RegexOptions.IgnoreCase);
        if (styleColor.Success)
        {
            return NormalizeHexColor(styleColor.Groups[1].Value);
        }

        var dataColor = Regex.Match(attrs, "(?:data-color|color)=[\"'](#[0-9a-fA-F]{3,8})[\"']", RegexOptions.IgnoreCase);
        return dataColor.Success ? NormalizeHexColor(dataColor.Groups[1].Value) : null;
    }

    private static string? NormalizeHexColor(string? color)
    {
        if (string.IsNullOrWhiteSpace(color))
        {
            return null;
        }

        color = color.Trim();
        if (!color.StartsWith('#'))
        {
            return null;
        }

        if (color.Length == 4)
        {
            return "#" + string.Concat(color.Skip(1).SelectMany(c => new[] { c, c }));
        }

        return color.Length is 7 or 9 ? color : null;
    }

    private static int ExtractIndentLevel(string attrs)
    {
        var ql = Regex.Match(attrs, "ql-indent-(\\d+)", RegexOptions.IgnoreCase);
        if (ql.Success && int.TryParse(ql.Groups[1].Value, out var indent))
        {
            return Math.Clamp(indent, 0, 6);
        }

        var margin = Regex.Match(attrs, "(?:margin-left|padding-left)\\s*:\\s*(\\d+)", RegexOptions.IgnoreCase);
        if (margin.Success && int.TryParse(margin.Groups[1].Value, out var px))
        {
            return Math.Clamp(px / 24, 0, 6);
        }

        return 0;
    }

    private static IEnumerable<string> SplitText(string text, int maxLength)
    {
        text = text.Trim();
        for (var i = 0; i < text.Length; i += maxLength)
        {
            yield return text.Substring(i, Math.Min(maxLength, text.Length - i));
        }
    }

    private void ApplyHeaders(HttpRequestMessage request, string referer)
    {
        request.Headers.TryAddWithoutValidation("User-Agent", BilibiliConstants.UserAgent);
        request.Headers.TryAddWithoutValidation("Referer", referer);
        request.Headers.TryAddWithoutValidation("Origin", BilibiliConstants.Origin);
        request.Headers.TryAddWithoutValidation("Accept", "application/json, text/plain, */*");
        if (!string.IsNullOrWhiteSpace(MyParserRuntime.BilibiliCookie))
        {
            request.Headers.TryAddWithoutValidation("Cookie", MyParserRuntime.BilibiliCookie);
        }
    }

    private static string? HtmlToPlainText(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return null;
        }

        var text = html;
        text = BrRegex().Replace(text, "\n");
        text = BlockEndRegex().Replace(text, "\n");
        text = TagRegex().Replace(text, string.Empty);
        text = WebUtility.HtmlDecode(text);
        text = MultiBlankLineRegex().Replace(text, "\n\n").Trim();
        return text;
    }

    [GeneratedRegex("<(p|div|h[1-6]|blockquote|li|pre|code)([^>]*)>(.*?)</\\1>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex HtmlBlockRegex();

    [GeneratedRegex("<br\\s*/?>", RegexOptions.IgnoreCase)]
    private static partial Regex BrRegex();

    [GeneratedRegex("</(p|div|h[1-6]|li|blockquote)>", RegexOptions.IgnoreCase)]
    private static partial Regex BlockEndRegex();

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex TagRegex();

    [GeneratedRegex("\\n{3,}")]
    private static partial Regex MultiBlankLineRegex();
}
