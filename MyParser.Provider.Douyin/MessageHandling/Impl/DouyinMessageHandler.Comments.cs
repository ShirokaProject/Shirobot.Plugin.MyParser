using Avalonia.Media.Imaging;
using MyParser.Provider.Douyin.Models;
using MyParser.Provider.Douyin.Views;
using ShiroBot.AvaloniaSdk;
using ShiroBot.SDK.Abstractions;
using ShiroBot.SDK.Models;
using ShiroBot.SDK.Plugin;

namespace MyParser.Provider.Douyin.MessageHandling;

internal sealed partial class DouyinMessageHandler
{
    private Task StartSendCommentsMessageAsync(
        IncomingMessage message,
        DouyinParseResult result,
        CancellationToken cancellationToken)
    {
        if (result.Comments.Count == 0)
        {
            BotLog.Info($"MyParser 抖音评论异步任务跳过: aweme_id={result.AwemeId}, reason=no_comments");
            return Task.CompletedTask;
        }

        BotLog.Info($"MyParser 抖音评论异步任务已启动: aweme_id={result.AwemeId}, comments={result.Comments.Count}");
        return _hostServices.RunLoggedBackgroundAsync(
            $"抖音评论区异步渲染发送: aweme_id={result.AwemeId}",
            async () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                await SendCommentsMessageAsync(message, result);
            });
    }

    private async Task SendCommentsMessageAsync(IncomingMessage message, DouyinParseResult result)
    {
        BotLog.Info($"MyParser 抖音评论发送检查: aweme_id={result.AwemeId}, comments={result.Comments.Count}, is_gallery={result.IsGallery}, image_count={result.Images.Count}");
        if (result.Comments.Count == 0)
        {
            BotLog.Info($"MyParser 抖音评论发送跳过: aweme_id={result.AwemeId}, reason=no_comments");
            return;
        }

        try
        {
            var senderId = long.TryParse(message.Sender.Id, out var parsedSenderId) ? parsedSenderId : 10000;
            var senderName = string.IsNullOrWhiteSpace(message.Sender.Name) ? "抖音评论" : message.Sender.Name;
            var nodes = await BuildCommentForwardMessagesAsync(result, senderId, senderName);
            if (nodes.Count == 0) return;

            BotLog.Info($"MyParser 抖音评论合并转发发送开始: aweme_id={result.AwemeId}, nodes={nodes.Count}, scene={GetMessageScene(message)}");
            await _context.Message.ReplyAsync(message, BuildCommentForwardSegment(result, nodes));
            BotLog.Info($"MyParser 抖音评论合并转发发送完成: aweme_id={result.AwemeId}, nodes={nodes.Count}");
        }
        catch (Exception ex)
        {
            BotLog.Warning($"MyParser 抖音评论发送失败，作品已正常发送: aweme_id={result.AwemeId}, error={ex.Message}");
        }
    }

    private async Task<List<OutgoingForwardedMessage>> BuildCommentForwardMessagesAsync(
        DouyinParseResult result,
        long fallbackSenderId,
        string fallbackSenderName)
    {
        var assets = new List<CommentRenderAsset>(result.Comments.Count);
        try
        {
            for (var index = 0; index < result.Comments.Count; index++)
            {
                assets.Add(await BuildCommentAssetAsync(result, result.Comments[index], index + 1));
            }

            var nodes = new List<OutgoingForwardedMessage>(result.Comments.Count + 1);
            var documentSegment = await BuildCommentsDocumentSegmentAsync(result, assets);
            nodes.Add(new OutgoingForwardedMessage(
                fallbackSenderId,
                "抖音热门评论",
                [documentSegment]));

            foreach (var asset in assets)
            {
                var comment = asset.Comment;
                var senderId = long.TryParse(comment.UserId, out var parsedUserId) && parsedUserId > 0
                    ? parsedUserId
                    : fallbackSenderId;
                var senderName = string.IsNullOrWhiteSpace(comment.UserName) ? fallbackSenderName : comment.UserName;
                var segments = new List<OutgoingSegment>
                {
                    new TextOutgoingSegment(BuildOriginalCommentText(comment)),
                };
                segments.AddRange(asset.AttachmentSegments);
                nodes.Add(new OutgoingForwardedMessage(senderId, senderName, segments));
                BotLog.Info($"MyParser 抖音原始评论转发节点完成: aweme_id={result.AwemeId}, comment_id={comment.CommentId}, images={asset.AttachmentSegments.Count}, segments={segments.Count}");
            }

            return nodes;
        }
        finally
        {
            foreach (var asset in assets)
            {
                asset.Avatar?.Dispose();
                asset.PreviewImage?.Dispose();
            }
        }
    }

    private async Task<CommentRenderAsset> BuildCommentAssetAsync(
        DouyinParseResult result,
        DouyinCommentInfo comment,
        int index)
    {
        Bitmap? avatarBitmap = null;
        Bitmap? previewBitmap = null;
        if (!string.IsNullOrWhiteSpace(comment.UserAvatarUrl))
        {
            var avatar = await BuildRemoteImageAsync(
                comment.UserAvatarUrl,
                result.SourceUrl,
                $"douyin_comment_avatar_{result.AwemeId}_{index:D2}");
            avatarBitmap = !string.IsNullOrWhiteSpace(avatar.LocalPath)
                ? _hostServices.DecodeImageFileForRender(avatar.LocalPath)
                : _hostServices.DecodeBase64ImageForRender(avatar.Uri);
        }

        var attachments = new List<OutgoingSegment>();
        for (var imageIndex = 0; imageIndex < comment.ImageUrls.Count; imageIndex++)
        {
            var image = await BuildRemoteImageAsync(
                comment.ImageUrls[imageIndex],
                result.SourceUrl,
                $"douyin_comment_image_{result.AwemeId}_{index:D2}_{imageIndex + 1:D2}",
                persistLocalFile: true);
            var attachmentUri = BuildLocalFileUri(image.LocalPath) ?? image.Uri;
            attachments.Add(new ImageOutgoingSegment(attachmentUri));
            if (imageIndex == 0)
            {
                previewBitmap = !string.IsNullOrWhiteSpace(image.LocalPath)
                    ? _hostServices.DecodeImageFileForRender(image.LocalPath)
                    : _hostServices.DecodeBase64ImageForRender(image.Uri);
            }

            BotLog.Info($"MyParser 抖音评论附图本地化完成: aweme_id={result.AwemeId}, comment_id={comment.CommentId}, image={imageIndex + 1}/{comment.ImageUrls.Count}, physical_path={DescribePhysicalPath(image.LocalPath)}, uri={attachmentUri}");
        }

        return new CommentRenderAsset(comment, avatarBitmap, previewBitmap, attachments);
    }

    private async Task<OutgoingSegment> BuildCommentsDocumentSegmentAsync(
        DouyinParseResult result,
        IReadOnlyList<CommentRenderAsset> assets)
    {
        if (_context.Render is not IAvaloniaRenderContext renderer)
        {
            return new TextOutgoingSegment($"抖音热门评论，共 {result.Comments.Count} 条");
        }

        Bitmap? coverBitmap = null;
        if (!string.IsNullOrWhiteSpace(result.CoverUrl))
        {
            var cover = await BuildRemoteImageAsync(
                result.CoverUrl,
                result.SourceUrl,
                $"douyin_comment_cover_{result.AwemeId}");
            coverBitmap = !string.IsNullOrWhiteSpace(cover.LocalPath)
                ? _hostServices.DecodeImageFileForRender(cover.LocalPath)
                : _hostServices.DecodeBase64ImageForRender(cover.Uri);
        }

        try
        {
            var comments = assets.Select((asset, index) => new DouyinCommentItemViewModel
            {
                Avatar = asset.Avatar,
                CommentImage = asset.PreviewImage,
                HasImage = asset.PreviewImage is not null,
                UserName = asset.Comment.UserName,
                UserIdText = "抖音号 " + (asset.Comment.DisplayUserId ?? asset.Comment.UserId ?? "--"),
                IpText = string.IsNullOrWhiteSpace(asset.Comment.IpLabel) ? "IP 未知" : asset.Comment.IpLabel,
                Message = asset.Comment.Text,
                LikeText = FormatCount(asset.Comment.LikeCount),
                ReplyText = FormatCount(asset.Comment.ReplyCount),
                TimeText = FormatCommentTime(asset.Comment.CreateTimeUnixSeconds),
                IndexText = $"#{index + 1:D2}",
                IsAuthor = asset.Comment.IsAuthor,
            }).ToArray();
            var canvasHeight = CalculateCommentDocumentHeight(assets);
            var vm = new DouyinCommentCardViewModel
            {
                CanvasHeight = canvasHeight,
                Cover = coverBitmap,
                Title = string.IsNullOrWhiteSpace(result.Title) ? "热门评论" : TrimLine(result.Title, 42),
                MetaText = $"作品 {result.AwemeId} · 评论区精选",
                StatsText = $"{comments.Length} 条评论 · {assets.Sum(asset => asset.AttachmentSegments.Count)} 张附图",
                Comments = comments,
            };

            BotLog.Info($"MyParser 抖音评论区大卡片渲染开始: aweme_id={result.AwemeId}, comments={comments.Length}, canvas_height={canvasHeight}, cover={coverBitmap is not null}");
            var uri = await renderer.RenderControlPngToFileUriAsync<DouyinCommentCard>(
                vm,
                new ControlRenderOptions(RenderTheme.Dark));
            BotLog.Info($"MyParser 抖音评论区大卡片渲染完成: aweme_id={result.AwemeId}, uri={uri}");
            return new ImageOutgoingSegment(uri);
        }
        finally
        {
            coverBitmap?.Dispose();
        }
    }

    private static int CalculateCommentDocumentHeight(IReadOnlyList<CommentRenderAsset> assets)
    {
        var height = 210;
        foreach (var asset in assets)
        {
            var estimatedLines = Math.Clamp((int)Math.Ceiling(asset.Comment.Text.Length / 30d), 1, 8);
            height += 142 + estimatedLines * 25 + (asset.PreviewImage is null ? 0 : 245);
        }

        return Math.Clamp(height, 520, 6000);
    }

    private static ForwardOutgoingSegment BuildCommentForwardSegment(
        DouyinParseResult result,
        IReadOnlyList<OutgoingForwardedMessage> nodes)
    {
        var preview = result.Comments.Take(4)
            .Select(comment => $"{comment.UserName}：{TrimLine(comment.Text, 28)}")
            .ToArray();
        return new ForwardOutgoingSegment(
            nodes,
            "抖音热门评论",
            preview,
            $"1 张评论区卡片 · {result.Comments.Count} 条原始评论",
            "[抖音热门评论]");
    }

    private static string BuildOriginalCommentText(DouyinCommentInfo comment)
    {
        var authorMark = comment.IsAuthor ? " [作者]" : string.Empty;
        return $"{comment.UserName}{authorMark}\n"
               + $"抖音号：{comment.DisplayUserId ?? comment.UserId ?? "--"}\n"
               + $"IP属地：{comment.IpLabel ?? "未知"}\n"
               + $"发布时间：{FormatCommentTime(comment.CreateTimeUnixSeconds)}\n"
               + $"点赞：{FormatCount(comment.LikeCount)} · 回复：{FormatCount(comment.ReplyCount)}\n\n"
               + comment.Text;
    }

    private static string FormatCommentTime(long unixSeconds)
    {
        if (unixSeconds <= 0) return "时间未知";
        try
        {
            return DateTimeOffset.FromUnixTimeSeconds(unixSeconds).ToLocalTime().ToString("yyyy-MM-dd HH:mm");
        }
        catch (ArgumentOutOfRangeException)
        {
            return "时间未知";
        }
    }

    private sealed record CommentRenderAsset(
        DouyinCommentInfo Comment,
        Bitmap? Avatar,
        Bitmap? PreviewImage,
        IReadOnlyList<OutgoingSegment> AttachmentSegments);
}
