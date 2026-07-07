using System.Net;
using Avalonia.Media.Imaging;
using ShiroBot.Model.Common;
using ShiroBot.SDK.Plugin;

namespace Shirobot.Plugin.MyParser.Parsing;

public interface IParseProviderWithParser : IParseProvider
{
    object ParserObject { get; }
}

public interface IParserHttpClientAccessor
{
    HttpClient HttpClient { get; }
}

public interface IProviderLoginStatusProvider
{
    Task<ProviderLoginStatus> CheckLoginStatusAsync(CancellationToken cancellationToken = default);
}

public interface IQrLoginProvider
{
    Task<QrLoginSession> GenerateQrLoginSessionAsync(CancellationToken cancellationToken = default);
    Task<QrLoginPollResult> PollQrLoginAsync(QrLoginSession session, CancellationToken cancellationToken = default);
}

public interface IVideoDownloadGate
{
    void EnsureVideoDownloadAllowed();
}

public interface IProviderTextNormalizer
{
    string? NormalizeParseText(string text);
}

public interface IIncomingProviderTextNormalizer : IProviderTextNormalizer
{
    string? NormalizeParseText(IncomingMessage message);
}

public interface ICookieValidator
{
    bool LooksLikeCookie(string cookie);
}

public interface IProviderCookieStore
{
    IReadOnlyList<ProviderCookieDescriptor> CookieDescriptors { get; }
}

public interface IProviderAutoParsePolicy
{
    bool IsAutoParseEnabled(PluginConfig config);
}

public interface IProviderResultMessageClassifier
{
    bool IsPluginResultMessage(string text);
}

public interface IProviderCommandContributor
{
    IReadOnlyList<ProviderCommandDescriptor> CreateCommands(ProviderCommandContext context);
}

public interface IProviderPriority
{
    int Priority { get; }
}

public sealed record ProviderCookieDescriptor(
    string ProviderId,
    string DisplayName,
    string FileName,
    Action<string> ApplyCookie,
    Func<string, bool>? ValidateCookie = null,
    bool CreateIfMissing = true,
    string? EmptyHint = null,
    string? InvalidHint = null);

public interface IMyParserProviderModule
{
    string Id { get; }
    IReadOnlyList<IParseProvider> CreateProviders(PluginConfig config);
}

[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class MyParserProviderAttribute(string id) : Attribute
{
    public string Id { get; } = id;
}

public interface IProviderMessageHandlerFactory
{
    IProviderMessageHandler? CreateMessageHandler(ProviderMessageHandlerContext context);
}

public interface IProviderMessageHandler : IDisposable
{
    string ProviderId { get; }
    Task ParseAndReplyAsync(IncomingMessage message, string text, bool silentProviderMismatch = false);
    Task HandleLoginAsync(IncomingMessage message);
}

public interface IProviderRuntimeModule
{
    IReadOnlyList<string> ProviderIds { get; }
    IReadOnlyList<string> CommandPrefixes { get; }
    void LoadRuntime(ProviderRuntimeContext context);
    void ReloadRuntime(ProviderRuntimeContext context);
    Task LogRuntimeStatusAsync(ProviderRuntimeContext context);
    IReadOnlyList<ProviderCommandDescriptor> CreateCommands(ProviderCommandContext context);
    string? GetHelpText(PluginConfig config);
    bool IsPluginResultMessage(string text);
    bool IsAutoParseEnabled(PluginConfig config);
}

public interface IProviderReplyParseTextBuilder
{
    string? TryBuildParseText(IncomingMessage message);
    bool IsDeferredParseText(string text);
}

public interface IProviderHostServices
{
    Task ReactAsync(IncomingMessage message, string faceId, string platformName);
    Task RemoveReactionAsync(IncomingMessage message, string faceId, string platformName);
    Task<SendMessageResult> ReplyTextAsync(PluginConfig config, IncomingMessage message, string text);
    Task SendImageAsync(IncomingMessage message, ImageOutgoingSegment segment);
    Task RunLoggedBackgroundAsync(string description, Func<Task> action);
    string ResolveCookiePath(string fileName);
    Task<string> UploadLocalVideoFileAsync(PluginConfig config, IncomingMessage message, string? localVideoPath, string platformName, string mediaId);
    Task<string> UploadLocalFileAsync(PluginConfig config, IncomingMessage message, string? localPath, string platformName, string mediaId, bool preferBase64 = false);
    string GetMessageScene(IncomingMessage message);
    string GetUriMode(string uri);
    string PreviewUri(string? uri, int maxLength = 180);
    void UnregisterLocalVideoFile(string? path);
    void DeleteLocalVideoIfConfigured(PluginConfig config, string? localPath, string provider);
    void CleanupStartupResidues(PluginConfig config);
    Task<ProviderImageBuildResult> BuildProviderImageAsync(
        ProviderImageBuildRequest request,
        CancellationToken cancellationToken = default);
    Task<ProviderLocalVideoSegmentResult> BuildLocalVideoSegmentAsync(
        PluginConfig config,
        ProviderLocalVideoSegmentRequest request,
        CancellationToken cancellationToken = default);
    Task<(string FileUri, string LocalPath)> DownloadProviderVideoAsync(
        PluginConfig config,
        ProviderVideoDownloadRequest request,
        CancellationToken cancellationToken = default);
    Task<(string FileUri, string LocalPath)> DownloadMuxedProviderVideoAsync(
        PluginConfig config,
        ProviderMuxedVideoDownloadRequest request,
        CancellationToken cancellationToken = default);
    Task<ProviderLiveReplayClipDownloadResult> DownloadLiveReplayClipAsync(
        PluginConfig config,
        ProviderLiveReplayClipDownloadRequest request,
        CancellationToken cancellationToken = default);
    Task<(string FileUri, string LocalPath)> DownloadProviderAudioAsync(
        PluginConfig config,
        ProviderAudioDownloadRequest request,
        CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ProviderRecordVariant>> BuildSilkRecordVariantsAsync(
        PluginConfig config,
        ProviderRecordBuildRequest request,
        CancellationToken cancellationToken = default);
    Task<string> BuildRecordUriAsync(string localPath, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<TResult>> SelectParallelOrderedAsync<TSource, TResult>(
        IEnumerable<TSource> source,
        int maxConcurrency,
        Func<TSource, Task<TResult>> selector);
    Bitmap? DecodeBase64ImageForRender(string uri);
    Bitmap? DecodeImageFileForRender(string path);
    Task<long> DownloadAsync(
        HttpRangeDownloadRequest request,
        bool logProgress,
        int intervalSeconds,
        string logPrefix,
        string identifierName,
        CancellationToken cancellationToken = default);
    Task<(long? ContentLength, bool AcceptRanges)> ProbeDownloadAsync(
        HttpRangeDownloadRequest request,
        bool logProgress,
        int intervalSeconds,
        string logPrefix,
        string identifierName,
        CancellationToken cancellationToken = default);
}

public sealed record ProviderMessageHandlerContext(
    IBotContext BotContext,
    PluginConfig Config,
    ParseProviderRegistry ProviderRegistry,
    IParseProvider PrimaryProvider,
    IProviderHostServices HostServices);

public sealed record ProviderRuntimeContext(
    IBotContext BotContext,
    PluginConfig Config,
    IProviderHostServices HostServices,
    IParseProvider? PrimaryProvider = null);

public sealed record ProviderCommandContext(
    IBotContext BotContext,
    PluginConfig Config,
    IProviderHostServices HostServices,
    IParseProvider? PrimaryProvider,
    IProviderMessageHandler? MessageHandler);

public sealed record ProviderCommandDescriptor(
    string Command,
    Func<IncomingMessage, Task> HandleAsync,
    bool AdminOnly = false);

public sealed record ProviderLoginStatus(bool IsLogin, string? UserName, string? UserId, string Message, bool NeedVerify = false);

public sealed record QrLoginSession(string Id, string Url, object? State = null);

public sealed record QrLoginPollResult(
    int Code,
    string Message,
    bool IsLogin,
    bool IsExpired = false,
    bool IsWaitingConfirmation = false,
    bool NeedVerify = false,
    string? UserName = null,
    object? State = null);

public enum ProviderVideoValidationKind
{
    None,
    Mp4,
    Mp4OrWebM,
}

public sealed record ProviderImageBuildRequest(
    string PlatformDisplayName,
    string? ImageUrl,
    string? Referer,
    string FilePrefix,
    Action<HttpRequestMessage>? ConfigureRequest = null,
    long MaxBytes = 10 * 1024L * 1024L);

public sealed record ProviderImageBuildResult(string Uri, string? LocalPath);

public sealed record ProviderLocalVideoSegmentRequest(
    string PlatformDisplayName,
    string MediaId,
    string LocalPath,
    string? FileUri,
    string? ThumbUri = null,
    string IdentifierName = "media_id");

public sealed record ProviderLocalVideoSegmentResult(
    VideoOutgoingSegment Segment,
    string UriMode,
    string VideoUri,
    long FileSize,
    bool RegisteredToHttpServer);

public sealed record ProviderVideoDownloadRequest(
    string PlatformId,
    string PlatformDisplayName,
    string MediaId,
    string CacheKey,
    IReadOnlyList<string> CandidateUrls,
    string DownloadDirectory,
    string FileNamePrefix,
    string FileExtension,
    Func<HttpMethod, string, string?, HttpRequestMessage> CreateRequest,
    ProviderVideoValidationKind ValidationKind = ProviderVideoValidationKind.Mp4,
    string IdentifierName = "media_id");

public sealed record ProviderMuxedVideoDownloadRequest(
    string PlatformId,
    string PlatformDisplayName,
    string MediaId,
    string CacheKeyPrefix,
    string? Title,
    string DownloadDirectory,
    IReadOnlyList<ProviderMuxedMediaStream> VideoStreams,
    IReadOnlyList<ProviderMuxedMediaStream> AudioStreams,
    Func<HttpMethod, string, string?, HttpRequestMessage> CreateRequest,
    string IdentifierName = "media_id");

public sealed record ProviderMuxedMediaStream(
    string StreamId,
    string Url,
    IReadOnlyList<string> BackupUrls,
    int QualityId,
    string QualityName,
    int Width,
    int Height,
    double Fps,
    string CodecName,
    bool IsAudio)
{
    public IEnumerable<string> UrlCandidates => string.IsNullOrWhiteSpace(Url) ? BackupUrls : new[] { Url }.Concat(BackupUrls);
}

public sealed record ProviderLiveReplayClipDownloadRequest(
    string PlatformId,
    string PlatformDisplayName,
    string MediaId,
    string DownloadDirectory,
    IReadOnlyList<ProviderLiveReplayStream> Streams,
    Func<HttpMethod, string, string?, HttpRequestMessage> CreatePlaylistRequest,
    Func<HttpMethod, string, string?, HttpRequestMessage> CreateSegmentRequest,
    Func<ProviderLiveReplayStream, int> StreamRank,
    string IdentifierName = "room_id");

public sealed record ProviderLiveReplayStream(
    string Protocol,
    string Format,
    string Codec,
    int CurrentQn,
    int CdnIndex,
    string Url);

public sealed record ProviderLiveReplayClipDownloadResult(
    string FileUri,
    string LocalPath,
    ProviderLiveReplayStream Stream,
    int SelectedSegments,
    int TotalSegments,
    double ActualSeconds,
    string PlaylistPath);

public sealed record ProviderAudioDownloadRequest(
    string PlatformId,
    string PlatformDisplayName,
    string MediaId,
    string CacheKey,
    string Url,
    string DownloadDirectory,
    string FileNamePrefix,
    string FileExtension,
    Func<HttpMethod, string, string?, HttpRequestMessage> CreateRequest,
    string IdentifierName = "media_id");

public sealed record ProviderRecordBuildRequest(
    string PlatformId,
    string PlatformDisplayName,
    string MediaId,
    string LocalAudioPath,
    string FileNamePrefix,
    bool IncludeMobileBest);

public sealed record ProviderRecordVariant(
    string Name,
    string DisplayName,
    string Description,
    string Path,
    int SilkRate);

public sealed record HttpRangeDownloadRequest(
    string Url,
    string Path,
    string MediaId,
    long MaxBytes,
    bool EnableParallel,
    long MinParallelBytes,
    int SegmentCount,
    Func<HttpMethod, string?, HttpRequestMessage> CreateRequest,
    Func<HttpStatusCode, Exception> CreateHttpException,
    Func<long, Exception> CreateTooLargeException,
    Func<Exception> CreateExceededLimitException,
    Func<int, HttpStatusCode, Exception> CreateRangeNotSupportedException,
    Func<int, string, Exception> CreateContentRangeMismatchException,
    Func<int, long, long, Exception> CreatePartSizeMismatchException,
    Func<long, long, Exception> CreateMergedSizeMismatchException,
    Action<Exception>? OnParallelDownloadFailed = null);
