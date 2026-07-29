using System.Diagnostics;
using System.Net;
using System.Text;
using MyParser.Provider.Douyin.Infrastructure;
using MyParser.Provider.Douyin.Models;
using MyParser.Provider.Douyin.Views;
using ShiroBot.AvaloniaSdk;
using ShiroBot.SDK.Models;
using ShiroBot.SDK.Abstractions;
using ShiroBot.SDK.Core;
using ShiroBot.SDK.Plugin;
using Shirobot.Plugin.MyParser.Parsing;
using static MyParser.Provider.Douyin.Infrastructure.DouyinRequestHeaders;

namespace MyParser.Provider.Douyin.MessageHandling;

internal sealed partial class DouyinMessageHandler
{
private async Task SendGalleryMessageAsync(
        IncomingMessage message,
        DouyinParseResult result,
        CancellationToken cancellationToken)
    {
        if (result.Images.Count == 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await _context.Message.ReplyAsync(message, FormatDouyinResult(result));
            return;
        }

        if (result.Images.Count == 1)
        {
            await SendSingleGalleryImageAsync(message, result, result.Images[0], cancellationToken);
            return;
        }

        var forwardedMessages = new List<OutgoingForwardedMessage>();
        var senderId = GetBotOrSenderId(message);
        var senderName = string.IsNullOrWhiteSpace(result.AuthorName) ? "抖音图文" : result.AuthorName!;

        var mediaInputs = result.Images.Select((image, index) => (image, Index: index + 1)).ToArray();
        var mediaFiles = await _hostServices.SelectParallelOrderedAsync(
            mediaInputs,
            4,
            item => BuildGalleryMediaAsync(result, item.image, item.Index));
        if (cancellationToken.IsCancellationRequested)
        {
            foreach (var mediaFile in mediaFiles)
            {
                CleanupGalleryLivePhoto(mediaFile);
            }

            cancellationToken.ThrowIfCancellationRequested();
        }
        foreach (var mediaFile in mediaFiles)
        {
            if (mediaFile.Segments.Count == 0)
            {
                continue;
            }

            forwardedMessages.Add(new OutgoingForwardedMessage(senderId, senderName, mediaFile.Segments));
        }

        if (forwardedMessages.Count == 0)
        {
            foreach (var mediaFile in mediaFiles)
            {
                CleanupGalleryLivePhoto(mediaFile);
            }

            await _context.Message.ReplyAsync(message, FormatDouyinResult(result));
            return;
        }

        var title = string.IsNullOrWhiteSpace(result.Title) ? "抖音图文" : TrimLine(result.Title, 48);
        var preview = result.Images.Take(4).Select((_, index) => $"图片 {index + 1}").ToArray();
        var summary = $"共 {result.Images.Count} 张";
        var forward = new ForwardOutgoingSegment(forwardedMessages, title, preview, summary, "抖音图文");
        var stopwatch = Stopwatch.StartNew();
        BotLog.Info($"MyParser 图文合并转发发送开始: aweme_id={result.AwemeId}, scene={GetMessageScene(message)}, images={forwardedMessages.Count}/{result.Images.Count}");

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var response = await _context.Message.ReplyAsync(message, forward);
            BotLog.Info($"MyParser 图文消息发送完成: aweme_id={result.AwemeId}, scene={GetMessageScene(message)}, message_id={response.MessageId}, time={response.Timestamp}, elapsed={stopwatch.Elapsed:mm\\:ss}");
        }
        finally
        {
            foreach (var mediaFile in mediaFiles)
            {
                CleanupGalleryLivePhoto(mediaFile);
            }
        }
    }

    private async Task SendSingleGalleryImageAsync(
        IncomingMessage message,
        DouyinParseResult result,
        DouyinImageInfo image,
        CancellationToken cancellationToken)
    {
        var mediaFile = await BuildGalleryMediaAsync(result, image, 1);
        var segments = mediaFile.Segments.ToArray();
        var stopwatch = Stopwatch.StartNew();
        BotLog.Info($"MyParser 单图图文媒体发送开始: aweme_id={result.AwemeId}, scene={GetMessageScene(message)}, live_photo={!string.IsNullOrWhiteSpace(image.LivePhotoUrl)}, segments={segments.Length}");

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var response = await _context.Message.ReplyAsync(message, segments);
            BotLog.Info($"MyParser 单图图文媒体发送完成: aweme_id={result.AwemeId}, scene={GetMessageScene(message)}, message_id={response.MessageId}, time={response.Timestamp}, elapsed={stopwatch.Elapsed:mm\\:ss}");
        }
        finally
        {
            CleanupGalleryLivePhoto(mediaFile);
        }
    }

    private async Task<GalleryMediaBuildResult> BuildGalleryMediaAsync(
        DouyinParseResult result,
        DouyinImageInfo image,
        int index)
    {
        var imageFile = await BuildRemoteImageAsync(
            image.Url,
            result.SourceUrl,
            $"douyin_image_{result.AwemeId}_{index:D2}",
            persistLocalFile: !string.IsNullOrWhiteSpace(image.LivePhotoUrl));
        BotLog.Info($"MyParser 图文图片发送资源物理位置: aweme_id={result.AwemeId}, index={index}, physical_path={DescribePhysicalPath(imageFile.LocalPath)}");

        if (!_config.SendVideoSegment || string.IsNullOrWhiteSpace(image.LivePhotoUrl))
        {
            return new GalleryMediaBuildResult([new ImageOutgoingSegment(imageFile.Uri)], null, false);
        }

        string? localVideoPath = null;
        try
        {
            var mediaId = $"{result.AwemeId}_{index:D2}";
            var (fileUri, localPath) = await ConvertLivePhotoToMp4Async(result, image.LivePhotoUrl, mediaId);
            localVideoPath = localPath;
            var thumbUri = BuildLocalFileUri(imageFile.LocalPath);
            BotLog.Info($"MyParser Live Photo 准备上传 MP4: aweme_id={result.AwemeId}, index={index}, physical_path={DescribePhysicalPath(localPath)}");
            BotLog.Info($"MyParser Live Photo 缩略图本地化: aweme_id={result.AwemeId}, index={index}, physical_path={DescribePhysicalPath(imageFile.LocalPath)}, thumb_uri={thumbUri ?? "<none>"}");
            var segmentResult = await _hostServices.BuildLocalVideoSegmentAsync(
                _config,
                new ProviderLocalVideoSegmentRequest(
                    "抖音 Live Photo",
                    mediaId,
                    localPath,
                    fileUri,
                    thumbUri,
                    "live_photo_id"));

            BotLog.Info($"MyParser Live Photo 已下载为 MP4: aweme_id={result.AwemeId}, index={index}, path={localPath}");
            return new GalleryMediaBuildResult([segmentResult.Segment], localPath, segmentResult.RegisteredToHttpServer);
        }
        catch (Exception ex)
        {
            _hostServices.DeleteLocalVideoIfConfigured(_config, localVideoPath, "douyin-live-photo");
            BotLog.Warning($"MyParser Live Photo 下载或发送准备失败，回退静图: aweme_id={result.AwemeId}, index={index}, error={ex.Message}");
            return new GalleryMediaBuildResult([new ImageOutgoingSegment(imageFile.Uri)], null, false);
        }
    }

    private Task<(string FileUri, string LocalPath)> ConvertLivePhotoToMp4Async(
        DouyinParseResult result,
        string livePhotoUrl,
        string mediaId)
    {
        // 抖音 Live Photo 的动态资源本身是 MP4；下载服务落盘为 .mp4 并校验 ftyp 后才允许发送。
        var downloadRequest = new ProviderVideoDownloadRequest(
            "douyin",
            "抖音 Live Photo",
            mediaId,
            $"douyin-live-photo:{mediaId}",
            [livePhotoUrl],
            MyParserRuntime.DownloadDirectory,
            "douyin_live_photo",
            "mp4",
            (method, url, range) => CreateVideoRequest(method, url, result, range),
            ProviderVideoValidationKind.Mp4,
            "live_photo_id");
        return _hostServices.DownloadProviderVideoAsync(_config, downloadRequest);
    }

    private void CleanupGalleryLivePhoto(GalleryMediaBuildResult mediaFile)
    {
        if (string.IsNullOrWhiteSpace(mediaFile.LocalVideoPath))
        {
            return;
        }

        if (mediaFile.RegisteredToHttpServer && _config.DeleteLocalVideoDelaySeconds <= 0)
        {
            _hostServices.UnregisterLocalVideoFile(mediaFile.LocalVideoPath);
        }

        _hostServices.DeleteLocalVideoIfConfigured(_config, mediaFile.LocalVideoPath, "douyin-live-photo");
    }

    private sealed record GalleryMediaBuildResult(
        IReadOnlyList<OutgoingSegment> Segments,
        string? LocalVideoPath,
        bool RegisteredToHttpServer);

    private async Task SendMusicMessageAsync(IncomingMessage message, DouyinParseResult result)
    {
        if (string.IsNullOrWhiteSpace(result.MusicUrl))
        {
            return;
        }

        string? localPath = null;
        var stopwatch = Stopwatch.StartNew();

        try
        {
            var downloadRequest = new ProviderAudioDownloadRequest(
                "douyin",
                "抖音图文音乐",
                result.AwemeId,
                $"douyin-gallery-music:{result.AwemeId}",
                result.MusicUrl,
                MyParserRuntime.DownloadDirectory,
                "douyin_gallery_music",
                "mp3",
                (method, url, range) => CreateVideoRequest(method, url, result, range),
                "aweme_id");
            var (_, downloadedPath) = await _hostServices.DownloadProviderAudioAsync(_config, downloadRequest);
            localPath = downloadedPath;
            var variants = await _hostServices.BuildSilkRecordVariantsAsync(
                _config,
                new ProviderRecordBuildRequest(
                    "douyin",
                    "抖音图文音乐",
                    result.AwemeId,
                    downloadedPath,
                    "douyin_gallery_music",
                    IncludeMobileBest: false));
            foreach (var variant in variants)
            {
                var recordUri = await _hostServices.BuildRecordUriAsync(variant.Path);
                var segment = new RecordOutgoingSegment(recordUri);
                BotLog.Info($"MyParser 图文音乐 SILK RecordSegment 发送开始: aweme_id={result.AwemeId}, variant={variant.Name}, scene={GetMessageScene(message)}, silk_path={variant.Path}, file_kb={new FileInfo(variant.Path).Length / 1024d:F1}, uri_preview={_hostServices.PreviewUri(recordUri)}");
                var response = await _context.Message.ReplyAsync(message, segment);
                if (string.IsNullOrWhiteSpace(response.MessageId))
                {
                    throw new InvalidOperationException("抖音图文音乐 SILK 发送未返回有效 message_id。");
                }

                BotLog.Info($"MyParser 图文音乐 SILK AudioSegment 发送完成: aweme_id={result.AwemeId}, variant={variant.Name}, scene={GetMessageScene(message)}, message_id={response.MessageId}, time={response.Timestamp}, elapsed={stopwatch.Elapsed:mm\\:ss}");
            }
        }
        catch (Exception ex)
        {
            BotLog.Warning($"MyParser 图文音乐 RecordSegment 发送失败，回退文本链接: aweme_id={result.AwemeId}, error={ex.Message}");
            await _context.Message.ReplyAsync(message, "音乐：" + result.MusicUrl);
        }
        finally
        {
            _hostServices.DeleteLocalVideoIfConfigured(_config, localPath, "douyin-gallery-music");
        }
    }
}
