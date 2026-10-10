using System.Net;
using ShiroBot.SDK.Models;
using ShiroBot.SDK.Plugin;
using Shirobot.Plugin.MyParser.Parsing;

namespace Shirobot.Plugin.MyParser.MessageHandling;

internal sealed class UnifiedMediaMessageHandler(ProviderMessageHandlerContext context) : ProviderMessageHandlerBase(context)
{
    private readonly ProviderMediaCardRenderer _cardRenderer = new(context.BotContext, context.HostServices);
    public override string ProviderId => PrimaryProvider.Id;

    protected override async Task PresentAsync(
        MessageEvent message,
        ParsedMedia media,
        bool silentProviderMismatch,
        CancellationToken cancellationToken)
    {
        if (Config.IsCoverEnabled(media.ProviderId))
        {
            await SendCoverAsync(message, media, cancellationToken).ConfigureAwait(false);
        }

        if (media.Kind == ParsedMediaKind.Track
            && Config.SendNetEaseCloudMusicLyricCard
            && media.Attributes.TryGetValue("lyrics", out var lyrics)
            && !string.IsNullOrWhiteSpace(lyrics))
        {
            var translated = media.Attributes.GetValueOrDefault("translated_lyrics");
            var lyricMedia = media with
            {
                Title = $"{media.Title} · 歌词",
                Description = string.IsNullOrWhiteSpace(translated) ? lyrics : lyrics + Environment.NewLine + translated,
                Attributes = new Dictionary<string, string>(),
            };
            var lyricCard = await RenderCardAsync(lyricMedia, ProviderCardPurpose.Lyrics, cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(lyricCard)) await SendImageAsync(message, new ImageSegment(lyricCard)).ConfigureAwait(false);
        }

        var hasComments = media.Content.Any(block => block.Attributes.GetValueOrDefault("category") == "comment");
        if (hasComments)
        {
            string? commentsCard = null;
            if (CardRenderer is not null)
            {
                try
                {
                    commentsCard = await CardRenderer.RenderAsync(media, ProviderCardPurpose.Comments, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    BotLog.Warning($"MyParser {media.ProviderName} 评论卡片渲染失败，回退文本: media_id={media.MediaId}, error={ex.Message}");
                }
            }

            if (!string.IsNullOrWhiteSpace(commentsCard))
            {
                await SendImageAsync(message, new ImageSegment(commentsCard)).ConfigureAwait(false);
            }
            else
            {
                await SendContentAsync(message, media, cancellationToken).ConfigureAwait(false);
            }
        }

        var succeeded = true;
        switch (media.Kind)
        {
            case ParsedMediaKind.Video:
                if (Config.IsVideoDeliveryEnabled())
                {
                    succeeded = await SendVideoAsync(message, media, cancellationToken).ConfigureAwait(false);
                }
                var accompanyingImages = media.Assets.Where(asset => asset.Kind == MediaAssetKind.Image).ToArray();
                if (accompanyingImages.Length > 0)
                {
                    await SendGalleryAsync(message, media with
                    {
                        Kind = ParsedMediaKind.Gallery,
                        Assets = accompanyingImages,
                    }, cancellationToken).ConfigureAwait(false);
                }
                break;
            case ParsedMediaKind.Track:
                succeeded = await SendTrackAsync(message, media, cancellationToken).ConfigureAwait(false);
                break;
            case ParsedMediaKind.Article:
                await SendContentAsync(message, media, cancellationToken).ConfigureAwait(false);
                break;
            case ParsedMediaKind.Gallery:
                await SendGalleryAsync(message, media, cancellationToken).ConfigureAwait(false);
                break;
            case ParsedMediaKind.Collection:
                await SendCollectionAsync(message, media).ConfigureAwait(false);
                break;
            case ParsedMediaKind.Live:
                succeeded = await SendLiveInfoAsync(message, media, cancellationToken).ConfigureAwait(false);
                break;
        }

        if (succeeded) await ReactAsync(message, "426", ReactionPlatformName).ConfigureAwait(false);
    }

    private async Task SendCoverAsync(MessageEvent message, ParsedMedia media, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(media.CoverUrl)) return;

        var purpose = media.Kind == ParsedMediaKind.Article ? ProviderCardPurpose.Article : ProviderCardPurpose.Main;
        var uri = await RenderCardAsync(media, purpose, cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(uri)) await SendImageAsync(message, new ImageSegment(uri)).ConfigureAwait(false);
    }

    private async Task<string?> RenderCardAsync(ParsedMedia media, ProviderCardPurpose purpose, CancellationToken cancellationToken)
    {
        if (CardRenderer is not null)
        {
            try
            {
                var custom = await CardRenderer.RenderAsync(media, purpose, cancellationToken).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(custom)) return custom;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                BotLog.Warning($"MyParser {media.ProviderName} 专属卡片渲染失败，回退统一卡片: media_id={media.MediaId}, error={ex.Message}");
            }
        }

        return await _cardRenderer.RenderAsync(media, cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> SendVideoAsync(MessageEvent message, ParsedMedia media, CancellationToken cancellationToken)
    {
        var videos = media.Assets.Where(asset => asset.Kind == MediaAssetKind.Video).ToArray();
        if (videos.Length == 0) return true;

        var audios = media.Assets.Where(asset => asset.Kind == MediaAssetKind.Audio).ToArray();
        string? localPath = null;
        var registered = false;
        try
        {
            if (PrimaryProvider is IParseProviderWithParser parserProvider
                && parserProvider.ParserObject is IVideoDownloadGate downloadGate)
            {
                downloadGate.EnsureVideoDownloadAllowed();
            }

            string fileUri;
            if (audios.Length > 0)
            {
                var request = new ProviderMuxedVideoDownloadRequest(
                    media.ProviderId,
                    media.ProviderName,
                    media.MediaId,
                    videos[0].CacheKey ?? $"{media.ProviderId}:{media.MediaId}",
                    media.Title,
                    videos[0].DownloadDirectory,
                    videos.Select(ToMuxedStream).ToArray(),
                    audios.Select(ToMuxedStream).ToArray(),
                    CreateRequestFactory(videos[0]),
                    "media_id");
                (fileUri, localPath) = await HostServices.DownloadMuxedProviderVideoAsync(Config, request, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                var selected = videos[0];
                var request = new ProviderVideoDownloadRequest(
                    media.ProviderId,
                    media.ProviderName,
                    media.MediaId,
                    selected.CacheKey ?? $"{media.ProviderId}:{media.MediaId}",
                    videos.SelectMany(asset => string.IsNullOrWhiteSpace(asset.Url) ? asset.BackupUrls : new[] { asset.Url }.Concat(asset.BackupUrls))
                        .Distinct(StringComparer.Ordinal).ToArray(),
                    selected.DownloadDirectory,
                    selected.FileNamePrefix ?? media.ProviderId,
                    selected.FileExtension,
                    CreateRequestFactory(selected),
                    ProviderVideoValidationKind.Mp4,
                    "media_id");
                (fileUri, localPath) = await HostServices.DownloadProviderVideoAsync(Config, request, cancellationToken).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            var segment = await HostServices.BuildLocalVideoSegmentAsync(Config,
                new ProviderLocalVideoSegmentRequest(media.ProviderName, media.MediaId, localPath, fileUri, media.CoverUrl),
                cancellationToken).ConfigureAwait(false);
            registered = segment.RegisteredToHttpServer;
            var sent = await HostServices.SendSegmentsAsync(message, [segment.Segment]).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(sent.MessageId))
            {
                throw new InvalidOperationException("视频发送没有返回消息 ID。");
            }

            if (Config.IsVideoFileUploadEnabled() && !Config.UploadVideoAsFileOnlyOnVideoSendFailure)
            {
                await HostServices.UploadLocalVideoFileAsync(Config, message, localPath, media.ProviderName, media.MediaId).ConfigureAwait(false);
            }
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            if (ex is IProviderClassifiedException { FailureKind: ProviderFailureKind.AuthenticationRequired })
            {
                throw;
            }

            BotLog.Warning($"MyParser {media.ProviderName} 视频投递失败: media_id={media.MediaId}, error={ex.Message}");
            if (Config.IsVideoFileUploadEnabled() && !string.IsNullOrWhiteSpace(localPath))
            {
                try
                {
                    var upload = await HostServices.UploadLocalVideoFileAsync(Config, message, localPath, media.ProviderName, media.MediaId).ConfigureAwait(false);
                    await ReplyAsync(message, $"视频已解析，VideoSegment 发送失败，已回退上传文件：{upload}").ConfigureAwait(false);
                    await ReactAsync(message, "426", ReactionPlatformName).ConfigureAwait(false);
                    return true;
                }
                catch (Exception uploadException)
                {
                    BotLog.Warning($"MyParser {media.ProviderName} 视频文件回退上传失败: media_id={media.MediaId}, error={uploadException.Message}");
                }
            }

            await ReportFailureAsync(message, ProviderFailureKind.MediaDelivery, ex, $"media_id={media.MediaId}").ConfigureAwait(false);
            await ReactAsync(message, "9", ReactionPlatformName).ConfigureAwait(false);
            return false;
        }
        finally
        {
            if (registered && Config.DeleteLocalVideoDelaySeconds <= 0)
            {
                HostServices.UnregisterLocalVideoFile(localPath);
            }

            HostServices.DeleteLocalVideoIfConfigured(Config, localPath, media.ProviderId);
        }
    }

    private async Task<bool> SendTrackAsync(MessageEvent message, ParsedMedia media, CancellationToken cancellationToken, string? existingPath = null)
    {
        var audio = media.Assets.FirstOrDefault(asset => asset.Kind == MediaAssetKind.Audio);
        if (audio is null) return true;

        string? localAudioPath = existingPath;
        var ownsLocalAudioPath = existingPath is null;
        try
        {
            if (localAudioPath is null)
            {
                var request = new ProviderAudioDownloadRequest(
                    media.ProviderId, media.ProviderName, media.MediaId,
                    audio.CacheKey ?? $"{media.ProviderId}:{media.MediaId}", audio.Url,
                    audio.DownloadDirectory, audio.FileNamePrefix ?? media.Title ?? media.MediaId,
                    audio.FileExtension, CreateRequestFactory(audio), "media_id");
                var (_, localPath) = await HostServices.DownloadProviderAudioAsync(Config, request, cancellationToken).ConfigureAwait(false);
                localAudioPath = localPath;
            }
            if (!Config.EnableSilkEncoding)
            {
                var recordUri = await HostServices.BuildRecordUriAsync(localAudioPath ?? throw new InvalidOperationException("音频下载未生成本地文件。"), cancellationToken).ConfigureAwait(false);
                var sent = await HostServices.SendSegmentsAsync(message, [new RecordOutgoingSegment(recordUri)]).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(sent.MessageId)) throw new InvalidOperationException("音频发送没有返回消息 ID。");
                return true;
            }
            var variants = await HostServices.BuildSilkRecordVariantsAsync(Config,
                new ProviderRecordBuildRequest(media.ProviderId, media.ProviderName, media.MediaId, localAudioPath!,
                    audio.FileNamePrefix ?? media.Title ?? media.MediaId,
                    media.ProviderId != "douyin" && Config.SendNetEaseMobileBestRecord), cancellationToken).ConfigureAwait(false);
            foreach (var variant in variants)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var uri = await HostServices.BuildRecordUriAsync(variant.Path, cancellationToken).ConfigureAwait(false);
                var sent = await HostServices.SendSegmentsAsync(message, [new RecordOutgoingSegment(uri)]).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(sent.MessageId)) throw new InvalidOperationException("音频发送没有返回消息 ID。");
            }
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            BotLog.Warning($"MyParser {media.ProviderName} 音频投递失败: media_id={media.MediaId}, error={ex.Message}");
            try
            {
                var upload = await HostServices.UploadLocalFileAsync(Config, message, localAudioPath,
                    media.ProviderName, media.MediaId, preferBase64: true).ConfigureAwait(false);
                await ReplyAsync(message, $"音频发送失败，已回退文件上传：{upload}").ConfigureAwait(false);
                await ReactAsync(message, "426", ReactionPlatformName).ConfigureAwait(false);
                return true;
            }
            catch (Exception uploadException)
            {
                BotLog.Warning($"MyParser {media.ProviderName} 音频文件回退失败: media_id={media.MediaId}, error={uploadException.Message}");
            }
            await ReportFailureAsync(message, ProviderFailureKind.MediaDelivery, ex, $"media_id={media.MediaId}").ConfigureAwait(false);
            await ReactAsync(message, "9", ReactionPlatformName).ConfigureAwait(false);
            return false;
        }
        finally
        {
            if (ownsLocalAudioPath)
                HostServices.DeleteLocalVideoIfConfigured(Config, localAudioPath, media.ProviderId);
        }
    }

    private async Task SendContentAsync(MessageEvent message, ParsedMedia media, CancellationToken cancellationToken)
    {
        foreach (var block in media.Content)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (block.Kind == MediaContentKind.Image)
            {
                await SendRemoteImageAsync(message, block.Url, block.Caption, media, cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (block.Kind == MediaContentKind.Video)
            {
                var video = media.Assets.FirstOrDefault(asset => asset.Kind == MediaAssetKind.Video
                    && string.Equals(asset.Url, block.Url, StringComparison.Ordinal));
                if (video is not null && Config.IsVideoDeliveryEnabled())
                {
                    var videoMedia = media with { Kind = ParsedMediaKind.Video, Assets = [video] };
                    if (!await SendVideoAsync(message, videoMedia, cancellationToken).ConfigureAwait(false))
                    {
                        await ReplyAsync(message, block.Url).ConfigureAwait(false);
                    }
                }
                else if (!string.IsNullOrWhiteSpace(block.Url))
                {
                    await ReplyAsync(message, $"正文视频：{block.Url}").ConfigureAwait(false);
                }

                continue;
            }

            var text = block.Attributes.GetValueOrDefault("category") == "comment"
                ? $"{block.Caption}: {block.Text}"
                : block.Kind == MediaContentKind.Heading ? $"【{block.Text}】" : block.Text;
            if (!string.IsNullOrWhiteSpace(text))
            {
                await ReplyAsync(message, text).ConfigureAwait(false);
            }
        }
    }

    private async Task SendGalleryAsync(MessageEvent message, ParsedMedia media, CancellationToken cancellationToken)
    {
        var images = media.Assets.Where(asset => asset.Kind == MediaAssetKind.Image).ToArray();
        var galleryMessages = new List<OutgoingForwardedMessage>();
        var senderId = message.Sender.Id;
        var senderName = string.IsNullOrWhiteSpace(media.AuthorName) ? "抖音图文" : media.AuthorName!;
        var audio = media.ProviderId == "douyin"
            ? media.Assets.FirstOrDefault(asset => asset.Kind == MediaAssetKind.Audio)
            : null;
        var pendingGalleryCleanup = new List<(string? VideoPath, string? SourcePath, string? ImagePath, bool Registered)>();
        string? musicPath = null;
        var musicMerged = false;
        try
        {
            if (Config.SendVideoSegment && audio is not null)
            {
                try
                {
                    var request = new ProviderAudioDownloadRequest(media.ProviderId, media.ProviderName, media.MediaId,
                        audio.CacheKey ?? $"{media.ProviderId}:{media.MediaId}", audio.Url, audio.DownloadDirectory,
                        audio.FileNamePrefix ?? media.Title ?? media.MediaId, audio.FileExtension,
                        CreateRequestFactory(audio), "aweme_id");
                    (_, musicPath) = await HostServices.DownloadProviderAudioAsync(Config, request, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception ex) { BotLog.Warning($"MyParser 抖音图文音乐预下载失败: media_id={media.MediaId}, error={ex.Message}"); }
            }

            for (var index = 0; index < images.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var image = images[index];
                var livePhotoUrl = media.Attributes.GetValueOrDefault($"gallery_live_photo_{index}");
                var shouldPersistImage = Config.SendVideoSegment
                    && (!string.IsNullOrWhiteSpace(livePhotoUrl) || !string.IsNullOrWhiteSpace(musicPath));
                var imageResult = await HostServices.BuildProviderImageAsync(new ProviderImageBuildRequest(
                    media.ProviderName, image.Url, media.SourceUrl, $"{image.FileNamePrefix ?? "image"}_{media.MediaId}_{index + 1:D2}",
                    request => ApplyHeaders(request, image.RequestHeaders), PersistLocalFile: shouldPersistImage), cancellationToken).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(imageResult.Uri)) continue;

                if (shouldPersistImage && imageResult.LocalPath is not null)
                {
                    var sourceVideoPath = (string?)null;
                    var outputVideoPath = (string?)null;
                    var registeredToHttpServer = false;
                    var keepFilesUntilSend = false;
                    try
                    {
                        if (!string.IsNullOrWhiteSpace(livePhotoUrl))
                        {
                            try
                            {
                                var videoRequest = new ProviderVideoDownloadRequest("douyin", "抖音 Live Photo",
                                    $"{media.MediaId}_{index + 1:D2}", $"douyin-live-photo:{media.MediaId}:{index}",
                                    [livePhotoUrl], audio?.DownloadDirectory ?? Path.GetDirectoryName(imageResult.LocalPath)!,
                                    "douyin_live_photo", "mp4", CreateRequestFactory(image),
                                    ProviderVideoValidationKind.Mp4, "live_photo_id");
                                (_, sourceVideoPath) = await HostServices.DownloadProviderVideoAsync(Config, videoRequest, cancellationToken).ConfigureAwait(false);
                            }
                            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                            catch (Exception ex) { BotLog.Warning($"MyParser Live Photo 下载失败，尝试静图配乐: media_id={media.MediaId}, index={index}, error={ex.Message}"); }
                        }

                        if (musicPath is not null)
                        {
                            outputVideoPath = sourceVideoPath is null
                                ? await HostServices.CreateStillImageVideoWithAudioAsync(Config, imageResult.LocalPath, musicPath, cancellationToken).ConfigureAwait(false)
                                : await HostServices.MuxLoopingVideoWithAudioAsync(Config, sourceVideoPath, musicPath, cancellationToken).ConfigureAwait(false);
                        }
                        else
                        {
                            outputVideoPath = sourceVideoPath;
                        }

                        if (outputVideoPath is not null)
                        {
                            var videoSegment = await HostServices.BuildLocalVideoSegmentAsync(Config,
                                new ProviderLocalVideoSegmentRequest("抖音图文视频", $"{media.MediaId}_{index + 1:D2}",
                                    outputVideoPath, new Uri(outputVideoPath).AbsoluteUri,
                                    new Uri(imageResult.LocalPath).AbsoluteUri, "live_photo_id"), cancellationToken).ConfigureAwait(false);
                            registeredToHttpServer = videoSegment.RegisteredToHttpServer;
                            galleryMessages.Add(new OutgoingForwardedMessage(senderId, senderName, [videoSegment.Segment]));
                            pendingGalleryCleanup.Add((outputVideoPath, sourceVideoPath, imageResult.LocalPath, registeredToHttpServer));
                            keepFilesUntilSend = true;
                            if (musicPath is not null) musicMerged = true;
                            continue;
                        }
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                    catch (Exception ex) { BotLog.Warning($"MyParser 图文媒体视频生成/发送失败，回退图片: media_id={media.MediaId}, index={index}, error={ex.Message}"); }
                    finally
                    {
                        if (!keepFilesUntilSend)
                        {
                            if (registeredToHttpServer && Config.DeleteLocalVideoDelaySeconds <= 0)
                                HostServices.UnregisterLocalVideoFile(outputVideoPath);
                            if (outputVideoPath is not null && outputVideoPath != sourceVideoPath)
                                HostServices.DeleteLocalVideoIfConfigured(Config, outputVideoPath, "douyin-gallery-video");
                            if (sourceVideoPath is not null)
                                HostServices.DeleteLocalVideoIfConfigured(Config, sourceVideoPath, "douyin-live-photo-source");
                            try { File.Delete(imageResult.LocalPath); } catch { }
                        }
                    }
                }

                galleryMessages.Add(new OutgoingForwardedMessage(senderId, senderName, [new ImageSegment(imageResult.Uri)]));
                if (!string.IsNullOrWhiteSpace(imageResult.LocalPath))
                {
                    try { File.Delete(imageResult.LocalPath); } catch { }
                }
            }

            if (galleryMessages.Count == 1)
            {
                var sent = await HostServices.SendSegmentsAsync(message, galleryMessages[0].Segments).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(sent.MessageId)) throw new InvalidOperationException("图文媒体发送未返回消息 ID。");
            }
            else if (galleryMessages.Count > 1)
            {
                var title = string.IsNullOrWhiteSpace(media.Title) ? media.ProviderName : TextPreviewFormatter.TrimLine(media.Title, 48);
                var preview = images.Take(4).Select((_, index) => $"图片 {index + 1}").ToArray();
                var forward = new ForwardOutgoingSegment(galleryMessages, title, preview, $"共 {galleryMessages.Count} 张", media.ProviderName);
                var sent = await BotContext.Message.ReplyAsync(message, forward).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(sent.MessageId)) throw new InvalidOperationException("图文合并转发未返回消息 ID。");
            }

            if (audio is not null && !musicMerged)
                await SendTrackAsync(message, media, cancellationToken, musicPath).ConfigureAwait(false);
        }
        finally
        {
            foreach (var cleanup in pendingGalleryCleanup)
            {
                if (cleanup.Registered && Config.DeleteLocalVideoDelaySeconds <= 0)
                    HostServices.UnregisterLocalVideoFile(cleanup.VideoPath);
                if (cleanup.VideoPath is not null && cleanup.VideoPath != cleanup.SourcePath)
                    HostServices.DeleteLocalVideoIfConfigured(Config, cleanup.VideoPath, "douyin-gallery-video");
                if (cleanup.SourcePath is not null)
                    HostServices.DeleteLocalVideoIfConfigured(Config, cleanup.SourcePath, "douyin-live-photo-source");
                if (cleanup.ImagePath is not null)
                {
                    try { File.Delete(cleanup.ImagePath); } catch { }
                }
            }
            if (musicPath is not null)
                HostServices.DeleteLocalVideoIfConfigured(Config, musicPath, "douyin-gallery-music");
        }
    }

    private async Task SendCollectionAsync(MessageEvent message, ParsedMedia media)
    {
        foreach (var asset in media.Assets)
        {
            if (!string.IsNullOrWhiteSpace(asset.Url))
            {
                await ReplyAsync(message, $"{asset.Label ?? "条目"}：{asset.Url}").ConfigureAwait(false);
            }
        }
    }

    private async Task<bool> SendLiveInfoAsync(MessageEvent message, ParsedMedia media, CancellationToken cancellationToken)
    {
        var streams = media.Assets.Where(asset => asset.Kind == MediaAssetKind.LiveStream).ToArray();
        if (streams.Length == 0)
        {
            return true;
        }

        await ReplyAsync(message, $"直播信息已解析，可用线路数：{streams.Length}。").ConfigureAwait(false);
        if (!Config.IsBilibiliLiveReplayEnabled()) return true;

        string? localPath = null;
        var registered = false;
        try
        {
            var replayStreams = streams.Select(asset => new ProviderLiveReplayStream(
                asset.Protocol, asset.Format, asset.Codec, asset.QualityId, asset.CdnIndex, asset.Url)).ToArray();
            var selected = streams[0];
            var request = new ProviderLiveReplayClipDownloadRequest(
                media.ProviderId, media.ProviderName, media.MediaId, selected.DownloadDirectory, replayStreams,
                CreateRequestFactory(selected), CreateRequestFactory(selected), GetLiveStreamRank, "room_id");
            var clip = await HostServices.DownloadLiveReplayClipAsync(Config, request, cancellationToken).ConfigureAwait(false);
            localPath = clip.LocalPath;
            var video = await HostServices.BuildLocalVideoSegmentAsync(Config,
                new ProviderLocalVideoSegmentRequest(media.ProviderName, media.MediaId, clip.LocalPath, clip.FileUri, media.CoverUrl, "room_id"),
                cancellationToken).ConfigureAwait(false);
            registered = video.RegisteredToHttpServer;
            var sent = await HostServices.SendSegmentsAsync(message, [video.Segment]).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(sent.MessageId)) throw new InvalidOperationException("直播回看片段发送没有返回消息 ID。");
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            BotLog.Warning($"MyParser {media.ProviderName} 直播回看片段投递失败: room_id={media.MediaId}, error={ex.Message}");
            if (Config.IsVideoFileUploadEnabled() && !string.IsNullOrWhiteSpace(localPath))
            {
                try
                {
                    var upload = await HostServices.UploadLocalVideoFileAsync(Config, message, localPath, media.ProviderName, media.MediaId).ConfigureAwait(false);
                    await ReplyAsync(message, $"直播回看片段已回退为文件上传：{upload}").ConfigureAwait(false);
                    return true;
                }
                catch (Exception uploadException)
                {
                    BotLog.Warning($"MyParser {media.ProviderName} 直播回看片段文件上传失败: room_id={media.MediaId}, error={uploadException.Message}");
                }
            }

            await ReportFailureAsync(message, ProviderFailureKind.MediaDelivery, ex, $"room_id={media.MediaId}; feature=live-replay").ConfigureAwait(false);
            await ReactAsync(message, "9", ReactionPlatformName).ConfigureAwait(false);
            return false;
        }
        finally
        {
            if (registered && Config.DeleteLocalVideoDelaySeconds <= 0) HostServices.UnregisterLocalVideoFile(localPath);
            HostServices.DeleteLocalVideoIfConfigured(Config, localPath, media.ProviderId);
        }
    }

    private static int GetLiveStreamRank(ProviderLiveReplayStream stream) => (stream.Protocol, stream.Format, stream.Codec) switch
    {
        ("http_hls", "fmp4", "avc") => 0,
        ("http_hls", "ts", "avc") => 1,
        ("http_stream", "flv", "avc") => 2,
        ("http_hls", "fmp4", "hevc") => 3,
        ("http_hls", "ts", "hevc") => 4,
        ("http_stream", "flv", "hevc") => 5,
        ("http_hls", "fmp4", "av1") => 6,
        _ => 99,
    };

    private async Task SendRemoteImageAsync(MessageEvent message, string? url, string? label, ParsedMedia media, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        try
        {
            var result = await HostServices.BuildProviderImageAsync(new ProviderImageBuildRequest(
                media.ProviderName, url, media.SourceUrl, $"{media.ProviderId}_{media.MediaId}_image",
                request => ApplyHeaders(request, GetImageHeaders(media)), PersistLocalFile: false), cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(result.Uri)) await SendImageAsync(message, new ImageSegment(result.Uri)).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(label)) await ReplyAsync(message, label).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            BotLog.Warning($"MyParser {media.ProviderName} 正文图片发送失败: media_id={media.MediaId}, error={ex.Message}");
        }
    }

    private static ProviderMuxedMediaStream ToMuxedStream(MediaAsset asset) =>
        new(asset.QualityId.ToString(), asset.Url, asset.BackupUrls, asset.QualityId, asset.Label ?? string.Empty,
            asset.Width, asset.Height, asset.FrameRate, asset.Codec, asset.Kind == MediaAssetKind.Audio);

    private static Func<HttpMethod, string, string?, HttpRequestMessage> CreateRequestFactory(MediaAsset asset) =>
        (method, url, range) =>
        {
            var request = new HttpRequestMessage(method, url);
            ApplyHeaders(request, asset.RequestHeaders);
            if (string.IsNullOrWhiteSpace(asset.RequestHeaders.GetValueOrDefault("Referer")) && !string.IsNullOrWhiteSpace(asset.Referer))
            {
                request.Headers.TryAddWithoutValidation("Referer", asset.Referer);
            }
            if (!string.IsNullOrWhiteSpace(range)) request.Headers.TryAddWithoutValidation("Range", range);
            return request;
        };

    private static IReadOnlyDictionary<string, string> GetCoverHeaders(ParsedMedia media) =>
        media.Assets.FirstOrDefault(asset => asset.Kind == MediaAssetKind.Image)?.RequestHeaders ?? new Dictionary<string, string>();

    private static IReadOnlyDictionary<string, string> GetImageHeaders(ParsedMedia media) => GetCoverHeaders(media);

    private static void ApplyHeaders(HttpRequestMessage request, IReadOnlyDictionary<string, string> headers)
    {
        foreach (var (key, value) in headers)
        {
            if (string.Equals(key, "Referer", StringComparison.OrdinalIgnoreCase) && Uri.TryCreate(value, UriKind.Absolute, out var referer))
            {
                request.Headers.Referrer = referer;
            }
            else
            {
                request.Headers.TryAddWithoutValidation(key, value);
            }
        }
    }
}
