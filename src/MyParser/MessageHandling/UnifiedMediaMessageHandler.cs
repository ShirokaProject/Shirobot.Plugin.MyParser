using System.Net;
using System.Diagnostics;
using ShiroBot.SDK.Models;
using ShiroBot.SDK.Plugin;
using Shirobot.Plugin.MyParser.Parsing;
using Shirobot.Plugin.MyParser.CardRendering;
using MyParser.Provider.BiliBili.Parsing;

namespace Shirobot.Plugin.MyParser.MessageHandling;

internal sealed class UnifiedMediaMessageHandler(ProviderMessageHandlerContext context) : ProviderMessageHandlerBase(context)
{
    private readonly ProviderMediaCardRenderer _cardRenderer = new(context.BotContext, context.HostServices);
    private readonly Func<MessageEvent, string, Task>? _replyPreviewHandler = context.ReplyPreviewHandler;
    private readonly ReplyDownloadSubscriptions _audioDownloads = new();
    private readonly ReplyDownloadSubscriptions _contentReplies = new();
    private ForwardFallbackSender? _forwardSender;
    /// <summary>聊天记录的多平台兜底发送：合并转发 → 单条多媒体 → 长图 → 回复「原图」才发。</summary>
    private ForwardFallbackSender ForwardSender => _forwardSender ??= new ForwardFallbackSender(BotContext?.Message, HostServices, _contentReplies);
    private VoiceFallbackSender? _voiceSender;
    /// <summary>语音的多平台兜底发送：原音频语音 → Silk 语音 → 上传 MP3 文件。</summary>
    private VoiceFallbackSender VoiceSender => _voiceSender ??= new VoiceFallbackSender(BotContext?.Message, HostServices, Config);
    public override string ProviderId => PrimaryProvider.Id;

    protected override async Task PresentAsync(
        MessageEvent message,
        ParsedMedia media,
        bool silentProviderMismatch,
        CancellationToken cancellationToken)
    {
        using var preparationCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var videoDownload = media.Kind == ParsedMediaKind.Video && Config.IsVideoDeliveryEnabled()
                            && media.Attributes.GetValueOrDefault("preview_only") != "true"
            ? DownloadVideoAsync(media, preparationCts.Token)
            : null;
        var audioDownload = media.Kind == ParsedMediaKind.Track
            ? DownloadTrackAsync(media, preparationCts.Token)
            : media.Kind == ParsedMediaKind.Gallery && media.ProviderId == "douyin"
                ? DownloadGalleryMusicAsync(media, preparationCts.Token)
                : null;
        var galleryDownload = media.Kind == ParsedMediaKind.Gallery
            ? PrepareGalleryAsync(media, preparationCts.Token)
            : null;
        var prepareVoice = media.Kind == ParsedMediaKind.Track
            || media.ProviderId == "douyin" && media.Kind == ParsedMediaKind.Gallery
                && media.Assets.Where(asset => asset.Kind == MediaAssetKind.Image).Select((_, index) => index)
                    .Any(index => !Config.IsVideoDeliveryEnabled()
                        || string.IsNullOrWhiteSpace(media.Attributes.GetValueOrDefault($"gallery_live_photo_{index}")));
        // 只有要发 Silk 的会话（QQ 且开启 Silk 编码）才提前编码；其他平台直接发原音频。
        var recordPreparation = prepareVoice && audioDownload is not null && VoiceSender.NeedsSilk(message)
            ? Task.Run(async () => await PrepareTrackSegmentsAsync(media,
                await audioDownload.ConfigureAwait(false), preparationCts.Token).ConfigureAwait(false), preparationCts.Token)
            : null;
        try
        {
            await PresentPreparedAsync(message, media, videoDownload, audioDownload, galleryDownload, recordPreparation, preparationCts.Token).ConfigureAwait(false);
        }
        finally
        {
            await preparationCts.CancelAsync().ConfigureAwait(false);
            // Observe preparation failures even when presentation fails or is cancelled first.
            foreach (var task in new Task?[] { videoDownload, audioDownload, galleryDownload, recordPreparation })
            {
                if (task is null) continue;
                try { await task.ConfigureAwait(false); }
                catch (Exception) { /* The delivery path reports failures; preserve a presentation failure. */ }
            }
        }
    }

    private async Task PresentPreparedAsync(
        MessageEvent message,
        ParsedMedia media,
        Task<(string FileUri, string LocalPath)>? videoDownload,
        Task<string?>? audioDownload,
        Task<PreparedGalleryItem[]>? galleryDownload,
        Task<IReadOnlyList<RecordOutgoingSegment>>? recordPreparation,
        CancellationToken cancellationToken)
    {
        if (media.Kind == ParsedMediaKind.Video)
            global::Shirobot.Plugin.MyParser.DiagnosticLog.Info($"MyParser 视频投递计划: provider={media.ProviderId}, media_id={media.MediaId}, preview_only={media.Attributes.GetValueOrDefault("preview_only", "false")}, video_streams={media.Assets.Count(asset => asset.Kind == MediaAssetKind.Video)}, audio_streams={media.Assets.Count(asset => asset.Kind == MediaAssetKind.Audio)}, development_mode={Config.DevelopmentMode}, send_video_segment={Config.SendVideoSegment}");
        var singlePictureGallery = media.ProviderId == "douyin" && media.Kind == ParsedMediaKind.Gallery
            && media.Assets.Count(asset => asset.Kind == MediaAssetKind.Image) == 1
            && (!Config.IsVideoDeliveryEnabled()
                || string.IsNullOrWhiteSpace(media.Attributes.GetValueOrDefault("gallery_live_photo_0")));
        var coverEnabled = Config.IsCoverEnabled(media.ProviderId) && !singlePictureGallery;
        string? coverMessageId = null;
        // 小黑盒文章在只能走长图的平台上，长图本身就是完整文章，不再额外发封面卡片。
        if (coverEnabled && !(media.ProviderId == "heybox" && media.Kind == ParsedMediaKind.Article
                              && ForwardSender.PredictLevel(message) >= ForwardDeliveryLevel.LongImage))
        {
            coverMessageId = await SendCoverAsync(message, media, cancellationToken, audioDownload).ConfigureAwait(false);
        }

        var coverSent = coverMessageId is not null;
        using var commentsCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var hasComments = media.Content.Any(block => block.Attributes.GetValueOrDefault("category") == "comment");
        var commentsTask = hasComments ? PrepareCommentsCardAsync(media, commentsCts.Token) : null;
        try
        {
            var pageCount = int.TryParse(media.Attributes.GetValueOrDefault("pages"), out var pages)
                ? Math.Max(1, pages) : Math.Max(1, media.Kind == ParsedMediaKind.Collection ? media.Assets.Count : 1);
            if (!coverSent && media.ProviderId is ("bilibili" or "bilibili-bangumi")
                && media.Kind is ParsedMediaKind.Video or ParsedMediaKind.Collection
                && (coverEnabled || pageCount > 1))
            {
                var currentPage = int.TryParse(media.Attributes.GetValueOrDefault("requested_page"), out var page)
                    ? Math.Clamp(page, 1, pageCount) : 1;
                var text = $"{media.Title}\n{media.SourceUrl}";
                if (media.ProviderId == "bilibili-bangumi")
                    text += $"\n当前解析 第{currentPage}/{pageCount}集，10分钟内回复数字解析并发送剧集。";
                else if (pageCount > 1)
                    text += $"\n当前解析 P{currentPage}/{pageCount}，当前多P，用数字或 p 加数字回复本消息解析并发送对应 P。";
                var sent = await ReplyAsync(message, text).ConfigureAwait(false);
                RegisterMediaSelection(message, sent, media, pageCount);
            }

            var succeeded = true;
            switch (media.Kind)
            {
                case ParsedMediaKind.Video:
                    if (Config.IsVideoDeliveryEnabled() && media.Attributes.GetValueOrDefault("preview_only") != "true")
                    {
                        succeeded = await SendVideoAsync(message, media, cancellationToken, videoDownload).ConfigureAwait(false);
                    }
                    else
                    {
                        var reason = media.Attributes.GetValueOrDefault("preview_only") == "true"
                            ? "preview_only" : Config.DevelopmentMode ? "development_mode" : "send_video_segment_disabled";
                        global::Shirobot.Plugin.MyParser.DiagnosticLog.Info($"MyParser 跳过视频投递: provider={media.ProviderId}, media_id={media.MediaId}, reason={reason}");
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
                    succeeded = await SendTrackAsync(message, media, cancellationToken, preparedDownload: audioDownload, preparedRecords: recordPreparation).ConfigureAwait(false);
                    break;
                case ParsedMediaKind.Article:
                    if (media.ProviderId == "heybox")
                        await SendArticleForwardAsync(message, media, cancellationToken).ConfigureAwait(false);
                    else if (media.ProviderId != "bilibili-article" || !coverSent)
                        await SendContentAsync(message, media, cancellationToken).ConfigureAwait(false);
                    else if (media.Attributes.GetValueOrDefault("is_dynamic") == "true")
                        await SendDynamicImagesAsync(message, media, coverMessageId, cancellationToken).ConfigureAwait(false);
                    break;
                case ParsedMediaKind.Gallery:
                    await SendGalleryAsync(message, media, cancellationToken, galleryDownload, audioDownload, recordPreparation).ConfigureAwait(false);
                    break;
                case ParsedMediaKind.Collection:
                    if (media.ProviderId is not ("bilibili" or "bilibili-bangumi")) await SendCollectionAsync(message, media).ConfigureAwait(false);
                    break;
                case ParsedMediaKind.Live:
                    succeeded = await SendLiveInfoAsync(message, media, cancellationToken).ConfigureAwait(false);
                    break;
            }

            if (succeeded)
            {
                await ReactAsync(message, "426", ReactionPlatformName).ConfigureAwait(false);
                BotLog.Info($"MyParser {ReactionPlatformName} 媒体发送完成。");
            }
            if (commentsTask is not null)
            {
                var commentsCard = await commentsTask.ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(commentsCard))
                {
                    var sentCard = await SendImageAsync(message, new ImageSegment(commentsCard)).ConfigureAwait(false);
                    // 评论卡片里的图片是缩略图：回复卡片「原图」再下载原图，按聊天记录发出。
                    if (sentCard.IsSuccess && !string.IsNullOrWhiteSpace(sentCard.MessageId))
                        RegisterCommentImagesReply(message, sentCard.MessageId, media);
                }
                else
                    await SendContentAsync(message, media with
                    {
                        Content = media.Content.Where(block => block.Attributes.GetValueOrDefault("category") == "comment").ToArray(),
                    }, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            await commentsCts.CancelAsync().ConfigureAwait(false);
            if (commentsTask is not null)
                try { await commentsTask.ConfigureAwait(false); } catch (Exception) { }
        }

    }

    private void RegisterCommentImagesReply(MessageEvent message, string cardMessageId, ParsedMedia media)
    {
        var comments = media.Content.Where(block => block.Attributes.GetValueOrDefault("category") == "comment"
            && !string.IsNullOrWhiteSpace(block.Attributes.GetValueOrDefault("image_url"))).Take(30).ToArray();
        if (comments.Length == 0) return;
        ForwardSender.RegisterReplyFor(message, cardMessageId, async (reply, cancellationToken) =>
        {
            using var gate = new SemaphoreSlim(4);
            var nodes = await Task.WhenAll(comments.Select(async (block, index) =>
            {
                await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    var image = await HostServices.BuildProviderImageAsync(new ProviderImageBuildRequest(
                        media.ProviderName, block.Attributes["image_url"], media.SourceUrl, $"{media.ProviderId}_{media.MediaId}_comment_original_{index:D2}",
                        request => ApplyHeaders(request, GetImageHeaders(media)), PersistLocalFile: false), cancellationToken).ConfigureAwait(false);
                    if (string.IsNullOrWhiteSpace(image.Uri)) return null;
                    // 每条评论一个节点：「昵称：内容」+ 原图，发送者显示评论者昵称。
                    var name = string.IsNullOrWhiteSpace(block.Caption) ? media.ProviderName : block.Caption!;
                    var segments = new List<MessageSegment>();
                    if (!string.IsNullOrWhiteSpace(block.Text)) segments.Add(new TextSegment($"{name}：{block.Text!.Trim()}"));
                    segments.Add(new ImageSegment(image.Uri));
                    return new OutgoingForwardedMessage(reply.Sender.Id, name, segments);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    BotLog.Warning($"MyParser 评论原图下载失败: media_id={media.MediaId}, index={index}, error={ex.Message}");
                    return null;
                }
                finally { gate.Release(); }
            })).ConfigureAwait(false);
            var content = nodes.OfType<OutgoingForwardedMessage>().ToArray();
            return content.Length == 0 ? null : new ForwardPackage(content, "评论区图片")
            {
                Preview = content.Take(4).Select(node => node.SenderName + "：[图片]").ToArray(),
                Summary = $"共 {content.Length} 张", Prompt = $"{media.ProviderName}评论区图片",
                ProviderId = media.ProviderId, MediaId = media.MediaId + "_comments",
                Platform = media.ProviderId.StartsWith("bilibili", StringComparison.Ordinal) ? MediaCardPlatform.Bilibili
                    : media.ProviderId == "douyin" ? MediaCardPlatform.Douyin : null,
            };
        });
    }

    private async Task<string?> PrepareCommentsCardAsync(ParsedMedia media, CancellationToken cancellationToken)
    {
        if (CardRenderer is null) return null;
        var timer = Stopwatch.StartNew();
        global::Shirobot.Plugin.MyParser.DiagnosticLog.Info($"MyParser 评论卡片准备开始: provider={media.ProviderId}, media_id={media.MediaId}");
        try
        {
            return await CardRenderer.RenderAsync(media, ProviderCardPurpose.Comments, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            BotLog.Warning($"MyParser 评论卡片准备失败，回退文本: media_id={media.MediaId}, error={ex.Message}");
            return null;
        }
        finally
        {
            global::Shirobot.Plugin.MyParser.DiagnosticLog.Info($"MyParser 评论卡片准备结束: media_id={media.MediaId}, elapsed_ms={timer.ElapsedMilliseconds}");
        }
    }

    /// <summary>发送封面卡片，返回卡片消息 ID；没发出去返回 null。</summary>
    private async Task<string?> SendCoverAsync(MessageEvent message, ParsedMedia media, CancellationToken cancellationToken,
        Task<string?>? audioDownload = null)
    {
        if (string.IsNullOrWhiteSpace(media.CoverUrl)) return null;

        var purpose = media.Kind == ParsedMediaKind.Article && media.ProviderId != "heybox" ? ProviderCardPurpose.Article : ProviderCardPurpose.Main;
        var uri = await RenderCardAsync(media, purpose, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(uri)) return null;
        var sent = await SendImageAsync(message, new ImageSegment(uri)).ConfigureAwait(false);
        if (media.ProviderId is ("bilibili" or "bilibili-bangumi") && int.TryParse(media.Attributes.GetValueOrDefault("pages"), out var pageCount))
            RegisterMediaSelection(message, sent, media, pageCount);
        // 网易云卡片上提示「回复 mp3 获取 MP3 文件」：回复卡片和回复语音一样能拿到下载好的原音频。
        if (media.ProviderId == "neteasecloudmusic" && sent.IsSuccess && audioDownload is not null)
            RegisterAudioFileReply(message, sent.MessageId, media, () => audioDownload);
        return sent.IsSuccess && !string.IsNullOrWhiteSpace(sent.MessageId) ? sent.MessageId : null;
    }

    /// <summary>60 分钟内回复这条消息「mp3」（或 dl）上传下载好的原音频文件。</summary>
    private void RegisterAudioFileReply(MessageEvent message, string messageId, ParsedMedia media, Func<Task<string?>> pathProvider)
    {
        _audioDownloads.Register(BotContext.Message, message, messageId, TimeSpan.FromMinutes(60), async reply =>
        {
            string? path = null;
            try
            {
                path = await pathProvider().ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(path)) throw new FileNotFoundException("音频还没有下载完成。");
                await HostServices.UploadLocalFileAsync(Config, reply, path,
                    media.ProviderName, media.MediaId, preferBase64: Config.FileProtocol == 0).ConfigureAwait(false);
            }
            catch (Exception error)
            {
                BotLog.Warning($"MyParser 网易云原音频上传失败: media_id={media.MediaId}, error={error.Message}");
                await HostServices.ReplyTextAsync(Config, reply,
                    path is not null && File.Exists(path) ? "音频文件上传失败，请稍后重试。" : "音频缓存已过期，请重新解析歌曲后再回复 mp3。").ConfigureAwait(false);
            }
        });
    }

    private void RegisterMediaSelection(MessageEvent source, SentMessage sent, ParsedMedia media, int pageCount)
    {
        if (!sent.IsSuccess) return;
        var messages = _replyPreviewHandler is null ? null : BotContext.Message;
        if (media.ProviderId == "bilibili-bangumi")
        {
            var urls = Enumerable.Range(1, Math.Max(0, pageCount))
                .Select(index => media.Attributes.GetValueOrDefault($"episode_url_{index}", string.Empty)).ToArray();
            BilibiliMultiPageReplyCache.RegisterSelections(source, sent.MessageId, urls, messages, _replyPreviewHandler);
        }
        else BilibiliMultiPageReplyCache.Register(source, sent.MessageId, media.MediaId, pageCount, messages, _replyPreviewHandler);
    }

    public override void Dispose()
    {
        if (ProviderId is "bilibili" or "bilibili-bangumi") BilibiliMultiPageReplyCache.Clear();
        _audioDownloads.Dispose();
        _contentReplies.Dispose();
        base.Dispose();
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

    private async Task<(string FileUri, string LocalPath)> DownloadVideoAsync(ParsedMedia media, CancellationToken cancellationToken)
    {
        var videos = media.Assets.Where(asset => asset.Kind == MediaAssetKind.Video).ToArray();
        var audios = media.Assets.Where(asset => asset.Kind == MediaAssetKind.Audio).ToArray();
        if (videos.Length == 0)
            throw new InvalidOperationException("解析结果标记为视频，但没有可下载的视频流。");
        global::Shirobot.Plugin.MyParser.DiagnosticLog.Info($"MyParser 视频投递开始: provider={media.ProviderId}, media_id={media.MediaId}");
        if (PrimaryProvider is IParseProviderWithParser parserProvider
            && parserProvider.ParserObject is IVideoDownloadGate downloadGate)
        {
            downloadGate.EnsureVideoDownloadAllowed();
        }

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
            return await NetworkRetry.ExecuteAsync(token => HostServices.DownloadMuxedProviderVideoAsync(Config, request, token),
                Config.NetworkRetryCount, cancellationToken).ConfigureAwait(false);
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
            return await NetworkRetry.ExecuteAsync(token => HostServices.DownloadProviderVideoAsync(Config, request, token),
                Config.NetworkRetryCount, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<bool> SendVideoAsync(MessageEvent message, ParsedMedia media, CancellationToken cancellationToken,
        Task<(string FileUri, string LocalPath)>? preparedDownload = null)
    {
        string? localPath = null;

        try
        {
            var (fileUri, downloadedPath) = await (preparedDownload ?? DownloadVideoAsync(media, cancellationToken)).ConfigureAwait(false);
            localPath = downloadedPath;

            cancellationToken.ThrowIfCancellationRequested();
            var segment = await HostServices.BuildLocalVideoSegmentAsync(Config,
                new ProviderLocalVideoSegmentRequest(media.ProviderName, media.MediaId, localPath, fileUri, media.CoverUrl),
                cancellationToken).ConfigureAwait(false);

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
    }

    private async Task<string?> DownloadTrackAsync(ParsedMedia media, CancellationToken cancellationToken)
    {
        var audio = media.Assets.FirstOrDefault(asset => asset.Kind == MediaAssetKind.Audio);
        if (audio is null) return null;
        var request = new ProviderAudioDownloadRequest(
            media.ProviderId, media.ProviderName, media.MediaId,
            audio.CacheKey ?? $"{media.ProviderId}:{media.MediaId}", audio.Url,
            audio.DownloadDirectory, audio.FileNamePrefix ?? media.Title ?? media.MediaId,
            audio.FileExtension, CreateRequestFactory(audio), "media_id");
        var (_, localPath) = await NetworkRetry.ExecuteAsync(token => HostServices.DownloadProviderAudioAsync(Config, request, token),
            Config.NetworkRetryCount, cancellationToken).ConfigureAwait(false);
        return localPath;
    }

    /// <summary>提前编码 Silk 语音（QQ）。</summary>
    private async Task<IReadOnlyList<RecordOutgoingSegment>> PrepareTrackSegmentsAsync(
        ParsedMedia media, string? localPath, CancellationToken cancellationToken)
    {
        if (localPath is null) throw new InvalidOperationException("音频下载未生成本地文件。");
        var timer = Stopwatch.StartNew();
        global::Shirobot.Plugin.MyParser.DiagnosticLog.Info($"MyParser Silk 语音准备开始: media_id={media.MediaId}");
        var records = await VoiceSender.EncodeSilkAsync(CreateVoicePackage(media, localPath), cancellationToken).ConfigureAwait(false);
        global::Shirobot.Plugin.MyParser.DiagnosticLog.Info($"MyParser Silk 语音准备完成: media_id={media.MediaId}, elapsed_ms={timer.ElapsedMilliseconds}");
        return records;
    }

    private static VoicePackage CreateVoicePackage(ParsedMedia media, string localPath,
        Task<IReadOnlyList<RecordOutgoingSegment>>? preparedSilk = null, Action<string>? onVoiceSent = null)
    {
        var audio = media.Assets.First(asset => asset.Kind == MediaAssetKind.Audio);
        return new VoicePackage(media.ProviderId, media.ProviderName, media.MediaId, localPath,
            audio.FileNamePrefix ?? media.Title ?? media.MediaId, preparedSilk, onVoiceSent);
    }

    /// <summary>语音三级兜底：原音频直接发语音 → 需要的地方编码 Silk → 都不行上传原 MP3 文件。</summary>
    private async Task<bool> SendTrackAsync(MessageEvent message, ParsedMedia media, CancellationToken cancellationToken,
        string? existingPath = null, Task<string?>? preparedDownload = null,
        Task<IReadOnlyList<RecordOutgoingSegment>>? preparedRecords = null)
    {
        var audio = media.Assets.FirstOrDefault(asset => asset.Kind == MediaAssetKind.Audio);
        if (audio is null) return true;

        try
        {
            var localAudioPath = existingPath ?? await (preparedDownload ?? DownloadTrackAsync(media, cancellationToken)).ConfigureAwait(false)
                ?? throw new InvalidOperationException("音频下载未生成本地文件。");
            // 网易云每条语音都登记「回复 mp3 获取文件」。
            Action<string>? onVoiceSent = media.ProviderId == "neteasecloudmusic"
                ? messageId => RegisterAudioFileReply(message, messageId, media, () => Task.FromResult<string?>(localAudioPath))
                : null;
            var level = await VoiceSender.SendAsync(message, CreateVoicePackage(media, localAudioPath, preparedRecords, onVoiceSent),
                cancellationToken).ConfigureAwait(false);
            if (level == VoiceDeliveryLevel.File) await ReactAsync(message, "426", ReactionPlatformName).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            BotLog.Warning($"MyParser {media.ProviderName} 音频投递失败: media_id={media.MediaId}, error={ex.Message}");
            await ReportFailureAsync(message, ProviderFailureKind.MediaDelivery, ex, $"media_id={media.MediaId}").ConfigureAwait(false);
            await ReactAsync(message, "9", ReactionPlatformName).ConfigureAwait(false);
            return false;
        }
    }

    private async Task SendArticleForwardAsync(MessageEvent message, ParsedMedia media, CancellationToken cancellationToken)
    {
        // 只能走长图的平台用文章长图兜底，长图里画不了视频，就不提前下载正文视频了。
        var downloadVideos = ForwardSender.PredictLevel(message) < ForwardDeliveryLevel.LongImage;
        using var gate = new SemaphoreSlim(4);
        var nodes = await Task.WhenAll(media.Content.Select(async (block, index) =>
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var segments = new List<MessageSegment>();
                if (block.Kind == MediaContentKind.Image && !string.IsNullOrWhiteSpace(block.Url))
                {
                    var image = await HostServices.BuildProviderImageAsync(new ProviderImageBuildRequest(
                        media.ProviderName, block.Url, media.SourceUrl, $"{media.ProviderId}_{media.MediaId}_article_{index}",
                        request => ApplyHeaders(request, GetImageHeaders(media)), PersistLocalFile: false), cancellationToken).ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(image.Uri)) segments.Add(new ImageSegment(image.Uri));
                    else segments.Add(new TextSegment($"图片：{block.Url}"));
                    if (!string.IsNullOrWhiteSpace(block.Caption)) segments.Add(new TextSegment(block.Caption));
                }
                else if (block.Kind == MediaContentKind.Video && !string.IsNullOrWhiteSpace(block.Url))
                {
                    var video = media.Assets.FirstOrDefault(asset => asset.Kind == MediaAssetKind.Video && asset.Url == block.Url);
                    if (video is not null && Config.IsVideoDeliveryEnabled() && downloadVideos)
                    {
                        var (fileUri, path) = await DownloadVideoAsync(media with { Kind = ParsedMediaKind.Video, Assets = [video] }, cancellationToken).ConfigureAwait(false);
                        var local = await HostServices.BuildLocalVideoSegmentAsync(Config,
                            new ProviderLocalVideoSegmentRequest(media.ProviderName, $"{media.MediaId}_{index}", path, fileUri, media.CoverUrl),
                            cancellationToken).ConfigureAwait(false);
                        segments.Add(local.Segment);
                    }
                    else segments.Add(new TextSegment($"正文视频：{block.Url}"));
                }
                else if (!string.IsNullOrWhiteSpace(block.Text))
                {
                    segments.Add(new TextSegment(block.Kind == MediaContentKind.Heading ? $"【{block.Text}】" : block.Text));
                }
                return new OutgoingForwardedMessage(message.Sender.Id,
                    string.IsNullOrWhiteSpace(media.AuthorName) ? media.ProviderName : media.AuthorName, segments);
            }
            finally { gate.Release(); }
        })).ConfigureAwait(false);
        var content = nodes.Where(node => node.Segments.Count > 0).ToArray();
        if (content.Length == 0) return;
        cancellationToken.ThrowIfCancellationRequested();
        var title = string.IsNullOrWhiteSpace(media.Title) ? "小黑盒文章" : TextPreviewFormatter.TrimLine(media.Title, 48);
        var preview = media.Content.Where(block => !string.IsNullOrWhiteSpace(block.Text)).Take(3)
            .Select(block => TextPreviewFormatter.TrimLine(block.Text!, 48)).ToArray();
        // 不支持合并转发、也不能一条发多图的平台，第 3 级用文章长图（含正文排版），比通用长图好看。
        await ForwardSender.SendAsync(message, new ForwardPackage(content, title)
        {
            Preview = preview, Summary = $"共 {content.Length} 条", Prompt = "小黑盒文章",
            ProviderId = media.ProviderId, MediaId = media.MediaId,
            RenderLongImageAsync = token => CardRenderer is null
                ? Task.FromResult<string?>(null)
                : CardRenderer.RenderAsync(media, ProviderCardPurpose.Article, token),
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>B 站动态：卡片之后把全部原图按聊天记录发出（QQ 合并转发，其他平台逐级兜底；最后一级挂在卡片上回复「原图」）。</summary>
    private async Task SendDynamicImagesAsync(MessageEvent message, ParsedMedia media, string? cardMessageId, CancellationToken cancellationToken)
    {
        var urls = media.Content.Where(block => block.Kind == MediaContentKind.Image && !string.IsNullOrWhiteSpace(block.Url))
            .Select(block => block.Url!).Distinct(StringComparer.Ordinal).ToArray();
        if (urls.Length == 0) return;
        using var gate = new SemaphoreSlim(4);
        var senderName = string.IsNullOrWhiteSpace(media.AuthorName) ? media.ProviderName : media.AuthorName!;
        var nodes = await Task.WhenAll(urls.Select(async (url, index) =>
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var image = await HostServices.BuildProviderImageAsync(new ProviderImageBuildRequest(
                    media.ProviderName, url, media.SourceUrl, $"{media.ProviderId}_{media.MediaId}_original_{index:D2}",
                    request => ApplyHeaders(request, GetImageHeaders(media)), PersistLocalFile: false), cancellationToken).ConfigureAwait(false);
                return string.IsNullOrWhiteSpace(image.Uri)
                    ? null
                    : new OutgoingForwardedMessage(message.Sender.Id, senderName, [new ImageSegment(image.Uri)]);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                BotLog.Warning($"MyParser 动态原图下载失败: media_id={media.MediaId}, index={index}, error={ex.Message}");
                return null;
            }
            finally { gate.Release(); }
        })).ConfigureAwait(false);
        var content = nodes.OfType<OutgoingForwardedMessage>().ToArray();
        if (content.Length == 0) return;
        cancellationToken.ThrowIfCancellationRequested();
        await ForwardSender.SendAsync(message, new ForwardPackage(content, $"{senderName} 的动态原图")
        {
            Preview = content.Take(4).Select((_, index) => $"图片 {index + 1}").ToArray(),
            Summary = $"共 {content.Length} 张", Prompt = "哔哩哔哩动态",
            ProviderId = media.ProviderId, MediaId = media.MediaId,
            Platform = MediaCardPlatform.Bilibili,
            ReplyAnchorMessageId = cardMessageId,
        }, cancellationToken).ConfigureAwait(false);
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

    private sealed record PreparedGalleryItem(string? ImageUri, string? ImagePath, string? VideoPath);

    private async Task<string?> DownloadGalleryMusicAsync(ParsedMedia media, CancellationToken cancellationToken)
    {
        try { return await DownloadTrackAsync(media, cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            BotLog.Warning($"MyParser 抖音图文音乐预下载失败: media_id={media.MediaId}, error={ex.Message}");
            return null;
        }
    }

    private async Task<PreparedGalleryItem[]> PrepareGalleryAsync(ParsedMedia media, CancellationToken cancellationToken)
    {
        var images = media.Assets.Where(asset => asset.Kind == MediaAssetKind.Image).ToArray();
        using var slots = new SemaphoreSlim(4);
        var tasks = images.Select(async (image, index) =>
        {
            await slots.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var livePhotoUrl = media.Attributes.GetValueOrDefault($"gallery_live_photo_{index}");
                var isLivePhoto = Config.IsVideoDeliveryEnabled() && !string.IsNullOrWhiteSpace(livePhotoUrl);
                var imageTask = HostServices.BuildProviderImageAsync(new ProviderImageBuildRequest(
                    media.ProviderName, image.Url, media.SourceUrl, $"{image.FileNamePrefix ?? "image"}_{media.MediaId}_{index + 1:D2}",
                    request => ApplyHeaders(request, image.RequestHeaders), PersistLocalFile: isLivePhoto), cancellationToken);
                var videoTask = isLivePhoto
                    ? DownloadGalleryLivePhotoAsync(media, image, livePhotoUrl!, index, cancellationToken)
                    : Task.FromResult<string?>(null);
                await Task.WhenAll(imageTask, videoTask).ConfigureAwait(false);
                var result = await imageTask.ConfigureAwait(false);
                return new PreparedGalleryItem(result.Uri, result.LocalPath, await videoTask.ConfigureAwait(false));
            }
            finally { slots.Release(); }
        }).ToArray();
        return await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private async Task<string?> DownloadGalleryLivePhotoAsync(ParsedMedia media, MediaAsset image, string url, int index,
        CancellationToken cancellationToken)
    {
        try
        {
            var audio = media.Assets.FirstOrDefault(asset => asset.Kind == MediaAssetKind.Audio);
            var request = new ProviderVideoDownloadRequest("douyin", "抖音 Live Photo",
                $"{media.MediaId}_{index + 1:D2}", $"douyin-live-photo:{media.MediaId}:{index}",
                [url], image.DownloadDirectory ?? audio?.DownloadDirectory ?? string.Empty, "douyin_live_photo", "mp4",
                CreateRequestFactory(image), ProviderVideoValidationKind.Mp4, "live_photo_id");
            var (_, path) = await NetworkRetry.ExecuteAsync(token => HostServices.DownloadProviderVideoAsync(Config, request, token),
                Config.NetworkRetryCount, cancellationToken).ConfigureAwait(false);
            return path;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            BotLog.Warning($"MyParser Live Photo 下载失败，回退图片: media_id={media.MediaId}, index={index}, error={ex.Message}");
            return null;
        }
    }

    private async Task SendGalleryAsync(MessageEvent message, ParsedMedia media, CancellationToken cancellationToken,
        Task<PreparedGalleryItem[]>? preparedGallery = null, Task<string?>? preparedMusic = null,
        Task<IReadOnlyList<RecordOutgoingSegment>>? preparedRecords = null)
    {
        var images = media.Assets.Where(asset => asset.Kind == MediaAssetKind.Image).ToArray();
        var galleryMessages = new List<OutgoingForwardedMessage>();
        var senderId = message.Sender.Id;
        var senderName = string.IsNullOrWhiteSpace(media.AuthorName) ? "抖音图文" : media.AuthorName!;
        var audio = media.ProviderId == "douyin"
            ? media.Assets.FirstOrDefault(asset => asset.Kind == MediaAssetKind.Audio)
            : null;
        var galleryTask = preparedGallery ?? PrepareGalleryAsync(media, cancellationToken);
        var musicTask = preparedMusic ?? (audio is not null
            ? DownloadGalleryMusicAsync(media, cancellationToken)
            : Task.FromResult<string?>(null));
        var items = await galleryTask.ConfigureAwait(false);
        var musicPath = items.Any(item => item.VideoPath is not null) ? await musicTask.ConfigureAwait(false) : null;
        var musicMerged = false;
        var hasStaticImages = false;
        var hasUnmergedVideo = false;

        for (var index = 0; index < items.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var item = items[index];
            if (item.VideoPath is not null)
            {
                var videoPath = item.VideoPath;
                var merged = false;
                try
                {
                    videoPath = await HostServices.MuxLoopingVideoWithAudioAsync(Config, videoPath, musicPath, cancellationToken,
                        media.Attributes.GetValueOrDefault($"gallery_clip_type_{index}") == "4" ? 1 : 3).ConfigureAwait(false);
                    merged = musicPath is not null;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    BotLog.Warning($"MyParser Live Photo 循环或配乐处理失败，保留原始动态视频: media_id={media.MediaId}, index={index}, error={ex.Message}");
                }

                var thumbUri = item.ImagePath is not null ? new Uri(item.ImagePath).AbsoluteUri : item.ImageUri;
                var segment = await HostServices.BuildLocalVideoSegmentAsync(Config,
                    new ProviderLocalVideoSegmentRequest("抖音图文视频", $"{media.MediaId}_{index + 1:D2}",
                        videoPath, new Uri(videoPath).AbsoluteUri, thumbUri, "live_photo_id"), cancellationToken).ConfigureAwait(false);
                galleryMessages.Add(new OutgoingForwardedMessage(senderId, senderName, [segment.Segment]));
                musicMerged |= merged;
                hasUnmergedVideo |= !merged;
            }
            else if (!string.IsNullOrWhiteSpace(item.ImageUri))
            {
                galleryMessages.Add(new OutgoingForwardedMessage(senderId, senderName, [new ImageSegment(item.ImageUri)]));
                hasStaticImages = true;
            }
        }

        var gallerySendTimer = Stopwatch.StartNew();
        global::Shirobot.Plugin.MyParser.DiagnosticLog.Info($"MyParser 图文媒体发送开始: media_id={media.MediaId}, count={galleryMessages.Count}");
        if (galleryMessages.Count == 1)
        {
            var sent = await HostServices.SendSegmentsAsync(message, galleryMessages[0].Segments).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(sent.MessageId)) throw new InvalidOperationException("图文媒体发送未返回消息 ID。");
        }
        else if (galleryMessages.Count > 1)
        {
            var title = string.IsNullOrWhiteSpace(media.Title) ? media.ProviderName : TextPreviewFormatter.TrimLine(media.Title, 48);
            var preview = images.Take(4).Select((_, index) => $"图片 {index + 1}").ToArray();
            await ForwardSender.SendAsync(message, new ForwardPackage(galleryMessages, title)
            {
                Preview = preview, Summary = $"共 {galleryMessages.Count} 张", Prompt = media.ProviderName,
                ProviderId = media.ProviderId, MediaId = media.MediaId,
                Platform = media.ProviderId == "douyin" ? MediaCardPlatform.Douyin : null,
                // 图集是主内容：合并转发 → 单条多图 → 长图，不做回复获取。
                ReplyOnDemand = false,
            }, cancellationToken).ConfigureAwait(false);
        }

        global::Shirobot.Plugin.MyParser.DiagnosticLog.Info($"MyParser 图文媒体发送完成: media_id={media.MediaId}, elapsed_ms={gallerySendTimer.ElapsedMilliseconds}");
        if (audio is not null && (!musicMerged || hasStaticImages || hasUnmergedVideo))
            await SendTrackAsync(message, media, cancellationToken, await musicTask.ConfigureAwait(false),
                preparedRecords: preparedRecords).ConfigureAwait(false);
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

        if (!Config.IsBilibiliLiveReplayEnabled()) return true;

        string? localPath = null;

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
    }

    private int GetLiveStreamRank(ProviderLiveReplayStream stream) =>
        (Config.BilibiliBlockPcdn && global::MyParser.Provider.BiliBili.Infrastructure.BilibiliCdnPolicy.IsPcdnUrl(stream.Url) ? 1000 : 0)
        + ((stream.Protocol, stream.Format, stream.Codec) switch
    {
        ("http_hls", "fmp4", "avc") => 0,
        ("http_hls", "ts", "avc") => 1,
        ("http_stream", "flv", "avc") => 2,
        ("http_hls", "fmp4", "hevc") => 3,
        ("http_hls", "ts", "hevc") => 4,
        ("http_stream", "flv", "hevc") => 5,
        ("http_hls", "fmp4", "av1") => 6,
        _ => 99,
    });

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
