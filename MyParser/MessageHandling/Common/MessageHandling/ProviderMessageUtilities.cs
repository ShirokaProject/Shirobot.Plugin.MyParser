using System.Collections.Concurrent;
using System.Diagnostics;
using ShiroBot.Model.QQ;
using ShiroBot.SDK.Abstractions;
using ShiroBot.SDK.Models;
using ShiroBot.SDK.Plugin;

namespace Shirobot.Plugin.MyParser.MessageHandling;

internal static class ProviderMessageUtilities
{
    private static readonly ConcurrentDictionary<string, byte> SentReactions = new(StringComparer.Ordinal);
    private static readonly HttpClient OfficialRemoteMediaHttp = new(SafeHttpTransport.CreateHandler());

    public static async Task ReactAsync(IBotContext context, IncomingMessage message, string faceId, string platformName)
    {
        if (!TryGetQqGroupMessage(context, message, out var groupApi, out var groupId, out var messageSeq))
        {
            return;
        }

        var key = $"{groupId}:{messageSeq}:{faceId}";
        if (!SentReactions.TryAdd(key, 0))
        {
            return;
        }

        try
        {
            await groupApi.SendMessageReactionAsync(groupId, messageSeq, faceId);
            if (!string.Equals(faceId, "351", StringComparison.OrdinalIgnoreCase))
            {
                await RemoveReactionAsync(context, message, "351", platformName);
            }
        }
        catch (Exception ex)
        {
            SentReactions.TryRemove(key, out _);
            if (IsAlreadyReactedError(ex))
            {
                BotLog.Info($"MyParser {platformName} 消息表情已存在，跳过重复贴表情: group_id={groupId}, message_seq={messageSeq}, face={faceId}");
                return;
            }

            BotLog.Warning($"MyParser {platformName} 消息贴表情失败: group_id={groupId}, message_seq={messageSeq}, face={faceId}, error={ex.Message}");
        }
    }

    public static async Task RemoveReactionAsync(IBotContext context, IncomingMessage message, string faceId, string platformName)
    {
        if (!TryGetQqGroupMessage(context, message, out var groupApi, out var groupId, out var messageSeq))
        {
            return;
        }

        var key = $"{groupId}:{messageSeq}:{faceId}";
        if (!SentReactions.ContainsKey(key))
        {
            return;
        }

        try
        {
            await groupApi.SendMessageReactionAsync(groupId, messageSeq, faceId, isAdd: false);
            SentReactions.TryRemove(key, out _);
        }
        catch (Exception ex)
        {
            BotLog.Warning($"MyParser {platformName} 消息取消表情失败: group_id={groupId}, message_seq={messageSeq}, face={faceId}, error={ex.Message}");
        }
    }

    public static void ClearReactionCache() => SentReactions.Clear();

    public static Task<SendMessageResult> ReplyTextAsync(IBotContext context, PluginConfig config, IncomingMessage message, string text) =>
        config.QuoteReply ? context.Message.QuoteReplyAsync(message, text) : context.Message.ReplyAsync(message, text);

    public static async Task ReportFailureAsync(
        IBotContext context,
        PluginConfig config,
        IncomingMessage message,
        string providerName,
        ProviderFailureKind kind,
        Exception? exception,
        string? diagnosticContext)
    {
        var contextText = string.IsNullOrWhiteSpace(diagnosticContext) ? string.Empty : $", context={diagnosticContext}";
        if (exception is null)
            BotLog.Warning($"MyParser provider failure: provider={providerName}, kind={kind}{contextText}");
        else
            BotLog.Error($"MyParser provider failure: provider={providerName}, kind={kind}{contextText}, exception={exception}");

        if (!config.SendProviderFailureMessages) return;
        await ReplyTextAsync(context, config, message, BuildProviderFailureMessage(config, providerName, kind))
            .ConfigureAwait(false);
    }

    private static string BuildProviderFailureMessage(PluginConfig config, string providerName, ProviderFailureKind kind) => kind switch
    {
        ProviderFailureKind.Parse => $"{providerName}解析失败，请稍后重试。",
        ProviderFailureKind.Timeout => $"{providerName}请求超时，请稍后重试。",
        ProviderFailureKind.AuthenticationRequired => $"{providerName}需要有效登录态或 Cookie，请检查插件配置。",
        ProviderFailureKind.UnsupportedContent when string.Equals(providerName, "抖音", StringComparison.OrdinalIgnoreCase)
            => "暂不支持该抖音作品类型，目前支持公开的视频和图集。",
        ProviderFailureKind.UnsupportedContent => $"{providerName}暂不支持此内容类型。",
        ProviderFailureKind.MediaDelivery => $"{providerName}内容已解析，但媒体发送未完成。",
        _ => (string.IsNullOrWhiteSpace(config.ProviderFailureMessageTemplate)
                ? "{provider}处理失败，请稍后重试。"
                : config.ProviderFailureMessageTemplate)
            .Replace("{provider}", providerName, StringComparison.OrdinalIgnoreCase),
    };

    public static Task<SentMessage> SendImageAsync(IBotContext context, IncomingMessage message, ImageOutgoingSegment segment) =>
        SendSegmentsAsync(context, message, [segment]);

    public static async Task<SentMessage> SendSegmentsAsync(
        IBotContext context,
        IncomingMessage message,
        IReadOnlyList<MessageSegment> segments)
    {
        if (!segments.Any(segment => segment is ResourceSegment)
            || context.GetAdapterExtension<IQOfficialMessageApi>() is not { } officialApi
            || message.Channel.Type is not (ChannelType.Direct or ChannelType.Group)
            )
        {
            return await context.Message.ReplyAsync(message, segments.ToArray()).ConfigureAwait(false);
        }

        if (segments.Any(segment => segment is not (ResourceSegment or TextSegment or QuoteSegment
                or MentionSegment or MentionAllSegment or EmojiSegment)))
        {
            return await context.Message.ReplyAsync(message, segments.ToArray()).ConfigureAwait(false);
        }

        var target = new QOfficialMessageTarget(
            message.Channel.Type == ChannelType.Direct ? QOfficialMessageScene.Direct : QOfficialMessageScene.Group,
            message.Channel.Id);
        var replyMessageId = segments.OfType<QuoteSegment>().FirstOrDefault()?.MessageId ?? message.MessageId;
        var caption = string.Concat(segments.Select(segment => segment switch
        {
            TextSegment text => text.Text,
            MentionSegment mention => "@" + (mention.DisplayName ?? "用户"),
            MentionAllSegment => "@全体成员",
            EmojiSegment emoji => emoji.Name ?? $":{emoji.Id}:",
            _ => string.Empty,
        }));

        string? lastMessageId = null;
        var resourceIndex = 0;
        foreach (var resource in segments.OfType<ResourceSegment>())
        {
            var mediaType = GetOfficialMediaType(resource);
            var fileName = GetResourceFileName(resource);
            var (stream, response) = await OpenResourceStreamAsync(context, resource, CancellationToken.None).ConfigureAwait(false);
            try
            {
                var officialMessage = QOfficialMessage.Media(
                    mediaType,
                    stream,
                    fileName,
                    resourceIndex == 0 && !string.IsNullOrWhiteSpace(caption) ? caption : null);
                lastMessageId = await officialApi.SendAsync(
                    target,
                    officialMessage,
                    new QOfficialMessageReply { MessageId = replyMessageId }).ConfigureAwait(false);
            }
            finally
            {
                await stream.DisposeAsync().ConfigureAwait(false);
                response?.Dispose();
            }

            resourceIndex++;
        }

        return new SentMessage(lastMessageId ?? string.Empty);
    }

    private static async Task<(Stream Stream, HttpResponseMessage? Response)> OpenResourceStreamAsync(
        IBotContext context,
        ResourceSegment resource,
        CancellationToken cancellationToken)
    {
        const string base64Prefix = "base64://";
        if (resource.Uri.StartsWith(base64Prefix, StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var bytes = Convert.FromBase64String(resource.Uri[base64Prefix.Length..]);
                return (new MemoryStream(bytes, writable: false), null);
            }
            catch (FormatException ex)
            {
                throw new InvalidDataException("消息媒体中的 base64 数据无效。", ex);
            }
        }

        if (Uri.TryCreate(resource.Uri, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            var response = await OfficialRemoteMediaHttp.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                response.Dispose();
                throw new HttpRequestException($"媒体下载失败：HTTP {(int)response.StatusCode}");
            }

            return (await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false), response);
        }

        var path = resource.Uri;
        if (Uri.TryCreate(resource.Uri, UriKind.Absolute, out uri) && uri.IsFile)
            path = uri.LocalPath;
        if (!Path.IsPathFullyQualified(path) || !File.Exists(path) || !IsPluginTemporaryFile(context, path))
            throw new NotSupportedException("QQ Official 只允许通过统一媒体接口发送插件临时目录中的本地媒体。");
        return (File.OpenRead(path), null);
    }

    private static QOfficialMediaType GetOfficialMediaType(ResourceSegment resource) => resource switch
    {
        ImageSegment => QOfficialMediaType.Image,
        VideoSegment => QOfficialMediaType.Video,
        AudioSegment => QOfficialMediaType.Audio,
        FileSegment => QOfficialMediaType.File,
        _ => throw new NotSupportedException($"QQ Official 不支持发送 {resource.GetType().Name}。"),
    };

    private static string GetResourceFileName(ResourceSegment resource)
    {
        if (!string.IsNullOrWhiteSpace(resource.FileName)) return Path.GetFileName(resource.FileName);
        return resource switch
        {
            ImageSegment => "image.png",
            VideoSegment => "video.mp4",
            AudioSegment => "audio.silk",
            _ => "file.bin",
        };
    }

    private static bool IsPluginTemporaryFile(IBotContext context, string path)
    {
        var fullPath = Path.GetFullPath(path);
        var pluginRoot = Path.GetFullPath(context.PluginDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        var tempRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "Shirobot.Plugin.MyParser"))
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        return fullPath.StartsWith(pluginRoot, StringComparison.OrdinalIgnoreCase)
               || fullPath.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase);
    }

    public static Task RunLoggedBackgroundAsync(string description, Func<Task> action)
    {
        return Task.Run(async () =>
        {
            try
            {
                await action();
            }
            catch (Exception ex)
            {
                BotLog.Warning($"MyParser {description} 未完成: {ex.Message}");
            }
        });
    }

    public static string ResolveCookiePath(IBotContext context, string fileName)
    {
        var cookieDirectory = Path.Combine(context.PluginDirectory, "cookies");
        Directory.CreateDirectory(cookieDirectory);
        return Path.Combine(cookieDirectory, Path.GetFileName(fileName));
    }

    public static Task<string> UploadLocalVideoFileAsync(
        IBotContext context,
        PluginConfig config,
        IncomingMessage message,
        string? localVideoPath,
        string platformName,
        string mediaId) =>
        UploadLocalFileAsync(context, config, message, localVideoPath, platformName, mediaId);

    public static async Task<string> UploadLocalFileAsync(
        IBotContext context,
        PluginConfig config,
        IncomingMessage message,
        string? localFilePath,
        string platformName,
        string mediaId,
        bool preferBase64 = false)
    {
        if (string.IsNullOrWhiteSpace(localFilePath) || !File.Exists(localFilePath))
        {
            throw new InvalidOperationException("本地文件不存在。");
        }

        var fileApi = context.GetAdapterExtension<IQFileApi>()
                      ?? throw new NotSupportedException("当前适配器不支持 QQ 文件上传扩展。");
        if (!long.TryParse(message.Channel.Id, out var peerId))
        {
            throw new NotSupportedException("当前渠道 ID 不是 QQ 数字 ID，无法上传文件。");
        }

        var localPath = Path.GetFullPath(localFilePath);
        var fileSize = new FileInfo(localPath).Length;
        var uploadMode = preferBase64 ? "base64" : "file";
        var fileUri = preferBase64
            ? "base64://" + Convert.ToBase64String(await File.ReadAllBytesAsync(localPath))
            : new Uri(localPath).AbsoluteUri;
        var fileName = Path.GetFileName(localPath);
        var stopwatch = Stopwatch.StartNew();

        BotLog.Info($"MyParser {platformName} 文件上传开始: media_id={mediaId}, mode={uploadMode}, file_mb={fileSize / 1024d / 1024d:F2}, file={localPath}");

        var fileId = message.Channel.Type switch
        {
            ChannelType.Group => await fileApi.UploadGroupFileAsync(peerId, fileUri, fileName),
            ChannelType.Direct => await fileApi.UploadPrivateFileAsync(peerId, fileUri, fileName),
            _ => throw new NotSupportedException("当前消息类型不支持文件上传。"),
        };
        var scene = message.Channel.Type == ChannelType.Group ? "group" : "friend";
        EnsureFileUploadAccepted(fileId, scene, uploadMode);
        return $"{scene} FileId={fileId} Mode={uploadMode} elapsed={stopwatch.Elapsed:mm\\:ss}";
    }

    private static void EnsureFileUploadAccepted(string? fileId, string scene, string uploadMode)
    {
        if (string.IsNullOrWhiteSpace(fileId))
        {
            BotLog.Warning($"MyParser 文件上传返回空 FileId，当前 ShiroBot/适配器可能不返回有效 FileId；不再按失败处理。scene={scene}, mode={uploadMode}");
        }
    }

    public static string GetMessageScene(IncomingMessage message) => message.Channel.Type switch
    {
        ChannelType.Group => "group",
        ChannelType.Direct => "friend",
        ChannelType.Thread => "thread",
        ChannelType.Other => "other",
        _ => "unknown",
    };

    private static bool TryGetQqGroupMessage(
        IBotContext context,
        IncomingMessage message,
        out IQGroupApi groupApi,
        out long groupId,
        out long messageSeq)
    {
        groupApi = null!;
        groupId = 0;
        messageSeq = 0;
        if (message.Channel.Type != ChannelType.Group
            || !long.TryParse(message.Channel.Id, out groupId)
            || !long.TryParse(message.MessageId, out messageSeq))
        {
            return false;
        }

        groupApi = context.GetAdapterExtension<IQGroupApi>()!;
        return groupApi is not null;
    }

    private static bool IsAlreadyReactedError(Exception ex)
    {
        for (var current = ex; current is not null; current = current.InnerException)
        {
            if (current.Message.Contains("已经设置过", StringComparison.OrdinalIgnoreCase)
                || current.Message.Contains("already", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
