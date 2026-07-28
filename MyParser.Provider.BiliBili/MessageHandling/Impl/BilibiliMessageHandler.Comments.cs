using Avalonia.Media.Imaging;
using MyParser.Provider.BiliBili.Models;
using MyParser.Provider.BiliBili.Views;
using ShiroBot.AvaloniaSdk;
using ShiroBot.SDK.Abstractions;
using ShiroBot.SDK.Plugin;

namespace MyParser.Provider.BiliBili.MessageHandling;

internal sealed partial class BilibiliMessageHandler
{
    private async Task SendCommentsMessagesAsync(IncomingMessage message, BilibiliParseResult result)
    {
        if (result.Comments.Count == 0)
        {
            return;
        }

        try
        {
            var cardUri = await BuildCommentDocumentCardUriAsync(result);
            if (!string.IsNullOrWhiteSpace(cardUri))
            {
                await context.Message.ReplyAsync(message, new ImageOutgoingSegment(cardUri));
                BotLog.Info($"MyParser Bilibili 评论区大卡片发送完成: bvid={result.Bvid}, comments={result.Comments.Count}");
            }
        }
        catch (Exception ex)
        {
            BotLog.Warning($"MyParser Bilibili 评论区大卡片发送失败，继续发送原始评论: bvid={result.Bvid}, error={ex.Message}");
        }

        try
        {
            var fallbackSenderId = long.TryParse(message.Sender.Id, out var parsedSenderId) ? parsedSenderId : 10000;
            var fallbackSenderName = string.IsNullOrWhiteSpace(result.AuthorName) ? "Bilibili 评论区" : result.AuthorName;
            var rawNodes = result.Comments.Select(comment => new OutgoingForwardedMessage(
                ParseCommentSenderId(comment.UserId, fallbackSenderId),
                string.IsNullOrWhiteSpace(comment.UserName) ? fallbackSenderName : comment.UserName,
                [new TextOutgoingSegment(comment.Message)])).ToList();
            await context.Message.ReplyAsync(message, BuildRawCommentForwardSegment(result, rawNodes));
            BotLog.Info($"MyParser Bilibili 原始评论合并转发发送完成: bvid={result.Bvid}, comments={rawNodes.Count}");
        }
        catch (Exception ex)
        {
            BotLog.Warning($"MyParser Bilibili 原始评论合并转发发送失败，作品继续发送: bvid={result.Bvid}, error={ex.Message}");
        }
    }

    private async Task<string> BuildCommentDocumentCardUriAsync(BilibiliParseResult result)
    {
        if (context.Render is null)
        {
            return string.Empty;
        }

        var ownedBitmaps = new List<Bitmap>();
        try
        {
            var coverTask = BuildRemoteImageAsync(result.CoverUrl, result.SourceUrl, $"bilibili_comment_cover_{result.Bvid}");
            var authorAvatarTask = BuildRemoteImageAsync(result.AuthorAvatarUrl, result.SourceUrl, $"bilibili_comment_up_{result.Bvid}");
            var renderedComments = await _hostServices.SelectParallelOrderedAsync(
                result.Comments,
                6,
                comment => BuildCommentItemViewModelAsync(result, comment));
            var coverImage = await coverTask;
            var authorAvatarImage = await authorAvatarTask;

            var cover = DecodeProviderImage(coverImage);
            var authorAvatar = DecodeProviderImage(authorAvatarImage);
            AddOwnedBitmap(ownedBitmaps, cover);
            AddOwnedBitmap(ownedBitmaps, authorAvatar);

            var comments = new List<BiliCommentItemViewModel>(renderedComments.Count);
            var estimatedHeight = 360;
            foreach (var rendered in renderedComments)
            {
                comments.Add(rendered.ViewModel);
                AddOwnedBitmap(ownedBitmaps, rendered.ViewModel.Avatar);
                AddOwnedBitmap(ownedBitmaps, rendered.ViewModel.Image);
                estimatedHeight += EstimateCommentHeight(rendered.Comment);
            }

            var canvasHeight = Math.Clamp(estimatedHeight + 90, 900, 12000);
            var viewModel = new BiliCommentCardViewModel
            {
                CanvasHeight = canvasHeight,
                Cover = cover,
                AuthorAvatar = authorAvatar,
                Title = string.IsNullOrWhiteSpace(result.Title) ? "Bilibili 视频评论区" : result.Title,
                AuthorName = string.IsNullOrWhiteSpace(result.AuthorName) ? "未知 UP" : result.AuthorName,
                MetaText = $"BV {result.Bvid} · AV {result.Aid}",
                StatsText = $"{FormatCount(result.ViewCount)}播放 · {FormatCount(result.LikeCount)}赞 · {FormatCount(result.ReplyCount)}评论 · 展示热门评论 {comments.Count} 条",
                Comments = comments,
            };
            var png = await context.RenderControlPngAsync<BiliCommentCard>(
                viewModel,
                new ControlRenderOptions(RenderTheme.Dark));
            BotLog.Info($"MyParser Bilibili 评论区大卡片渲染完成: bvid={result.Bvid}, comments={comments.Count}, height={canvasHeight}, png_kb={png.Length / 1024d:F1}");
            return "base64://" + Convert.ToBase64String(png);
        }
        finally
        {
            foreach (var bitmap in ownedBitmaps.Distinct())
            {
                bitmap.Dispose();
            }
        }
    }

    private async Task<(BilibiliCommentInfo Comment, BiliCommentItemViewModel ViewModel)> BuildCommentItemViewModelAsync(
        BilibiliParseResult result,
        BilibiliCommentInfo comment)
    {
        var index = result.Comments.IndexOf(comment) + 1;
        var avatarTask = BuildRemoteImageAsync(
            comment.UserAvatarUrl,
            result.SourceUrl,
            $"bilibili_comment_avatar_{result.Bvid}_{index:D2}");
        var imageTask = string.IsNullOrWhiteSpace(comment.ImageUrl)
            ? Task.FromResult(new ProviderImageBuildResult(string.Empty, null))
            : BuildRemoteImageAsync(
                comment.ImageUrl,
                result.SourceUrl,
                $"bilibili_comment_image_{result.Bvid}_{index:D2}");
        var avatarImage = await avatarTask;
        var commentImage = await imageTask;
        return (comment, new BiliCommentItemViewModel
        {
            Avatar = DecodeProviderImage(avatarImage),
            Image = DecodeProviderImage(commentImage),
            UserName = comment.UserName,
            UserIdText = "UID " + (comment.UserId ?? "--"),
            Message = comment.Message,
            MetaText = BuildCommentMetaText(comment),
            StatsText = $"{FormatCount(comment.LikeCount)}赞 · {FormatCount(comment.ReplyCount)}回复",
            IndexText = index.ToString("D2"),
            IsAuthor = comment.IsAuthor,
        });
    }

    private Bitmap? DecodeProviderImage(ProviderImageBuildResult image)
    {
        if (!string.IsNullOrWhiteSpace(image.LocalPath))
        {
            return _hostServices.DecodeImageFileForRender(image.LocalPath);
        }

        return string.IsNullOrWhiteSpace(image.Uri) ? null : _hostServices.DecodeBase64ImageForRender(image.Uri);
    }

    private static void AddOwnedBitmap(ICollection<Bitmap> bitmaps, Bitmap? bitmap)
    {
        if (bitmap is not null)
        {
            bitmaps.Add(bitmap);
        }
    }

    private static int EstimateCommentHeight(BilibiliCommentInfo comment)
    {
        var textLines = Math.Clamp((comment.Message.Length + 29) / 30, 1, 12);
        return 125 + textLines * 23 + (string.IsNullOrWhiteSpace(comment.ImageUrl) ? 0 : 278);
    }

    private static string BuildCommentMetaText(BilibiliCommentInfo comment)
    {
        var location = string.IsNullOrWhiteSpace(comment.IpLocation) ? "IP 未知" : comment.IpLocation;
        var time = !string.IsNullOrWhiteSpace(comment.TimeDescription)
            ? comment.TimeDescription
            : FormatCommentTimestamp(comment.CreateTimeUnixSeconds);
        return $"{location} · {time}";
    }

    private static string FormatCommentTimestamp(long unixSeconds)
    {
        if (unixSeconds <= 0)
        {
            return "时间未知";
        }

        try
        {
            return DateTimeOffset.FromUnixTimeSeconds(unixSeconds).ToLocalTime().ToString("yyyy-MM-dd HH:mm");
        }
        catch (ArgumentOutOfRangeException)
        {
            return "时间未知";
        }
    }

    private static ForwardOutgoingSegment BuildRawCommentForwardSegment(
        BilibiliParseResult result,
        IReadOnlyList<OutgoingForwardedMessage> nodes)
    {
        return new ForwardOutgoingSegment(
            nodes,
            "Bilibili 原始评论区",
            result.Comments.Take(4).Select(comment => comment.Message).ToArray(),
            $"共 {nodes.Count} 条原始评论",
            "[Bilibili 原始评论区]");
    }

    private static long ParseCommentSenderId(string? userId, long fallbackSenderId)
    {
        return long.TryParse(userId, out var senderId) && senderId > 0 ? senderId : fallbackSenderId;
    }
}
