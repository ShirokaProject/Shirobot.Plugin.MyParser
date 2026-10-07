using ShiroBot.Model.QQ;
using ShiroBot.SDK.Abstractions;
using ShiroBot.SDK.Models;
using ShiroBot.SDK.Plugin;
using Shirobot.Plugin.MyParser.Parsing;

namespace Shirobot.Plugin.MyParser.Services;

internal sealed class ProviderMessageSender(IBotContext context)
{
#if SHIROBOT_SOURCE
    private static readonly HttpClient OfficialRemoteMediaHttp = new(SafeHttpTransport.CreateHandler());
#endif

    public Task<SentMessage> ReplyTextAsync(PluginConfig config, MessageEvent message, string text) =>
        config.QuoteReply ? context.Message.QuoteReplyAsync(message, text) : context.Message.ReplyAsync(message, text);

    public Task<SentMessage> SendImageAsync(MessageEvent message, ImageSegment segment) => SendSegmentsAsync(message, [segment]);

    public async Task<SentMessage> SendSegmentsAsync(MessageEvent message, IReadOnlyList<MessageSegment> segments)
    {
#if SHIROBOT_SOURCE
        if (!segments.Any(segment => segment is ResourceSegment)
            || context.GetAdapterExtension<IQOfficialMessageApi>() is not { } officialApi
            || message.Channel.Type is not (ChannelType.Direct or ChannelType.Group))
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
            var (stream, response) = await OpenResourceStreamAsync(resource, CancellationToken.None).ConfigureAwait(false);
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
#else
        return await context.Message.ReplyAsync(message, segments.ToArray()).ConfigureAwait(false);
#endif
    }

#if SHIROBOT_SOURCE
    private async Task<(Stream Stream, HttpResponseMessage? Response)> OpenResourceStreamAsync(
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
        if (Uri.TryCreate(resource.Uri, UriKind.Absolute, out uri) && uri.IsFile) path = uri.LocalPath;
        if (!Path.IsPathFullyQualified(path) || !File.Exists(path) || !IsPluginTemporaryFile(path))
            throw new NotSupportedException("QQ Official 只允许通过统一媒体接口发送插件临时目录中的本地媒体。");
        return (File.OpenRead(path), null);
    }

    private bool IsPluginTemporaryFile(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var pluginRoot = Path.GetFullPath(context.PluginDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var tempRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "Shirobot.Plugin.MyParser"))
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return fullPath.StartsWith(pluginRoot, StringComparison.OrdinalIgnoreCase)
               || fullPath.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase);
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
#endif
}
