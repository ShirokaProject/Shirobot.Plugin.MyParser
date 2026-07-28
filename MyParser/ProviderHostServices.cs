using Avalonia.Media.Imaging;
using Shirobot.Plugin.MyParser.MessageHandling;
using Shirobot.Plugin.MyParser.Parsing;
using Shirobot.Plugin.MyParser.Services;
using Shirobot.Plugin.MyParser.Utility;
using ShiroBot.SDK.Models;
using ShiroBot.SDK.Abstractions;
using ShiroBot.SDK.Plugin;

namespace Shirobot.Plugin.MyParser;

internal sealed class ProviderHostServices(IBotContext context) : IProviderHostServices, IDisposable
{
    private LocalVideoHttpServer? _localVideoHttpServer;

    public Task ReactAsync(IncomingMessage message, string faceId, string platformName)
    {
        return ProviderMessageUtilities.ReactAsync(context, message, faceId, platformName);
    }

    public Task RemoveReactionAsync(IncomingMessage message, string faceId, string platformName)
    {
        return ProviderMessageUtilities.RemoveReactionAsync(context, message, faceId, platformName);
    }

    public Task<SendMessageResult> ReplyTextAsync(PluginConfig config, IncomingMessage message, string text)
    {
        return ProviderMessageUtilities.ReplyTextAsync(context, config, message, text);
    }

    public Task SendImageAsync(IncomingMessage message, ImageOutgoingSegment segment)
    {
        return ProviderMessageUtilities.SendImageAsync(context, message, segment);
    }

    public Task RunLoggedBackgroundAsync(string description, Func<Task> action)
    {
        return ProviderMessageUtilities.RunLoggedBackgroundAsync(description, action);
    }

    public string ResolveCookiePath(string fileName)
    {
        return ProviderMessageUtilities.ResolveCookiePath(context, fileName);
    }

    public Task<string> UploadLocalVideoFileAsync(PluginConfig config, IncomingMessage message, string? localVideoPath, string platformName, string mediaId)
    {
        return ProviderMessageUtilities.UploadLocalVideoFileAsync(context, config, message, localVideoPath, platformName, mediaId);
    }

    public Task<string> UploadLocalFileAsync(PluginConfig config, IncomingMessage message, string? localPath, string platformName, string mediaId, bool preferBase64 = false)
    {
        return ProviderMessageUtilities.UploadLocalFileAsync(context, config, message, localPath, platformName, mediaId, preferBase64);
    }

    public string GetMessageScene(IncomingMessage message) => ProviderMessageUtilities.GetMessageScene(message);

    public string GetUriMode(string uri) => MediaUriUtilities.GetUriMode(uri);

    public string PreviewUri(string? uri, int maxLength = 180) => MediaUriUtilities.PreviewUri(uri, maxLength);

    public void UnregisterLocalVideoFile(string? path) => _localVideoHttpServer?.UnregisterFile(path);

    public void DeleteLocalVideoIfConfigured(PluginConfig config, string? localPath, string provider)
    {
        LocalMediaCleanup.DeleteLocalVideoIfConfigured(config, localPath, provider);
    }

    public void CleanupStartupResidues(PluginConfig config)
    {
        LocalMediaCleanup.CleanupStartupResidues(config);
    }

    public async Task<ProviderImageBuildResult> BuildProviderImageAsync(
        ProviderImageBuildRequest request,
        CancellationToken cancellationToken = default)
    {
        var (uri, localPath) = await RemoteImageFetchService.BuildRemoteImageAsync(
            request.PlatformDisplayName,
            request.ImageUrl,
            request.Referer,
            request.FilePrefix,
            request.ConfigureRequest,
            request.MaxBytes,
            request.PersistLocalFile,
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
        if (config.FileProtocol == VideoSegmentFileProtocol.Base64)
        {
            var bytes = await File.ReadAllBytesAsync(request.LocalPath, cancellationToken).ConfigureAwait(false);
            videoUri = "base64://" + Convert.ToBase64String(bytes);
            uriMode = "base64";
        }
        else if (config.FileProtocol == VideoSegmentFileProtocol.Http)
        {
            videoUri = GetLocalVideoHttpServer().RegisterFile(request.LocalPath);
            registeredToHttpServer = true;
            uriMode = "http";
        }
        else
        {
            videoUri = string.IsNullOrWhiteSpace(request.FileUri) ? new Uri(request.LocalPath).AbsoluteUri : request.FileUri;
            uriMode = "file";
        }

        BotLog.Info($"MyParser {request.PlatformDisplayName} VideoSegment URI 模式：{uriMode}, {request.IdentifierName}={request.MediaId}, file_mb={fileSize / 1024d / 1024d:F2}, uri_preview={PreviewUri(videoUri)}");
        var segment = new VideoOutgoingSegment(videoUri)
        {
            ThumbnailUri = string.IsNullOrWhiteSpace(request.ThumbUri) ? null : request.ThumbUri,
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

    public Bitmap? DecodeBase64ImageForRender(string uri) => RenderBitmapUtilities.DecodeBase64ImageForRender(uri);

    public Bitmap? DecodeImageFileForRender(string path) => RenderBitmapUtilities.DecodeImageFileForRender(path);

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
