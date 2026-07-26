using System.Diagnostics;
using System.Net;
using System.Text;
using ShiroBot.AvaloniaSdk;
using Shirobot.Plugin.MyParser.Providers.Common.MessageHandling;
using Shirobot.Plugin.MyParser.Providers.Bilibili.Models;
using Shirobot.Plugin.MyParser.Providers.Bilibili.ViewModels;
using Shirobot.Plugin.MyParser.Providers.Bilibili.Views;
using Shirobot.Plugin.MyParser.Utility;
using ShiroBot.Qq.Model;
using ShiroBot.SDK.Abstractions;
using ShiroBot.SDK.Models;
using ShiroBot.SDK.Plugin;

namespace Shirobot.Plugin.MyParser.Providers.Bilibili.Impl.MessageHandling.Impl;

internal sealed partial class BilibiliMessageHandler
{
private async Task SendArticleForwardAsync(MessageEvent message, BilibiliArticleParseResult result)
    {
        var senderId = GetBotOrSenderId(message);
        var senderName = string.IsNullOrWhiteSpace(result.AuthorName) ? GetArticleKindText(result) : result.AuthorName!;
        var forwarded = new List<QqForwardedMessage>();

        forwarded.Add(new QqForwardedMessage(senderId, senderName, [new QqTextOutgoing(BuildArticleHeaderText(result))]));

        var forwardBlocks = BuildForwardBlocks(result).ToArray();
        var imageBlocks = forwardBlocks
            .Select((block, index) => (Block: block, SourceIndex: index))
            .Where(i => i.Block.Type == BilibiliArticleBlockType.Image)
            .Select((item, imageIndex) => (item.Block, item.SourceIndex, ImageIndex: imageIndex + 1))
            .ToArray();
        var downloadedImages = await MessageFetchConcurrency.SelectParallelOrderedAsync(
            imageBlocks,
            MessageFetchConcurrency.DefaultImageConcurrency,
            item => BuildRemoteImageAsync(item.Block.Url, result.SourceUrl, $"bilibili_article_{result.Cvid}_{item.ImageIndex:D2}"));
        var imageBySourceIndex = imageBlocks.Zip(downloadedImages, (item, image) => (item.SourceIndex, image))
            .ToDictionary(i => i.SourceIndex, i => i.image);

        foreach (var (block, sourceIndex) in forwardBlocks.Select((block, index) => (block, index)))
        {
            if (block.Type == BilibiliArticleBlockType.Image)
            {
                if (imageBySourceIndex.TryGetValue(sourceIndex, out var image) && !string.IsNullOrWhiteSpace(image.Uri))
                {
                    var segments = new List<QqOutgoingSegment> { new QqImageOutgoing(image.Uri) };
                    if (!string.IsNullOrWhiteSpace(block.Caption))
                    {
                        segments.Add(new QqTextOutgoing(block.Caption));
                    }

                    forwarded.Add(new QqForwardedMessage(senderId, senderName, segments));
                }

                continue;
            }

            if (string.IsNullOrWhiteSpace(block.Text))
            {
                continue;
            }

            foreach (var chunk in SplitText(block.Text, 1200))
            {
                forwarded.Add(new QqForwardedMessage(senderId, senderName, [new QqTextOutgoing(chunk)]));
            }
        }

        if (!string.IsNullOrWhiteSpace(result.SourceUrl))
        {
            forwarded.Add(new QqForwardedMessage(senderId, senderName, [new QqTextOutgoing("原文：" + result.SourceUrl)]));
        }

        if (forwarded.Count == 0)
        {
            await ReplyAsync(message, FormatBilibiliArticleResult(result));
            return;
        }

        var title = string.IsNullOrWhiteSpace(result.Title) ? GetArticleKindText(result) : TrimLine(result.Title!, 48);
        var preview = new[]
        {
            GetArticleKindText(result),
            string.IsNullOrWhiteSpace(result.AuthorName) ? "Bilibili" : result.AuthorName!,
            result.ImageUrls.Count > 0 ? $"图片 {result.ImageUrls.Count} 张" : "正文摘要",
        };
        var summary = $"完整正文 + {result.ImageUrls.Count} 张图";
        var forward = MessageHandlerCommon.BuildForwardSegment(forwarded, title, preview, summary, GetArticleKindText(result));
        await context.Message.ReplyAsync(message, forward);
    }

    private async Task SendArticleDocumentCardAsync(MessageEvent message, BilibiliArticleParseResult result)
    {
        var cardUri = await BuildArticleDocumentCardUriAsync(result);
        if (string.IsNullOrWhiteSpace(cardUri))
        {
            return;
        }

        await context.Message.ReplyAsync(message, new ImageSegment(cardUri));
    }

    private async Task<string> BuildArticleDocumentCardUriAsync(BilibiliArticleParseResult result)
    {
        if (context.Render is null)
        {
            return string.Empty;
        }

        try
        {
            var avatarTask = BuildRemoteImageAsync(result.AuthorAvatarUrl, result.SourceUrl, $"bilibili_article_avatar_{result.Cvid}_{result.OpusId}");
            var renderBlocks = BuildDocumentBlocksForRender(result).Take(80).ToArray();
            var renderImageBlocks = renderBlocks
                .Select((block, index) => (Block: block, SourceIndex: index))
                .Where(i => i.Block.Type == BilibiliArticleBlockType.Image)
                .Select((item, imageIndex) => (item.Block, item.SourceIndex, ImageIndex: imageIndex + 1))
                .ToArray();
            var renderImages = await MessageFetchConcurrency.SelectParallelOrderedAsync(
                renderImageBlocks,
                MessageFetchConcurrency.DefaultImageConcurrency,
                item => BuildRemoteImageAsync(item.Block.Url, result.SourceUrl, $"bilibili_doc_img_{item.ImageIndex:D2}_{result.Cvid}_{result.OpusId}"));
            var renderImageBySourceIndex = renderImageBlocks.Zip(renderImages, (item, image) => (item.SourceIndex, image))
                .ToDictionary(i => i.SourceIndex, i => i.image);
            var avatarImage = await avatarTask;
            var blocks = new List<BiliArticleDocumentBlockViewModel>();
            var estimatedHeight = 230;
            foreach (var (block, sourceIndex) in renderBlocks.Select((block, index) => (block, index)))
            {
                if (block.Type == BilibiliArticleBlockType.Image)
                {
                    if (!renderImageBySourceIndex.TryGetValue(sourceIndex, out var image) || string.IsNullOrWhiteSpace(image.Uri))
                    {
                        continue;
                    }

                    var height = 280;
                    blocks.Add(new BiliArticleDocumentBlockViewModel
                    {
                        IsImage = true,
                        Image = !string.IsNullOrWhiteSpace(image.LocalPath) ? RenderBitmapUtilities.DecodeImageFileForRender(image.LocalPath) : RenderBitmapUtilities.DecodeBase64ImageForRender(image.Uri),
                        Caption = string.IsNullOrWhiteSpace(block.Caption) ? $"图 {blocks.Count(i => i.IsImage) + 1}" : block.Caption,
                        Height = height,
                    });
                    estimatedHeight += height + 42;
                }
                else if (!string.IsNullOrWhiteSpace(block.Text))
                {
                    foreach (var chunk in SplitText(block.Text, 520))
                    {
                        var styled = CreateStyledTextBlockViewModel(block, chunk);
                        blocks.Add(styled);
                        estimatedHeight += EstimateTextBlockHeight(chunk, styled.FontSize, styled.LineHeight, Math.Max(300, 624 - styled.Indent)) + 18;
                    }
                }
            }

            var canvasHeight = Math.Clamp(estimatedHeight + 96, 900, 12000);
            var vm = new BiliArticleDocumentViewModel
            {
                CanvasHeight = canvasHeight,
                Avatar = !string.IsNullOrWhiteSpace(avatarImage.LocalPath) ? RenderBitmapUtilities.DecodeImageFileForRender(avatarImage.LocalPath) : RenderBitmapUtilities.DecodeBase64ImageForRender(avatarImage.Uri),
                KindText = GetArticleKindText(result),
                Title = string.IsNullOrWhiteSpace(result.Title) ? GetArticleKindText(result) : result.Title!,
                AuthorName = string.IsNullOrWhiteSpace(result.AuthorName) ? "未知作者" : result.AuthorName!,
                MetaText = BuildArticleAuthorMeta(result),
                StatsText = $"{FormatCount(result.ViewCount)}阅读 · {FormatCount(result.LikeCount)}赞 · {FormatCount(result.CoinCount)}投币 · {FormatCount(result.FavoriteCount)}收藏 · {FormatCount(result.ReplyCount)}评论 · {result.ImageUrls.Count}图",
                Blocks = blocks,
            };
            var png = await context.RenderControlPngAsync<BiliArticleDocument>(vm, new ControlRenderOptions(RenderTheme.Dark));
            BotLog.Info($"MyParser Bilibili 完整文档卡片渲染完成: id={(result.IsOpus ? result.OpusId : result.Cvid)}, blocks={blocks.Count}, height={canvasHeight}, png_kb={png.Length / 1024d:F1}, mode=base64");
            return "base64://" + Convert.ToBase64String(png);
        }
        catch (Exception ex)
        {
            BotLog.Warning($"MyParser Bilibili 完整文档卡片渲染失败: error={ex.Message}");
            return string.Empty;
        }
    }

private static string GetArticleKindText(BilibiliArticleParseResult result) => result.IsOpus ? "Bilibili 图文" : "Bilibili 专栏";

    private static BiliArticleDocumentBlockViewModel CreateStyledTextBlockViewModel(BilibiliArticleBlock block, string text)
    {
        var vm = new BiliArticleDocumentBlockViewModel
        {
            IsImage = false,
            Text = text,
            HeadingLevel = block.HeadingLevel,
            Indent = Math.Clamp(block.IndentLevel, 0, 6) * 24,
            Margin = new Avalonia.Thickness(Math.Clamp(block.IndentLevel, 0, 6) * 24, 0, 0, 0),
        };

        var foreground = NormalizeCardColor(block.TextColor) ?? "#F4EFF4";
        return block.TextStyle switch
        {
            BilibiliArticleTextStyle.Heading => CreateHeadingStyle(vm, block.HeadingLevel, foreground),
            BilibiliArticleTextStyle.Quote => WithStyle(vm, 15, 24, "#D7D0DC", "#25232A", "#66FB7299", "SemiBold", accentBrush: "#FFFB7299", accentWidth: 5),
            BilibiliArticleTextStyle.Code => WithStyle(vm, 13, 21, "#D8F5DD", "#18251C", "#4437D67A", "Normal", accentBrush: "#FF37D67A", accentWidth: 4),
            BilibiliArticleTextStyle.ListItem => WithStyle(vm, 15, 24, foreground, "#141218", "#22CAC4D0", block.IsBold ? "SemiBold" : "Normal", accentBrush: "#6607B6D5", accentWidth: 3),
            _ when block.IsBold || !string.IsNullOrWhiteSpace(block.TextColor) => WithStyle(vm, 15, 24, foreground, "#152831", "#6607B6D5", block.IsBold ? "SemiBold" : "Normal", accentBrush: "#FF07B6D5", accentWidth: 4),
            _ => WithStyle(vm, 15, 24, "#F4EFF4", "Transparent", "Transparent", "Normal"),
        };

        static BiliArticleDocumentBlockViewModel CreateHeadingStyle(BiliArticleDocumentBlockViewModel source, int headingLevel, string foreground)
        {
            return headingLevel switch
            {
                <= 1 => WithStyle(source, 27, 36, foreground == "#F4EFF4" ? "#FFFFFFFF" : foreground, "#33FB7299", "#CCFB7299", "Bold", accentBrush: "#FFFF4F8B", accentWidth: 7),
                2 => WithStyle(source, 23, 32, foreground == "#F4EFF4" ? "#FFFFFFFF" : foreground, "#26FB7299", "#99FB7299", "Bold", accentBrush: "#FFFB7299", accentWidth: 6),
                3 => WithStyle(source, 20, 29, foreground == "#F4EFF4" ? "#FFEAF2" : foreground, "#1E2233", "#7707B6D5", "SemiBold", accentBrush: "#FF07B6D5", accentWidth: 5),
                _ => WithStyle(source, 18, 27, foreground == "#F4EFF4" ? "#FFEAF2" : foreground, "#152831", "#6607B6D5", "SemiBold", accentBrush: "#CC07B6D5", accentWidth: 4),
            };
        }

        static BiliArticleDocumentBlockViewModel WithStyle(BiliArticleDocumentBlockViewModel source, int fontSize, int lineHeight, string foreground, string background, string borderBrush, string fontWeight, string accentBrush = "Transparent", int accentWidth = 0)
        {
            return new BiliArticleDocumentBlockViewModel
            {
                IsImage = source.IsImage,
                Text = source.Text,
                HeadingLevel = source.HeadingLevel,
                Indent = source.Indent,
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

    private static string? NormalizeCardColor(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var color = value.Trim();
        if (color.StartsWith('#') && (color.Length == 7 || color.Length == 9))
        {
            return color.Length == 7 ? "#FF" + color[1..] : color;
        }

        return null;
    }

    private static IEnumerable<BilibiliArticleBlock> BuildForwardBlocks(BilibiliArticleParseResult result)
    {
        if (result.Blocks.Count > 0)
        {
            return result.Blocks;
        }

        var blocks = new List<BilibiliArticleBlock>();
        if (!string.IsNullOrWhiteSpace(result.PlainText))
        {
            blocks.AddRange(SplitText(result.PlainText!, 900).Select(i => new BilibiliArticleBlock { Type = BilibiliArticleBlockType.Text, Text = i }));
        }

        blocks.AddRange(result.ImageUrls.Select(url => new BilibiliArticleBlock { Type = BilibiliArticleBlockType.Image, Url = url }));
        return blocks;
    }

    private static IEnumerable<BilibiliArticleBlock> BuildDocumentBlocksForRender(BilibiliArticleParseResult result)
    {
        if (result.Blocks.Count > 0)
        {
            return result.Blocks;
        }

        var blocks = new List<BilibiliArticleBlock>();
        if (!string.IsNullOrWhiteSpace(result.PlainText))
        {
            blocks.AddRange(SplitText(result.PlainText!, 900).Select(i => new BilibiliArticleBlock { Type = BilibiliArticleBlockType.Text, Text = i }));
        }

        blocks.AddRange(result.ImageUrls.Select(url => new BilibiliArticleBlock { Type = BilibiliArticleBlockType.Image, Url = url }));
        return blocks;
    }

    private static int EstimateTextBlockHeight(string text, int fontSize, int lineHeight, int width)
    {
        var charsPerLine = Math.Max(12, width / Math.Max(1, fontSize));
        var lines = text.Split('\n').Sum(line => Math.Max(1, (int)Math.Ceiling(line.Length / (double)charsPerLine)));
        return Math.Clamp(lines * lineHeight + 6, lineHeight + 8, 1200);
    }

    private static string BuildArticleHeaderText(BilibiliArticleParseResult result)
    {
        var sb = new StringBuilder();
        sb.AppendLine(GetArticleKindText(result));
        if (!string.IsNullOrWhiteSpace(result.Title)) sb.AppendLine(result.Title);
        if (!string.IsNullOrWhiteSpace(result.AuthorName)) sb.AppendLine("作者：" + result.AuthorName);
        if (result.PublishTime is not null) sb.AppendLine($"发布时间：{result.PublishTime:yyyy-MM-dd HH:mm}");
        sb.AppendLine($"数据：{FormatCount(result.ViewCount)}阅读 / {FormatCount(result.LikeCount)}赞 / {FormatCount(result.ReplyCount)}评论");
        return sb.ToString().TrimEnd();
    }

    private static string BuildArticleBodyText(BilibiliArticleParseResult result)
    {
        return BuildArticleSummary(result, 5000);
    }

    private static string BuildArticleSummary(BilibiliArticleParseResult result, int maxLength = 520)
    {
        var text = !string.IsNullOrWhiteSpace(result.Summary) ? result.Summary! : result.PlainText ?? string.Empty;
        return TrimLine(text, maxLength);
    }

    private static string BuildArticleAuthorMeta(BilibiliArticleParseResult result)
    {
        var parts = new List<string>();
        if (result.AuthorFans > 0) parts.Add($"{FormatCount(result.AuthorFans)}粉丝");
        if (result.Words > 0) parts.Add($"{result.Words}字");
        if (result.ImageUrls.Count > 0) parts.Add($"{result.ImageUrls.Count}图");
        return parts.Count > 0 ? string.Join(" · ", parts) : (result.IsOpus ? "Bilibili 图文" : "Bilibili 专栏");
    }
}
