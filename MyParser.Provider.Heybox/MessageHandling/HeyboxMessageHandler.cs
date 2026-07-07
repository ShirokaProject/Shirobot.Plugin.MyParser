using System.Diagnostics;
using System.Text;
using Avalonia.Media.Imaging;
using MyParser.Provider.Heybox.Models;
using MyParser.Provider.Heybox.Parsing;
using MyParser.Provider.Heybox.Views;
using ShiroBot.AvaloniaSdk;
using ShiroBot.Model.Common;
using ShiroBot.SDK.Core;
using ShiroBot.SDK.Plugin;

namespace MyParser.Provider.Heybox.MessageHandling;

internal sealed class HeyboxMessageHandler(ProviderMessageHandlerContext context) : ProviderMessageHandlerBase(context)
{
    public override string ProviderId => "heybox";

    public override async Task ParseAndReplyAsync(IncomingMessage message, string text, bool silentProviderMismatch = false)
    {
        if (!Config.EnableHeybox)
        {
            if (!silentProviderMismatch)
            {
                await ReplyAsync(message, "小黑盒解析已关闭。");
            }

            return;
        }

        await ReactAsync(message, "351", "小黑盒");
        try
        {
            var media = await ProviderRegistry.ParseAsync(text);
            if (media.ProviderPayload is not HeyboxParseResult result)
            {
                await ReplyAsync(message, "小黑盒链接已识别，但解析结果类型异常。");
                await ReactAsync(message, "9", "小黑盒");
                return;
            }

            await SendInfoCardAsync(message, result);
            await SendHeyboxArticleAsync(message, result);
            await SendArticleDocumentCardAsync(message, result);
            await ReactAsync(message, "426", "小黑盒");
        }
        catch (HeyboxParseException ex)
        {
            BotLog.Warning($"MyParser 小黑盒解析失败：{ex.Message}");
            await ReplyAsync(message, "小黑盒解析失败：" + ex.Message);
            await ReactAsync(message, "9", "小黑盒");
        }
        catch (Exception ex)
        {
            BotLog.Warning($"MyParser 小黑盒解析异常：{ex}");
            await ReplyAsync(message, "小黑盒解析异常：" + ex.Message);
            await ReactAsync(message, "9", "小黑盒");
        }
    }

    private async Task SendInfoCardAsync(IncomingMessage message, HeyboxParseResult result)
    {
        var uri = await BuildInfoCardUriAsync(result);
        if (string.IsNullOrWhiteSpace(uri))
        {
            return;
        }

        var segment = new ImageOutgoingSegment(uri);
        var stopwatch = Stopwatch.StartNew();
        BotLog.Info($"MyParser 小黑盒信息卡片 ImageSegment 发送开始: link_id={result.LinkId}, scene={GetMessageScene(message)}, uri_preview={HostServices.PreviewUri(uri)}");
        switch (message)
        {
            case GroupIncomingMessage group:
            {
                var response = await BotContext.Message.SendGroupMessageAsync(group.Group.GroupId, segment);
                BotLog.Info($"MyParser 小黑盒信息卡片 ImageSegment 发送接口完成: link_id={result.LinkId}, scene=group, group_id={group.Group.GroupId}, message_seq={response.MessageSeq}, elapsed={stopwatch.Elapsed:mm\\:ss}");
                break;
            }
            case FriendIncomingMessage friend:
            {
                var response = await BotContext.Message.SendPrivateMessageAsync(friend.SenderId, segment);
                BotLog.Info($"MyParser 小黑盒信息卡片 ImageSegment 发送接口完成: link_id={result.LinkId}, scene=friend, user_id={friend.SenderId}, message_seq={response.MessageSeq}, elapsed={stopwatch.Elapsed:mm\\:ss}");
                break;
            }
            default:
            {
                var response = await BotContext.Message.ReplyAsync(message, segment);
                BotLog.Info($"MyParser 小黑盒信息卡片 ImageSegment 发送接口完成: link_id={result.LinkId}, scene=reply, message_seq={response.MessageSeq}, elapsed={stopwatch.Elapsed:mm\\:ss}");
                break;
            }
        }
    }

    private async Task<string> BuildInfoCardUriAsync(HeyboxParseResult result)
    {
        var coverUrl = result.CoverUrl ?? result.ImageUrls.FirstOrDefault();
        var cover = await HostServices.BuildProviderImageAsync(new ProviderImageBuildRequest(
            "小黑盒",
            coverUrl,
            result.SourceUrl,
            $"heybox_cover_{result.LinkId}"));

        if (BotContext.Render is null)
        {
            return cover.Uri;
        }

        try
        {
            var coverBitmap = !string.IsNullOrWhiteSpace(cover.LocalPath)
                ? HostServices.DecodeImageFileForRender(cover.LocalPath)
                : HostServices.DecodeBase64ImageForRender(cover.Uri);
            var vm = new HeyboxCardViewModel
            {
                Cover = coverBitmap,
                Title = string.IsNullOrWhiteSpace(result.Title) ? "小黑盒帖子" : result.Title!,
                Description = string.IsNullOrWhiteSpace(result.Description) ? "该帖子没有返回正文摘要。" : result.Description!,
                AuthorName = string.IsNullOrWhiteSpace(result.AuthorName) ? "小黑盒用户" : result.AuthorName!,
                StatsText = BuildStatsText(result),
                MediaText = $"图片 {result.ImageUrls.Count} 张 · 视频 {result.VideoUrls.Count} 个",
                LinkId = result.LinkId,
                SourceText = "小黑盒 / Heybox",
            };
            var png = await BotContext.RenderControlPngAsync<HeyboxCard>(vm, new ControlRenderOptions(RenderTheme.Auto));
            BotLog.Info($"MyParser 小黑盒信息卡片渲染完成: link_id={result.LinkId}, png_kb={png.Length / 1024d:F1}, mode=base64");
            return "base64://" + Convert.ToBase64String(png);
        }
        catch (Exception ex)
        {
            BotLog.Warning($"MyParser 小黑盒信息卡片渲染失败，直接发送封面: link_id={result.LinkId}, error={ex.Message}");
            return cover.Uri;
        }
    }

    private async Task SendHeyboxArticleAsync(IncomingMessage message, HeyboxParseResult result)
    {
        var senderId = GetBotOrSenderId(message);
        var senderName = string.IsNullOrWhiteSpace(result.AuthorName) ? "小黑盒" : result.AuthorName!;
        var forwarded = new List<OutgoingForwardedMessage>
        {
            new(senderId, senderName, [new TextOutgoingSegment(BuildHeaderText(result))])
        };

        var blocks = BuildForwardBlocks(result).ToArray();
        var imageBlocks = blocks
            .Select((block, index) => (Block: block, SourceIndex: index))
            .Where(i => i.Block.Type == HeyboxArticleBlockType.Image)
            .Select((item, imageIndex) => (item.Block, item.SourceIndex, ImageIndex: imageIndex + 1))
            .Take(30)
            .ToArray();
        var downloadedImages = await HostServices.SelectParallelOrderedAsync(
            imageBlocks,
            6,
            item => HostServices.BuildProviderImageAsync(new ProviderImageBuildRequest(
                "小黑盒文章图片",
                item.Block.Url,
                result.SourceUrl,
                $"heybox_article_{result.LinkId}_{item.ImageIndex:D2}")));
        var imageBySourceIndex = imageBlocks.Zip(downloadedImages, (item, image) => (item.SourceIndex, image))
            .ToDictionary(i => i.SourceIndex, i => i.image);

        foreach (var (block, sourceIndex) in blocks.Select((block, index) => (block, index)))
        {
            switch (block.Type)
            {
                case HeyboxArticleBlockType.Image:
                    if (imageBySourceIndex.TryGetValue(sourceIndex, out var image) && !string.IsNullOrWhiteSpace(image.Uri))
                    {
                        var segments = new List<OutgoingSegment> { new ImageOutgoingSegment(image.Uri) };
                        if (!string.IsNullOrWhiteSpace(block.Caption))
                        {
                            segments.Add(new TextOutgoingSegment(block.Caption));
                        }

                        forwarded.Add(new OutgoingForwardedMessage(senderId, senderName, segments));
                    }
                    break;
                case HeyboxArticleBlockType.Video:
                    if (!string.IsNullOrWhiteSpace(block.Url))
                    {
                        forwarded.Add(new OutgoingForwardedMessage(senderId, senderName, [new TextOutgoingSegment("视频直链：\n" + block.Url)]));
                    }
                    break;
                default:
                    if (!string.IsNullOrWhiteSpace(block.Text))
                    {
                        foreach (var chunk in ProviderTextUtilities.SplitText(block.Text, 1200))
                        {
                            forwarded.Add(new OutgoingForwardedMessage(senderId, senderName, [new TextOutgoingSegment(chunk)]));
                        }
                    }
                    break;
            }
        }

        forwarded.Add(new OutgoingForwardedMessage(senderId, senderName, [new TextOutgoingSegment("原文：" + result.SourceUrl)]));

        if (forwarded.Count > 1)
        {
            var title = ProviderTextUtilities.TrimLine(result.Title ?? "小黑盒文章", 48);
            var preview = new[]
            {
                result.IsArticle ? "小黑盒文章" : "小黑盒帖子",
                string.IsNullOrWhiteSpace(result.AuthorName) ? "小黑盒" : result.AuthorName!,
                $"图片 {result.ImageUrls.Count} 张 / 视频 {result.VideoUrls.Count} 个",
            };
            var summary = result.IsArticle
                ? $"完整正文 + {result.ImageUrls.Count} 张图"
                : BuildStatsText(result);
            var forward = new ForwardOutgoingSegment(forwarded, title, preview, summary, result.IsArticle ? "小黑盒文章" : "小黑盒帖子");
            switch (message)
            {
                case GroupIncomingMessage group:
                    await BotContext.Message.SendGroupMessageAsync(group.Group.GroupId, forward);
                    return;
                case FriendIncomingMessage friend:
                    await BotContext.Message.SendPrivateMessageAsync(friend.SenderId, forward);
                    return;
            }
        }

        await ReplyAsync(message, FormatHeyboxResult(result));
    }

    private async Task SendArticleDocumentCardAsync(IncomingMessage message, HeyboxParseResult result)
    {
        if (!result.IsArticle && result.Blocks.Count == 0)
        {
            return;
        }

        var cardUri = await BuildArticleDocumentCardUriAsync(result);
        if (string.IsNullOrWhiteSpace(cardUri))
        {
            return;
        }

        var segment = new ImageOutgoingSegment(cardUri);
        var stopwatch = Stopwatch.StartNew();
        BotLog.Info($"MyParser 小黑盒完整文档卡片 ImageSegment 发送开始: link_id={result.LinkId}, scene={GetMessageScene(message)}, uri_preview={HostServices.PreviewUri(cardUri)}");
        switch (message)
        {
            case GroupIncomingMessage group:
            {
                var response = await BotContext.Message.SendGroupMessageAsync(group.Group.GroupId, segment);
                BotLog.Info($"MyParser 小黑盒完整文档卡片 ImageSegment 发送接口完成: link_id={result.LinkId}, scene=group, group_id={group.Group.GroupId}, message_seq={response.MessageSeq}, elapsed={stopwatch.Elapsed:mm\\:ss}");
                break;
            }
            case FriendIncomingMessage friend:
            {
                var response = await BotContext.Message.SendPrivateMessageAsync(friend.SenderId, segment);
                BotLog.Info($"MyParser 小黑盒完整文档卡片 ImageSegment 发送接口完成: link_id={result.LinkId}, scene=friend, user_id={friend.SenderId}, message_seq={response.MessageSeq}, elapsed={stopwatch.Elapsed:mm\\:ss}");
                break;
            }
            default:
            {
                var response = await BotContext.Message.ReplyAsync(message, segment);
                BotLog.Info($"MyParser 小黑盒完整文档卡片 ImageSegment 发送接口完成: link_id={result.LinkId}, scene=reply, message_seq={response.MessageSeq}, elapsed={stopwatch.Elapsed:mm\\:ss}");
                break;
            }
        }
    }

    private async Task<string> BuildArticleDocumentCardUriAsync(HeyboxParseResult result)
    {
        if (BotContext.Render is null)
        {
            return string.Empty;
        }

        try
        {
            var avatarTask = HostServices.BuildProviderImageAsync(new ProviderImageBuildRequest(
                "小黑盒作者头像",
                result.AuthorAvatarUrl ?? result.CoverUrl ?? result.ImageUrls.FirstOrDefault(),
                result.SourceUrl,
                $"heybox_article_avatar_{result.LinkId}"));
            var coverTask = HostServices.BuildProviderImageAsync(new ProviderImageBuildRequest(
                "小黑盒文章封面",
                result.CoverUrl ?? result.ImageUrls.FirstOrDefault(),
                result.SourceUrl,
                $"heybox_article_cover_{result.LinkId}"));
            var renderBlocks = BuildDocumentBlocksForRender(result).Take(90).ToArray();
            var renderImageBlocks = renderBlocks
                .Select((block, index) => (Block: block, SourceIndex: index))
                .Where(i => i.Block.Type == HeyboxArticleBlockType.Image)
                .Select((item, imageIndex) => (item.Block, item.SourceIndex, ImageIndex: imageIndex + 1))
                .Take(36)
                .ToArray();
            var renderImages = await HostServices.SelectParallelOrderedAsync(
                renderImageBlocks,
                6,
                item => HostServices.BuildProviderImageAsync(new ProviderImageBuildRequest(
                    "小黑盒文档图片",
                    item.Block.Url,
                    result.SourceUrl,
                    $"heybox_doc_img_{item.ImageIndex:D2}_{result.LinkId}")));
            var renderImageBySourceIndex = renderImageBlocks.Zip(renderImages, (item, image) => (item.SourceIndex, image))
                .ToDictionary(i => i.SourceIndex, i => i.image);
            var avatarImage = await avatarTask;
            var coverImage = await coverTask;
            var blocks = new List<HeyboxArticleDocumentBlockViewModel>();
            var estimatedHeight = 500;
            foreach (var (block, sourceIndex) in renderBlocks.Select((block, index) => (block, index)))
            {
                if (block.Type == HeyboxArticleBlockType.Image)
                {
                    if (!renderImageBySourceIndex.TryGetValue(sourceIndex, out var image) || string.IsNullOrWhiteSpace(image.Uri))
                    {
                        continue;
                    }

                    var bitmap = !string.IsNullOrWhiteSpace(image.LocalPath)
                        ? HostServices.DecodeImageFileForRender(image.LocalPath)
                        : HostServices.DecodeBase64ImageForRender(image.Uri);
                    var height = EstimateImageBlockHeight(bitmap);
                    blocks.Add(new HeyboxArticleDocumentBlockViewModel
                    {
                        IsImage = true,
                        Image = bitmap,
                        Caption = string.IsNullOrWhiteSpace(block.Caption) ? $"图 {blocks.Count(i => i.IsImage) + 1}" : block.Caption,
                        Height = height,
                    });
                    estimatedHeight += height + (string.IsNullOrWhiteSpace(block.Caption) ? 30 : 42);
                }
                else if (block.Type == HeyboxArticleBlockType.Video && !string.IsNullOrWhiteSpace(block.Url))
                {
                    var videoText = string.IsNullOrWhiteSpace(block.Caption) ? "视频直链：" + block.Url : block.Caption + "\n" + block.Url;
                    var videoBlock = CreateStyledTextBlockViewModel(new HeyboxArticleBlock
                    {
                        Type = HeyboxArticleBlockType.Text,
                        TextStyle = HeyboxArticleTextStyle.Quote,
                        Text = videoText,
                    }, videoText);
                    blocks.Add(videoBlock);
                    estimatedHeight += EstimateTextBlockHeight(videoText, videoBlock.FontSize, videoBlock.LineHeight, 624) + 18;
                }
                else if (!string.IsNullOrWhiteSpace(block.Text))
                {
                    foreach (var chunk in ProviderTextUtilities.SplitText(block.Text, 520))
                    {
                        var styled = CreateStyledTextBlockViewModel(block, chunk);
                        blocks.Add(styled);
                        estimatedHeight += EstimateTextBlockHeight(chunk, styled.FontSize, styled.LineHeight, 624) + 18;
                    }
                }
            }

            var canvasHeight = Math.Clamp(estimatedHeight + 96, 980, 12000);
            var vm = new HeyboxArticleDocumentViewModel
            {
                CanvasHeight = canvasHeight,
                Avatar = !string.IsNullOrWhiteSpace(avatarImage.LocalPath) ? HostServices.DecodeImageFileForRender(avatarImage.LocalPath) : HostServices.DecodeBase64ImageForRender(avatarImage.Uri),
                Cover = !string.IsNullOrWhiteSpace(coverImage.LocalPath) ? HostServices.DecodeImageFileForRender(coverImage.LocalPath) : HostServices.DecodeBase64ImageForRender(coverImage.Uri),
                KindText = result.IsArticle ? "小黑盒文章" : "小黑盒帖子",
                Title = string.IsNullOrWhiteSpace(result.Title) ? "小黑盒文章" : result.Title!,
                AuthorName = string.IsNullOrWhiteSpace(result.AuthorName) ? "小黑盒用户" : result.AuthorName!,
                MetaText = BuildArticleAuthorMeta(result),
                StatsText = BuildStatsText(result),
                Blocks = blocks,
            };
            var png = await BotContext.RenderControlPngAsync<HeyboxArticleDocument>(vm, new ControlRenderOptions(RenderTheme.Dark));
            BotLog.Info($"MyParser 小黑盒完整文档卡片渲染完成: link_id={result.LinkId}, blocks={blocks.Count}, height={canvasHeight}, png_kb={png.Length / 1024d:F1}, mode=base64");
            return "base64://" + Convert.ToBase64String(png);
        }
        catch (Exception ex)
        {
            BotLog.Warning($"MyParser 小黑盒完整文档卡片渲染失败: link_id={result.LinkId}, error={ex}");
            return string.Empty;
        }
    }

    private static HeyboxArticleDocumentBlockViewModel CreateStyledTextBlockViewModel(HeyboxArticleBlock block, string text)
    {
        var vm = new HeyboxArticleDocumentBlockViewModel
        {
            Text = text,
            HeadingLevel = block.HeadingLevel,
            Margin = new Avalonia.Thickness(0),
        };

        return block.TextStyle switch
        {
            HeyboxArticleTextStyle.Heading => CreateHeadingStyle(vm, block.HeadingLevel),
            HeyboxArticleTextStyle.Quote => WithStyle(vm, 15, 24, "#FFE2E8F0", "#221E293B", "#6638BDF8", "SemiBold", accentBrush: "#FF38BDF8", accentWidth: 5),
            HeyboxArticleTextStyle.ListItem => WithStyle(vm, 15, 24, "#FFF4F7FB", "#16111827", "#330BC5EA", block.IsBold ? "SemiBold" : "Normal", accentBrush: "#990BC5EA", accentWidth: 3),
            _ when block.IsBold => WithStyle(vm, 15, 24, "#FFFFFFFF", "#191E293B", "#440BC5EA", "SemiBold", accentBrush: "#CC0BC5EA", accentWidth: 4),
            _ => WithStyle(vm, 15, 24, "#FFF4F7FB", "Transparent", "Transparent", "Normal"),
        };

        static HeyboxArticleDocumentBlockViewModel CreateHeadingStyle(HeyboxArticleDocumentBlockViewModel source, int headingLevel)
        {
            return headingLevel switch
            {
                <= 1 => WithStyle(source, 27, 36, "#FFFFFFFF", "#330BC5EA", "#990BC5EA", "Bold", accentBrush: "#FF0BC5EA", accentWidth: 7),
                2 => WithStyle(source, 23, 32, "#FFFFFFFF", "#260BC5EA", "#770BC5EA", "Bold", accentBrush: "#FF38BDF8", accentWidth: 6),
                3 => WithStyle(source, 20, 29, "#FFEAF6FF", "#1E1E293B", "#6638BDF8", "SemiBold", accentBrush: "#FF38BDF8", accentWidth: 5),
                _ => WithStyle(source, 18, 27, "#FFEAF6FF", "#191E293B", "#5538BDF8", "SemiBold", accentBrush: "#CC38BDF8", accentWidth: 4),
            };
        }

        static HeyboxArticleDocumentBlockViewModel WithStyle(HeyboxArticleDocumentBlockViewModel source, int fontSize, int lineHeight, string foreground, string background, string borderBrush, string fontWeight, string accentBrush = "Transparent", int accentWidth = 0)
        {
            return new HeyboxArticleDocumentBlockViewModel
            {
                Text = source.Text,
                HeadingLevel = source.HeadingLevel,
                Margin = source.Margin,
                FontSize = fontSize,
                LineHeight = lineHeight,
                Foreground = foreground,
                Background = background,
                BorderBrush = borderBrush,
                FontWeight = fontWeight,
                AccentBrush = accentBrush,
                AccentWidth = accentWidth,
            };
        }
    }

    private static IEnumerable<HeyboxArticleBlock> BuildDocumentBlocksForRender(HeyboxParseResult result)
    {
        return BuildForwardBlocks(result);
    }

    private static int EstimateImageBlockHeight(Bitmap? image, int contentWidth = 624)
    {
        if (image?.PixelSize.Width is not > 0 || image.PixelSize.Height <= 0)
        {
            return 320;
        }

        var height = (int)Math.Round(contentWidth * image.PixelSize.Height / (double)image.PixelSize.Width);
        return Math.Clamp(height, 260, 920);
    }

    private static int EstimateTextBlockHeight(string text, int fontSize, int lineHeight, int width)
    {
        var charsPerLine = Math.Max(12, width / Math.Max(1, fontSize));
        var lines = text.Split('\n').Sum(line => Math.Max(1, (int)Math.Ceiling(line.Length / (double)charsPerLine)));
        return Math.Clamp(lines * lineHeight + 6, lineHeight + 8, 1200);
    }

    private static string BuildArticleAuthorMeta(HeyboxParseResult result)
    {
        var parts = new List<string>();
        if (result.PublishTime is not null) parts.Add($"{result.PublishTime:yyyy-MM-dd HH:mm}");
        if (result.Topics.Count > 0) parts.Add(string.Join(" / ", result.Topics.Take(3)));
        if (!string.IsNullOrWhiteSpace(result.PlainText)) parts.Add($"{result.PlainText!.Length:N0}字");
        if (result.ImageUrls.Count > 0) parts.Add($"{result.ImageUrls.Count}图");
        return parts.Count > 0 ? string.Join(" · ", parts) : (result.IsArticle ? "小黑盒文章" : "小黑盒帖子");
    }

    private static IEnumerable<HeyboxArticleBlock> BuildForwardBlocks(HeyboxParseResult result)
    {
        if (result.Blocks.Count > 0)
        {
            return result.Blocks;
        }

        var blocks = new List<HeyboxArticleBlock>();
        if (!string.IsNullOrWhiteSpace(result.PlainText))
        {
            blocks.AddRange(ProviderTextUtilities.SplitText(result.PlainText!, 900).Select(i => new HeyboxArticleBlock { Type = HeyboxArticleBlockType.Text, Text = i }));
        }
        else if (!string.IsNullOrWhiteSpace(result.Description))
        {
            blocks.Add(new HeyboxArticleBlock { Type = HeyboxArticleBlockType.Text, Text = result.Description! });
        }

        blocks.AddRange(result.ImageUrls.Select(url => new HeyboxArticleBlock { Type = HeyboxArticleBlockType.Image, Url = url }));
        blocks.AddRange(result.VideoUrls.Select(url => new HeyboxArticleBlock { Type = HeyboxArticleBlockType.Video, Url = url }));
        return blocks;
    }

    private static string BuildHeaderText(HeyboxParseResult result)
    {
        var sb = new StringBuilder();
        sb.AppendLine(result.IsArticle ? "小黑盒文章" : "小黑盒解析");
        if (!string.IsNullOrWhiteSpace(result.Title))
        {
            sb.AppendLine("标题：" + result.Title);
        }

        if (!string.IsNullOrWhiteSpace(result.AuthorName))
        {
            sb.AppendLine("作者：" + result.AuthorName + (string.IsNullOrWhiteSpace(result.AuthorId) ? string.Empty : $" ({result.AuthorId})"));
        }

        if (result.PublishTime is not null)
        {
            sb.AppendLine($"发布时间：{result.PublishTime:yyyy-MM-dd HH:mm}");
        }

        if (result.Topics.Count > 0)
        {
            sb.AppendLine("话题：" + string.Join(" / ", result.Topics.Take(4)));
        }

        if (!string.IsNullOrWhiteSpace(result.Description))
        {
            sb.AppendLine();
            sb.AppendLine(result.Description);
        }

        sb.AppendLine();
        sb.AppendLine(BuildStatsText(result));
        return sb.ToString().TrimEnd();
    }

    private static string FormatHeyboxResult(HeyboxParseResult result)
    {
        var sb = new StringBuilder(BuildHeaderText(result));
        if (!string.IsNullOrWhiteSpace(result.PlainText) && !string.Equals(result.PlainText, result.Description, StringComparison.Ordinal))
        {
            sb.AppendLine();
            sb.AppendLine(ProviderTextUtilities.TrimLine(result.PlainText!, 1800));
        }

        if (result.ImageUrls.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("图片：" + string.Join("\n", result.ImageUrls.Take(6)));
        }

        if (result.VideoUrls.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("视频：" + string.Join("\n", result.VideoUrls.Take(4)));
        }

        sb.AppendLine();
        sb.AppendLine(result.SourceUrl);
        return sb.ToString().TrimEnd();
    }

    private static string BuildStatsText(HeyboxParseResult result)
    {
        var parts = new List<string>();
        if (result.ViewCount is not null)
        {
            parts.Add($"阅读 {result.ViewCount:N0}");
        }

        if (result.LikeCount is not null)
        {
            parts.Add($"点赞 {result.LikeCount:N0}");
        }

        if (result.CommentCount is not null)
        {
            parts.Add($"评论 {result.CommentCount:N0}");
        }

        if (result.FavoriteCount is not null)
        {
            parts.Add($"收藏 {result.FavoriteCount:N0}");
        }

        if (result.ShareCount is not null)
        {
            parts.Add($"转发 {result.ShareCount:N0}");
        }

        parts.Add($"图片 {result.ImageUrls.Count}");
        parts.Add($"视频 {result.VideoUrls.Count}");
        if (!string.IsNullOrWhiteSpace(result.SourceKind))
        {
            parts.Add("来源 " + result.SourceKind);
        }

        return string.Join(" · ", parts);
    }
}
