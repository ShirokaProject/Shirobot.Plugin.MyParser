using Avalonia.Media.Imaging;
using Shirobot.Plugin.MyParser.MessageHandling;
using Shirobot.Plugin.MyParser.Parsing;
using Shirobot.Plugin.MyParser.Services;
using Shirobot.Plugin.MyParser.Downloading;
using Shirobot.Plugin.MyParser.Media;
using Shirobot.Plugin.MyParser.CardRendering;
using ShiroBot.SDK.Models;
using ShiroBot.SDK.Abstractions;
using ShiroBot.SDK.Plugin;

namespace Shirobot.Plugin.MyParser;

internal sealed class ProviderHostServices : IProviderHostServices, IDisposable
{
    private readonly IBotContext context;
    private readonly PluginConfig pluginConfig;
    private readonly ProviderMessageSender _messageSender;
    private readonly ProviderReactionService _reactionService;
    private readonly ProviderFailureReporter _failureReporter;
    private readonly ProviderLocalFileService _localFiles;
    private LocalVideoHttpServer? _localVideoHttpServer;

    public ProviderHostServices(IBotContext context, PluginConfig pluginConfig)
    {
        this.context = context;
        this.pluginConfig = pluginConfig;
        _messageSender = new ProviderMessageSender(context);
        _reactionService = new ProviderReactionService(context);
        _failureReporter = new ProviderFailureReporter(_messageSender);
        _localFiles = new ProviderLocalFileService(context);
    }

    public Task ReactAsync(IncomingMessage message, string faceId, string platformName)
    {
        return _reactionService.AddAsync(message, faceId, platformName);
    }

    public Task RemoveReactionAsync(IncomingMessage message, string faceId, string platformName)
    {
        return _reactionService.RemoveAsync(message, faceId, platformName);
    }

    public Task<SendMessageResult> ReplyTextAsync(PluginConfig config, IncomingMessage message, string text)
    {
        return _messageSender.ReplyTextAsync(config, message, text);
    }

    public Task ReportFailureAsync(PluginConfig config, IncomingMessage message, string providerName,
        ProviderFailureKind kind, Exception? exception = null, string? diagnosticContext = null)
    {
        return _failureReporter.ReportAsync(config, message, providerName, kind, exception, diagnosticContext);
    }

    public Task<SendMessageResult> SendImageAsync(IncomingMessage message, ImageOutgoingSegment segment)
    {
        return _messageSender.SendImageAsync(message, segment);
    }

    public Task<SendMessageResult> SendSegmentsAsync(IncomingMessage message, IReadOnlyList<OutgoingSegment> segments)
    {
        return _messageSender.SendSegmentsAsync(message, segments);
    }

    public Task RunLoggedBackgroundAsync(string description, Func<Task> action)
    {
        return _localFiles.RunLoggedBackgroundAsync(description, action);
    }

    public string ResolveCookiePath(string fileName)
    {
        return _localFiles.ResolveCookiePath(fileName);
    }

    public Task<string> UploadLocalVideoFileAsync(PluginConfig config, IncomingMessage message, string? localVideoPath, string platformName, string mediaId)
    {
        return _localFiles.UploadLocalVideoFileAsync(config, message, localVideoPath, platformName, mediaId);
    }

    public Task<string> UploadLocalFileAsync(PluginConfig config, IncomingMessage message, string? localPath, string platformName, string mediaId, bool preferBase64 = false)
    {
        return _localFiles.UploadLocalFileAsync(config, message, localPath, platformName, mediaId, preferBase64);
    }

    public string GetMessageScene(IncomingMessage message) => _localFiles.GetMessageScene(message);

    public string GetUriMode(string uri) => MediaUriFormatter.GetUriMode(uri);

    public string PreviewUri(string? uri, int maxLength = 180) => MediaUriFormatter.PreviewUri(uri, maxLength);

    public void UnregisterLocalVideoFile(string? path) => _localVideoHttpServer?.UnregisterFile(path);

    public void DeleteLocalVideoIfConfigured(PluginConfig config, string? localPath, string provider)
    {
        TemporaryMediaCleanupService.DeleteLocalVideoIfConfigured(config, localPath, provider);
    }

    public void CleanupStartupResidues(PluginConfig config)
    {
        TemporaryMediaCleanupService.CleanupStartupResidues(config);
    }

    public async Task<ProviderImageBuildResult> BuildProviderImageAsync(
        ProviderImageBuildRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request.FilePrefix.Contains("cover", StringComparison.OrdinalIgnoreCase)
            && !pluginConfig.IsCoverEnabled(request.PlatformDisplayName))
            return new ProviderImageBuildResult(string.Empty, null);
        var (uri, localPath) = await RemoteImageFetchService.BuildRemoteImageAsync(
            request.PlatformDisplayName,
            request.ImageUrl,
            request.Referer,
            request.FilePrefix,
            request.ConfigureRequest,
            request.MaxBytes,
            request.PersistLocalFile,
            pluginConfig.HttpProxy,
            cancellationToken).ConfigureAwait(false);
        return new ProviderImageBuildResult(uri, localPath);
    }

    public async Task<ProviderLocalVideoSegmentResult> BuildLocalVideoSegmentAsync(
        PluginConfig config,
        ProviderLocalVideoSegmentRequest request,
        CancellationToken cancellationToken = default)
    {
        var fileSize = new FileInfo(request.LocalPath).Length;
        string videoUri;
        string uriMode;
        var registeredToHttpServer = false;
        if (config.FileProtocol == 0)
        {
            var bytes = await File.ReadAllBytesAsync(request.LocalPath, cancellationToken).ConfigureAwait(false);
            videoUri = "base64://" + Convert.ToBase64String(bytes);
            uriMode = "base64";
        }
        else if (config.FileProtocol == 2)
        {
            videoUri = GetLocalVideoHttpServer().RegisterFile(request.LocalPath);
            registeredToHttpServer = true;
            uriMode = "http";
        }
        else if (config.FileProtocol == 1)
        {
            videoUri = string.IsNullOrWhiteSpace(request.FileUri) ? new Uri(request.LocalPath).AbsoluteUri : request.FileUri;
            uriMode = "file";
        }
        else
        {
            throw new InvalidOperationException("FileProtocol 必须为 0（Base64）、1（File）或 2（Http）。");
        }

        BotLog.Info($"MyParser {request.PlatformDisplayName} VideoSegment URI 模式：{uriMode}, {request.IdentifierName}={request.MediaId}, file_mb={fileSize / 1024d / 1024d:F2}, uri_preview={PreviewUri(videoUri)}");
        var segment = new VideoOutgoingSegment(videoUri)
        {
            ThumbnailUri = config.IsCoverEnabled(request.PlatformDisplayName) && !string.IsNullOrWhiteSpace(request.ThumbUri) ? request.ThumbUri : null,
        };
        return new ProviderLocalVideoSegmentResult(segment, uriMode, videoUri, fileSize, registeredToHttpServer);
    }

    private readonly ProviderDownloadService _downloadService = new();

    public Task<(string FileUri, string LocalPath)> DownloadProviderVideoAsync(
        PluginConfig config,
        ProviderVideoDownloadRequest request,
        CancellationToken cancellationToken = default)
    {
        return _downloadService.DownloadProviderVideoAsync(config, request, cancellationToken);
    }

    public Task<ProviderLiveReplayClipDownloadResult> DownloadLiveReplayClipAsync(
        PluginConfig config,
        ProviderLiveReplayClipDownloadRequest request,
        CancellationToken cancellationToken = default)
    {
        return _downloadService.DownloadLiveReplayClipAsync(config, request, cancellationToken);
    }

    public Task<(string FileUri, string LocalPath)> DownloadMuxedProviderVideoAsync(
        PluginConfig config,
        ProviderMuxedVideoDownloadRequest request,
        CancellationToken cancellationToken = default)
    {
        return _downloadService.DownloadMuxedProviderVideoAsync(config, request, cancellationToken);
    }

    public Task<(string FileUri, string LocalPath)> DownloadProviderAudioAsync(
        PluginConfig config,
        ProviderAudioDownloadRequest request,
        CancellationToken cancellationToken = default)
    {
        return _downloadService.DownloadProviderAudioAsync(config, request, cancellationToken);
    }

    public Task<string> MuxLoopingVideoWithAudioAsync(
        PluginConfig config,
        string videoPath,
        string audioPath,
        CancellationToken cancellationToken = default)
    {
        return _downloadService.MuxLoopingVideoWithAudioAsync(config, videoPath, audioPath, cancellationToken);
    }

    public Task<string> CreateStillImageVideoWithAudioAsync(
        PluginConfig config,
        string imagePath,
        string audioPath,
        CancellationToken cancellationToken = default)
    {
        return _downloadService.CreateStillImageVideoWithAudioAsync(config, imagePath, audioPath, cancellationToken);
    }

    public Task<IReadOnlyList<ProviderRecordVariant>> BuildSilkRecordVariantsAsync(
        PluginConfig config,
        ProviderRecordBuildRequest request,
        CancellationToken cancellationToken = default)
    {
        return _downloadService.BuildSilkRecordVariantsAsync(config, request, cancellationToken);
    }

    public Task<string> BuildRecordUriAsync(string localPath, CancellationToken cancellationToken = default)
    {
        return ProviderDownloadService.BuildRecordUriAsync(localPath, cancellationToken);
    }

    public Task<IReadOnlyList<TResult>> SelectParallelOrderedAsync<TSource, TResult>(
        IEnumerable<TSource> source,
        int maxConcurrency,
        Func<TSource, Task<TResult>> selector)
    {
        return MessageFetchConcurrency.SelectParallelOrderedAsync(source, maxConcurrency, selector);
    }

    public Bitmap? DecodeBase64ImageForRender(string uri) => MediaBitmapDecoder.DecodeBase64ImageForRender(uri);

    public Bitmap? DecodeImageFileForRender(string path) => MediaBitmapDecoder.DecodeImageFileForRender(path);

    public Task<long> DownloadAsync(
        HttpRangeDownloadRequest request,
        bool logProgress,
        int intervalSeconds,
        string logPrefix,
        string identifierName,
        CancellationToken cancellationToken = default)
    {
        return _downloadService.DownloadAsync(request, logProgress, intervalSeconds, logPrefix, identifierName, cancellationToken);
    }

    public Task<(long? ContentLength, bool AcceptRanges)> ProbeDownloadAsync(
        HttpRangeDownloadRequest request,
        bool logProgress,
        int intervalSeconds,
        string logPrefix,
        string identifierName,
        CancellationToken cancellationToken = default)
    {
        return _downloadService.ProbeDownloadAsync(request, logProgress, intervalSeconds, logPrefix, identifierName, cancellationToken);
    }

    private LocalVideoHttpServer GetLocalVideoHttpServer()
    {
        return _localVideoHttpServer ??= new LocalVideoHttpServer();
    }

    public void Dispose()
    {
        _localVideoHttpServer?.Dispose();
        _localVideoHttpServer = null;
    }
}
