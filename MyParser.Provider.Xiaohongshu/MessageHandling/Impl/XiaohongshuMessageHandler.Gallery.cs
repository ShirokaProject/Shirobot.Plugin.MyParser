using System.Diagnostics;
using System.Net;
using System.Text;
using Net.Codecrete.QrCodeGenerator;
using ShiroBot.AvaloniaSdk;
using Shirobot.Plugin.MyParser.Parsing;
using MyParser.Provider.Xiaohongshu.Infrastructure;
using MyParser.Provider.Xiaohongshu.Models;
using MyParser.Provider.Xiaohongshu.Views;
using ShiroBot.SDK.Models;
using ShiroBot.SDK.Abstractions;
using ShiroBot.SDK.Core;
using ShiroBot.SDK.Plugin;

namespace MyParser.Provider.Xiaohongshu.MessageHandling;

internal sealed partial class XiaohongshuMessageHandler
{
private async Task SendCoverOrCardAsync(IncomingMessage message, XiaohongshuParseResult result)
    {
        if (string.IsNullOrWhiteSpace(result.CoverUrl))
        {
            return;
        }

        var image = await BuildRemoteImageAsync(result.CoverUrl, result.SourceUrl, $"xhs_cover_{result.NoteId}");
        if (!string.IsNullOrWhiteSpace(image.Uri))
        {
            await SendImageAsync(message, new ImageOutgoingSegment(image.Uri));
        }
    }

    private async Task SendGalleryForwardAsync(IncomingMessage message, XiaohongshuParseResult result)
    {
        var forwarded = new List<OutgoingForwardedMessage>();
        var senderId = GetBotOrSenderId(message);
        var senderName = string.IsNullOrWhiteSpace(result.AuthorName) ? "小红书图文" : result.AuthorName!;
        forwarded.Add(new OutgoingForwardedMessage(senderId, senderName, [new TextOutgoingSegment(BuildHeaderText(result))]));
        var imageInputs = result.Images.Select((image, index) => (image, Index: index + 1)).ToArray();
        var imageFiles = await _hostServices.SelectParallelOrderedAsync(
            imageInputs,
            6,
            item => BuildRemoteImageAsync(item.image.Url, result.SourceUrl, $"xhs_image_{result.NoteId}_{item.Index:D2}"));
        foreach (var local in imageFiles)
        {
            if (!string.IsNullOrWhiteSpace(local.Uri))
            {
                forwarded.Add(new OutgoingForwardedMessage(senderId, senderName, [new ImageOutgoingSegment(local.Uri)]));
            }
        }

        if (result.Comments.Count > 0)
        {
            forwarded.Add(new OutgoingForwardedMessage(senderId, senderName, [new TextOutgoingSegment(BuildCommentsText(result))]));
        }

        if (!string.IsNullOrWhiteSpace(result.SourceUrl))
        {
            forwarded.Add(new OutgoingForwardedMessage(senderId, senderName, [new TextOutgoingSegment("原文：" + result.SourceUrl)]));
        }

        var title = string.IsNullOrWhiteSpace(result.Title) ? "小红书图文" : TrimLine(result.Title!, 48);
        var preview = new[] { "小红书图文", senderName, result.Comments.Count > 0 ? $"前 {result.Comments.Count} 条评论" : $"图片 {result.Images.Count} 张" };
        var summary = $"{result.Images.Count} 张图 · {result.Comments.Count} 条评论";
        var forward = new ForwardOutgoingSegment(forwarded, title, preview, summary, "小红书图文");
        await _context.Message.ReplyAsync(message, forward);
    }

    private async Task SendGalleryCardAsync(IncomingMessage message, XiaohongshuParseResult result)
    {
        var cardUri = await BuildGalleryCardUriAsync(result);
        if (!string.IsNullOrWhiteSpace(cardUri))
        {
            await SendImageAsync(message, new ImageOutgoingSegment(cardUri));
        }
    }

    private async Task<string> BuildGalleryCardUriAsync(XiaohongshuParseResult result)
    {
        if (_context.Render is null || result.Images.Count == 0)
        {
            return string.Empty;
        }

        try
        {
            var coverTask = BuildRemoteImageAsync(result.Images[0].Url, result.SourceUrl, $"xhs_card_cover_{result.NoteId}");
            var secondTask = BuildRemoteImageAsync(result.Images.ElementAtOrDefault(1)?.Url ?? result.Images[0].Url, result.SourceUrl, $"xhs_card_second_{result.NoteId}");
            var avatarTask = BuildRemoteImageAsync(result.AuthorAvatarUrl, result.SourceUrl, $"xhs_card_avatar_{result.NoteId}");
            await Task.WhenAll(coverTask, secondTask, avatarTask);
            var cover = await coverTask;
            var second = await secondTask;
            var avatar = await avatarTask;
            var vm = new XiaohongshuCardViewModel
            {
                Cover = !string.IsNullOrWhiteSpace(cover.LocalPath) ? _hostServices.DecodeImageFileForRender(cover.LocalPath) : _hostServices.DecodeBase64ImageForRender(cover.Uri),
                SecondImage = !string.IsNullOrWhiteSpace(second.LocalPath) ? _hostServices.DecodeImageFileForRender(second.LocalPath) : _hostServices.DecodeBase64ImageForRender(second.Uri),
                Avatar = !string.IsNullOrWhiteSpace(avatar.LocalPath) ? _hostServices.DecodeImageFileForRender(avatar.LocalPath) : _hostServices.DecodeBase64ImageForRender(avatar.Uri),
                Title = string.IsNullOrWhiteSpace(result.Title) ? "小红书图文" : result.Title!,
                Description = string.IsNullOrWhiteSpace(result.Description) ? "" : result.Description!,
                AuthorName = string.IsNullOrWhiteSpace(result.AuthorName) ? "未知作者" : result.AuthorName!,
                MetaText = $"{result.Images.Count} 图 · {result.Comments.Count} 条评论",
                StatsText = $"{FormatCount(result.LikeCount)}赞 · {FormatCount(result.CollectCount)}收藏 · {FormatCount(result.CommentCount)}评论",
                TagsText = result.Tags.Count > 0 ? string.Join(" ", result.Tags.Take(6).Select(i => "#" + i)) : "# 小红书",
                Comments = result.Comments.Take(10).Select((comment, index) => new XiaohongshuCommentViewModel
                {
                    Index = (index + 1).ToString(),
                    Nickname = comment.User.Nickname,
                    Content = string.IsNullOrWhiteSpace(comment.Content) ? "[空评论]" : comment.Content,
                    Meta = $"{FormatCount(comment.LikeCount)}赞" + (string.IsNullOrWhiteSpace(comment.IpLocation) ? "" : " · " + comment.IpLocation),
                }).ToList(),
            };
            if (vm.Comments.Count == 0)
            {
                vm.Comments.Add(new XiaohongshuCommentViewModel { Index = "1", Nickname = "提示", Content = "没有获取到评论；评论接口需要登录 Cookie、xsec_token 和 xhshow sign 服务。", Meta = "MyParser" });
            }

            var png = await _context.RenderControlPngAsync<XiaohongshuCard>(vm, new ControlRenderOptions(RenderTheme.Dark));
            BotLog.Info($"MyParser 小红书图文卡片渲染完成: note_id={result.NoteId}, comments={result.Comments.Count}, png_kb={png.Length / 1024d:F1}, mode=base64");
            return "base64://" + Convert.ToBase64String(png);
        }
        catch (Exception ex)
        {
            BotLog.Warning($"MyParser 小红书图文卡片渲染失败: note_id={result.NoteId}, error={ex.Message}");
            return string.Empty;
        }
    }

    private Task<ProviderImageBuildResult> BuildRemoteImageAsync(string? imageUrl, string? referer, string filePrefix)
    {
        return _hostServices.BuildProviderImageAsync(new ProviderImageBuildRequest(
            "小红书",
            imageUrl,
            referer,
            filePrefix,
            request =>
            {
                request.Headers.TryAddWithoutValidation("User-Agent", XiaohongshuConstants.UserAgent);
                request.Headers.TryAddWithoutValidation("Referer", referer ?? XiaohongshuConstants.Origin + "/");
                request.Headers.TryAddWithoutValidation("Accept", "image/avif,image/webp,image/apng,image/svg+xml,image/*,*/*;q=0.8");
                if (!string.IsNullOrWhiteSpace(MyParserRuntime.XiaohongshuCookie))
                {
                    request.Headers.TryAddWithoutValidation("Cookie", MyParserRuntime.XiaohongshuCookie);
                }
            }));
    }
}
