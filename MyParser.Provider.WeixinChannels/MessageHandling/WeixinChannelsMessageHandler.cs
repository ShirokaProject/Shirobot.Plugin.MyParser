using System.Diagnostics;
using System.Text;
using MyParser.Provider.WeixinChannels.Infrastructure;
using MyParser.Provider.WeixinChannels.Models;
using MyParser.Provider.WeixinChannels.Parsing;
using MyParser.Provider.WeixinChannels.Views;
using ShiroBot.AvaloniaSdk;
using ShiroBot.SDK.Models;
using ShiroBot.SDK.Core;
using ShiroBot.SDK.Plugin;

namespace MyParser.Provider.WeixinChannels.MessageHandling;

internal sealed class WeixinChannelsMessageHandler(ProviderMessageHandlerContext context) : ProviderMessageHandlerBase(context)
{
    public override string ProviderId => WeixinChannelsConstants.ProviderId;

    public override async Task ParseAndReplyAsync(IncomingMessage message, string text, bool silentProviderMismatch = false, CancellationToken cancellationToken = default)
    {
        await ReactAsync(message, "351", WeixinChannelsConstants.DisplayName);
        try
        {
            var media = await ProviderRegistry.ParseAsync(text, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (media.ProviderPayload is not WeixinChannelsParseResult result)
            {
                await ReplyAsync(message, "微信视频号链接已识别，但解析结果类型异常。").ConfigureAwait(false);
                await ReactAsync(message, "9", WeixinChannelsConstants.DisplayName).ConfigureAwait(false);
                return;
            }

            _ = HostServices.RunLoggedBackgroundAsync($"微信视频号卡片异步发送: sph_id={result.SphId}", async () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                await SendCardAsync(message, result);
            });

            var videoSent = false;
            var fileUploaded = false;
            string? videoSendError = null;
            string? fileUploadInfo = null;
            try
            {
                if (Config.SendVideoSegment)
                {
                    var videoSegment = await BuildVideoSegmentAsync(result).ConfigureAwait(false);
                    await SendVideoMessageAsync(message, result, videoSegment).ConfigureAwait(false);
                    videoSent = true;
                }

                if (Config.UploadVideoAsFile && !Config.UploadVideoAsFileOnlyOnVideoSendFailure && !string.IsNullOrWhiteSpace(result.LocalVideoPath))
                {
                    fileUploadInfo = await UploadVideoFileAsync(message, result).ConfigureAwait(false);
                    fileUploaded = true;
                    BotLog.Info($"MyParser 微信视频号文件上传完成: sph_id={result.SphId}, {fileUploadInfo}");
                }

                CleanupLocalVideoAfterSend(result);
                await ReactAsync(message, "426", WeixinChannelsConstants.DisplayName).ConfigureAwait(false);
                return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                videoSendError = ex.Message;
                BotLog.Warning($"MyParser 微信视频号 VideoSegment 发送未完成: sph_id={result.SphId}, detail={ex.Message}");
                if (Config.UploadVideoAsFile && !string.IsNullOrWhiteSpace(result.LocalVideoPath))
                {
                    try
                    {
                        fileUploadInfo = await UploadVideoFileAsync(message, result).ConfigureAwait(false);
                        fileUploaded = true;
                        CleanupLocalVideoAfterSend(result);
                        await ReactAsync(message, "426", WeixinChannelsConstants.DisplayName).ConfigureAwait(false);
                        return;
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception uploadEx)
                    {
                        fileUploadInfo = uploadEx.Message;
                        BotLog.Warning($"MyParser 微信视频号文件上传失败: sph_id={result.SphId}, detail={uploadEx.Message}");
                    }
                }
            }

            await ReplyAsync(message, FormatResult(result, videoSent, videoSendError, fileUploaded, fileUploadInfo)).ConfigureAwait(false);
            await ReactAsync(message, "9", WeixinChannelsConstants.DisplayName).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (WeixinChannelsParseException ex)
        {
            BotLog.Warning($"MyParser 微信视频号解析失败：{ex.Message}");
            await ReplyAsync(message, "微信视频号解析失败：" + ex.Message).ConfigureAwait(false);
            await ReactAsync(message, "9", WeixinChannelsConstants.DisplayName).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            BotLog.Warning($"MyParser 微信视频号解析异常：{ex}");
            await ReplyAsync(message, "微信视频号解析异常：" + ex.Message).ConfigureAwait(false);
            await ReactAsync(message, "9", WeixinChannelsConstants.DisplayName).ConfigureAwait(false);
        }
    }

    private async Task SendCardAsync(IncomingMessage message, WeixinChannelsParseResult result)
    {
        var uri = await BuildCardUriAsync(result).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(uri))
        {
            return;
        }

        var segment = new ImageOutgoingSegment(uri);
        var stopwatch = Stopwatch.StartNew();
        BotLog.Info($"MyParser 微信视频号卡片 ImageSegment 发送开始: sph_id={result.SphId}, scene={GetMessageScene(message)}, uri_preview={HostServices.PreviewUri(uri)}");
        var response = await BotContext.Message.ReplyAsync(message, segment).ConfigureAwait(false);
        BotLog.Info($"MyParser 微信视频号卡片 ImageSegment 发送接口完成: sph_id={result.SphId}, scene={GetMessageScene(message)}, message_id={response.MessageId}, elapsed={stopwatch.Elapsed:mm\\:ss}");
    }

    private async Task<string> BuildCardUriAsync(WeixinChannelsParseResult result)
    {
        var coverTask = HostServices.BuildProviderImageAsync(new ProviderImageBuildRequest(
            WeixinChannelsConstants.DisplayName,
            result.CoverUrl,
            result.ShareUrl,
            $"wxchannels_cover_{result.SphId}",
            request =>
            {
                request.Headers.TryAddWithoutValidation("User-Agent", WeixinChannelsConstants.UserAgent);
                request.Headers.TryAddWithoutValidation("Referer", result.ShareUrl);
            }));
        var avatarTask = BotContext.Render is null
            ? Task.FromResult(new ProviderImageBuildResult(string.Empty, null))
            : HostServices.BuildProviderImageAsync(new ProviderImageBuildRequest(
                "微信视频号头像",
                result.AuthorAvatarUrl,
                result.ShareUrl,
                $"wxchannels_avatar_{result.SphId}",
                request => request.Headers.TryAddWithoutValidation("User-Agent", WeixinChannelsConstants.UserAgent)));
        var cover = await coverTask.ConfigureAwait(false);
        if (BotContext.Render is null)
        {
            return cover.Uri;
        }

        try
        {
            var avatar = await avatarTask.ConfigureAwait(false);
            var vm = new WeixinChannelsCardViewModel
            {
                Cover = !string.IsNullOrWhiteSpace(cover.LocalPath) ? HostServices.DecodeImageFileForRender(cover.LocalPath) : HostServices.DecodeBase64ImageForRender(cover.Uri),
                Avatar = !string.IsNullOrWhiteSpace(avatar.LocalPath) ? HostServices.DecodeImageFileForRender(avatar.LocalPath) : HostServices.DecodeBase64ImageForRender(avatar.Uri),
                Title = string.IsNullOrWhiteSpace(result.Title) ? "微信视频号" : result.Title!,
                Description = string.IsNullOrWhiteSpace(result.Description) ? result.ShareUrl : result.Description!,
                AuthorName = string.IsNullOrWhiteSpace(result.AuthorName) ? "微信视频号作者" : result.AuthorName!,
                DurationText = FormatDuration(result.DurationSeconds),
                PublishText = result.PublishTime is null ? "微信视频号" : $"微信视频号 · {result.PublishTime.Value:yyyy-MM-dd}",
                LikeCount = "赞 " + FormatCountText(result.LikeCountText),
                FavoriteCount = "收藏 " + FormatCountText(result.FavoriteCountText),
                CommentCount = "评论 " + FormatCountText(result.CommentCountText),
                ForwardCount = "转发 " + FormatCountText(result.ForwardCountText),
            };
            var png = await BotContext.RenderControlPngAsync<WeixinChannelsCard>(vm, new ControlRenderOptions(RenderTheme.Auto)).ConfigureAwait(false);
            BotLog.Info($"MyParser 微信视频号卡片渲染完成: sph_id={result.SphId}, png_kb={png.Length / 1024d:F1}, mode=base64");
            return "base64://" + Convert.ToBase64String(png);
        }
        catch (Exception ex)
        {
            BotLog.Warning($"MyParser 微信视频号卡片渲染失败，直接发送封面: sph_id={result.SphId}, error={ex.Message}");
            return cover.Uri;
        }
    }

    private async Task<VideoOutgoingSegment> BuildVideoSegmentAsync(WeixinChannelsParseResult result)
    {
        var (fileUri, localPath) = await HostServices.DownloadProviderVideoAsync(Config, BuildVideoDownloadRequest(result)).ConfigureAwait(false);
        result.LocalVideoFileUri = fileUri;
        result.LocalVideoPath = localPath;
        var segmentResult = await HostServices.BuildLocalVideoSegmentAsync(Config, new ProviderLocalVideoSegmentRequest(
            WeixinChannelsConstants.DisplayName,
            result.SphId,
            localPath,
            fileUri,
            result.CoverUrl,
            "sph_id")).ConfigureAwait(false);
        result.LocalVideoRegisteredToHttpServer = segmentResult.RegisteredToHttpServer;
        return segmentResult.Segment;
    }

    private static ProviderVideoDownloadRequest BuildVideoDownloadRequest(WeixinChannelsParseResult result)
    {
        var candidates = new[] { result.H265VideoUrl, result.H264VideoUrl, result.VideoUrl }
            .Where(i => !string.IsNullOrWhiteSpace(i))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(i => i!)
            .ToArray();
        if (candidates.Length == 0)
        {
            throw new WeixinChannelsParseException("没有可下载的视频地址。");
        }

        return new ProviderVideoDownloadRequest(
            WeixinChannelsConstants.ProviderId,
            WeixinChannelsConstants.DisplayName,
            result.SphId,
            $"wxchannels:{result.SphId}:{result.ExportId}",
            candidates,
            MyParserRuntime.WeixinChannelsDownloadDirectory,
            "wxchannels",
            "mp4",
            (method, url, range) => WeixinChannelsParser.CreateVideoRequest(method, url, result, range),
            ProviderVideoValidationKind.Mp4,
            "sph_id");
    }

    private async Task SendVideoMessageAsync(IncomingMessage message, WeixinChannelsParseResult result, VideoOutgoingSegment videoSegment)
    {
        var segments = new OutgoingSegment[] { videoSegment };
        var stopwatch = Stopwatch.StartNew();
        BotLog.Info($"MyParser 微信视频号 VideoSegment 发送开始: sph_id={result.SphId}, scene={GetMessageScene(message)}, uri_mode={HostServices.GetUriMode(videoSegment.Uri)}, uri_preview={HostServices.PreviewUri(videoSegment.Uri)}");
        var response = await BotContext.Message.ReplyAsync(message, segments).ConfigureAwait(false);
        BotLog.Info($"MyParser 微信视频号 VideoSegment 发送接口完成: sph_id={result.SphId}, scene={GetMessageScene(message)}, message_id={response.MessageId}, elapsed={stopwatch.Elapsed:mm\\:ss}");
    }

    private Task<string> UploadVideoFileAsync(IncomingMessage message, WeixinChannelsParseResult result)
    {
        return HostServices.UploadLocalVideoFileAsync(Config, message, result.LocalVideoPath, WeixinChannelsConstants.DisplayName, result.SphId);
    }

    private void CleanupLocalVideoAfterSend(WeixinChannelsParseResult result)
    {
        if (result.LocalVideoRegisteredToHttpServer && Config.DeleteLocalVideoDelaySeconds <= 0)
        {
            HostServices.UnregisterLocalVideoFile(result.LocalVideoPath);
            result.LocalVideoRegisteredToHttpServer = false;
        }

        HostServices.DeleteLocalVideoIfConfigured(Config, result.LocalVideoPath, WeixinChannelsConstants.ProviderId);
    }

    private static string FormatResult(WeixinChannelsParseResult result, bool videoSent, string? videoSendError, bool fileUploaded, string? fileUploadInfo)
    {
        var builder = new StringBuilder();
        builder.AppendLine("微信视频号解析完成");
        builder.AppendLine($"标题：{ProviderTextUtilities.TrimLine(result.Title ?? result.Description ?? "微信视频号", 80)}");
        builder.AppendLine($"作者：{result.AuthorName ?? "未知"}");
        builder.AppendLine($"链接：{result.ShareUrl}");
        builder.AppendLine(videoSent ? "视频：已发送" : "视频：未发送" + (string.IsNullOrWhiteSpace(videoSendError) ? string.Empty : "，" + videoSendError));
        if (fileUploaded || !string.IsNullOrWhiteSpace(fileUploadInfo))
        {
            builder.AppendLine("文件：" + (fileUploaded ? fileUploadInfo : "上传失败：" + fileUploadInfo));
        }

        return builder.ToString().TrimEnd();
    }

    private static string FormatDuration(int seconds)
    {
        if (seconds <= 0)
        {
            return "--:--";
        }

        var time = TimeSpan.FromSeconds(seconds);
        return time.TotalHours >= 1 ? time.ToString(@"h\:mm\:ss") : time.ToString(@"m\:ss");
    }

    private static string FormatCountText(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? "--" : value;
    }
}
