using System.Diagnostics;
using ShiroBot.AvaloniaSdk;
using Shirobot.Plugin.MyParser.Providers.Common.MessageHandling;
using Shirobot.Plugin.MyParser.Providers.Bilibili.Infrastructure;
using Shirobot.Plugin.MyParser.Providers.Bilibili.Models;
using Shirobot.Plugin.MyParser.Providers.Bilibili.ViewModels;
using Shirobot.Plugin.MyParser.Providers.Bilibili.Views;
using Shirobot.Plugin.MyParser.Utility;
using ShiroBot.SDK.Abstractions;
using ShiroBot.SDK.Models;
using ShiroBot.SDK.Plugin;

namespace Shirobot.Plugin.MyParser.Providers.Bilibili.Impl.MessageHandling.Impl;

internal sealed partial class BilibiliMessageHandler
{
private async Task SendCoverMessageAsync(MessageEvent message, BilibiliParseResult result)
    {
        var coverUri = await BuildCoverCardUriAsync(result);
        var segment = new ImageSegment(coverUri);
        var stopwatch = Stopwatch.StartNew();
        var scene = GetMessageScene(message);
        BotLog.Info($"MyParser Bilibili 封面卡片 ImageSegment 发送开始: bvid={result.Bvid}, scene={scene}, uri_preview={MediaUriUtilities.PreviewUri(coverUri)}");

        var response = await context.Message.ReplyAsync(message, segment);
        BotLog.Info($"MyParser Bilibili 封面卡片 ImageSegment 发送接口完成: bvid={result.Bvid}, scene={scene}, channel_id={message.Channel.Id}, message_id={response.MessageId}, elapsed={stopwatch.Elapsed:mm\\:ss}");
    }

    private async Task<string> BuildCoverCardUriAsync(BilibiliParseResult result)
    {
        var coverTask = BuildRemoteImageAsync(result.CoverUrl, result.SourceUrl, $"bilibili_cover_{result.Bvid}");
        var avatarTask = context.Render is null
            ? Task.FromResult<(string Uri, string? LocalPath)>((string.Empty, null))
            : BuildRemoteImageAsync(result.AuthorAvatarUrl, result.SourceUrl, $"bilibili_avatar_{result.Bvid}");
        var coverImage = await coverTask;
        var coverUri = coverImage.Uri;
        if (context.Render is null)
        {
            BotLog.Warning($"MyParser Bilibili Avalonia 渲染服务不可用，直接发送原始封面: bvid={result.Bvid}");
            return coverUri;
        }

        try
        {
            var avatarImage = await avatarTask;
            var coverBitmap = !string.IsNullOrWhiteSpace(coverImage.LocalPath)
                ? RenderBitmapUtilities.DecodeImageFileForRender(coverImage.LocalPath)
                : RenderBitmapUtilities.DecodeBase64ImageForRender(coverUri);
            var avatarBitmap = !string.IsNullOrWhiteSpace(avatarImage.LocalPath)
                ? RenderBitmapUtilities.DecodeImageFileForRender(avatarImage.LocalPath)
                : RenderBitmapUtilities.DecodeBase64ImageForRender(avatarImage.Uri);

            var selected = result.SelectedVideo;
            var vm = new BiliCardViewModel
            {
                Cover = coverBitmap,
                Avatar = avatarBitmap,
                Title = string.IsNullOrWhiteSpace(result.Title) ? "Bilibili 视频" : result.Title,
                Description = BuildCardDescription(result),
                AuthorName = string.IsNullOrWhiteSpace(result.AuthorName) ? "未知 UP" : result.AuthorName,
                AuthorMeta = BuildAuthorMeta(result),
                DurationText = FormatDurationText(result.DurationSeconds),
                TagsText = selected is null ? "# 视频" : $"# {selected.QualityName}",
                LikeCount = FormatCount(result.LikeCount),
                CoinCount = FormatCount(result.CoinCount),
                CollectCount = FormatCount(result.FavoriteCount),
                ShareCount = FormatCount(result.ShareCount),
            };
            var png = await context.RenderControlPngAsync<BiliCard>(vm, new ControlRenderOptions(RenderTheme.Auto));
            BotLog.Info($"MyParser Bilibili 封面卡片渲染完成: bvid={result.Bvid}, cover_url={result.CoverUrl}, png_kb={png.Length / 1024d:F1}, view={typeof(BiliCard).FullName}, mode=base64");
            return "base64://" + Convert.ToBase64String(png);
        }
        catch (Exception ex)
        {
            BotLog.Warning($"MyParser Bilibili 封面卡片渲染失败，直接发送原始封面: bvid={result.Bvid}, cover_url={result.CoverUrl}, error={ex.Message}");
            return coverUri;
        }
    }

    private Task<(string Uri, string? LocalPath)> BuildRemoteImageAsync(string? imageUrl, string? referer, string filePrefix)
    {
        return RemoteImageFetchService.BuildRemoteImageAsync(
            CoverHttp,
            "Bilibili",
            imageUrl,
            referer,
            filePrefix,
            ResolveCoverDownloadDirectory(),
            request =>
            {
                request.Headers.TryAddWithoutValidation("User-Agent", BilibiliConstants.UserAgent);
                request.Headers.TryAddWithoutValidation("Referer", referer ?? BilibiliConstants.Origin + "/");
                request.Headers.TryAddWithoutValidation("Accept", "image/avif,image/webp,image/apng,image/svg+xml,image/*,*/*;q=0.8");
                if (!string.IsNullOrWhiteSpace(MyParserRuntime.BilibiliCookie))
                {
                    request.Headers.TryAddWithoutValidation("Cookie", MyParserRuntime.BilibiliCookie);
                }
            });
    }

}
