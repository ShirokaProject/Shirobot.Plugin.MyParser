using System.Diagnostics;
using System.Collections.Concurrent;
using ShiroBot.Qq.Model;
using ShiroBot.SDK.Abstractions;
using ShiroBot.SDK.Models;
using ShiroBot.SDK.Plugin;

namespace Shirobot.Plugin.MyParser.Providers.Common.MessageHandling;

internal static class MessageHandlerCommon
{
    private static readonly ConcurrentDictionary<string, byte> SentReactions = new(StringComparer.Ordinal);

    public static async Task ReactAsync(IBotContext context, MessageEvent message, string faceId, string platformName)
    {
        if (message.IsDirect || !TryGetQqGroupReactionTarget(context, message, out var groupApi, out var groupId, out var messageSeq))
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
            if (!string.Equals(faceId, "351", StringComparison.OrdinalIgnoreCase))
            {
                await RemoveReactionAsync(context, message, "351", platformName);
            }

            await groupApi.SendMessageReactionAsync(groupId, messageSeq, faceId);
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

    public static async Task RemoveReactionAsync(IBotContext context, MessageEvent message, string faceId, string platformName)
    {
        if (message.IsDirect || !TryGetQqGroupReactionTarget(context, message, out var groupApi, out var groupId, out var messageSeq))
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

    private static bool TryGetQqGroupReactionTarget(
        IBotContext context,
        MessageEvent message,
        out IQqGroupApi groupApi,
        out long groupId,
        out long messageSeq)
    {
        groupApi = null!;
        groupId = 0;
        messageSeq = 0;
        var api = context.GetAdapterExtension<IQqGroupApi>();
        if (api is null
            || !long.TryParse(message.Channel.Id, out groupId)
            || !long.TryParse(message.MessageId, out messageSeq))
        {
            return false;
        }

        groupApi = api;
        return true;
    }

    public static void ClearReactionCache()
    {
        SentReactions.Clear();
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

    public static Task<SentMessage> ReplyTextAsync(IBotContext context, PluginConfig config, MessageEvent message, string text)
    {
        return config.QuoteReply ? context.Message.QuoteReplyAsync(message, text) : context.Message.ReplyAsync(message, text);
    }

    public static async Task SendImageAsync(IBotContext context, MessageEvent message, ImageSegment segment)
    {
        await context.Message.ReplyAsync(message, segment);
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
        return Path.Combine(context.PluginDirectory, fileName);
    }

    public static async Task<string> UploadLocalVideoFileAsync(
        IBotContext context,
        PluginConfig config,
        MessageEvent message,
        string? localVideoPath,
        string platformName,
        string mediaId)
    {
        if (string.IsNullOrWhiteSpace(localVideoPath) || !File.Exists(localVideoPath))
        {
            throw new InvalidOperationException("本地视频文件不存在。");
        }

        var localPath = Path.GetFullPath(localVideoPath);
        var fileSize = new FileInfo(localPath).Length;
        var fileUri = new Uri(localPath).AbsoluteUri;
        var fileName = Path.GetFileName(localPath);
        const string uploadMode = "file";
        var stopwatch = Stopwatch.StartNew();

        BotLog.Info($"MyParser {platformName} 文件上传开始: media_id={mediaId}, mode={uploadMode}, file_mb={fileSize / 1024d / 1024d:F2}, file={localPath}");

        var fileApi = context.GetAdapterExtension<IQqFileApi>();
        if (fileApi is null || !long.TryParse(message.Channel.Id, out var peerId))
        {
            throw new NotSupportedException("当前平台/消息类型不支持文件上传。");
        }

        if (message.IsDirect)
        {
            var fileId = await fileApi.UploadPrivateFileAsync(peerId, fileUri, fileName);
            EnsureFileUploadAccepted(fileId, "friend", uploadMode);
            return $"私聊文件 FileId={fileId} Mode={uploadMode} elapsed={stopwatch.Elapsed:mm\\:ss}";
        }

        if (message.Channel.Type == ChannelType.Group)
        {
            var fileId = await fileApi.UploadGroupFileAsync(peerId, fileUri, fileName);
            EnsureFileUploadAccepted(fileId, "group", uploadMode);
            return $"群文件 FileId={fileId} Mode={uploadMode} elapsed={stopwatch.Elapsed:mm\\:ss}";
        }

        throw new NotSupportedException("当前消息类型不支持文件上传。");
    }

    private static void EnsureFileUploadAccepted(string? fileId, string scene, string uploadMode)
    {
        if (string.IsNullOrWhiteSpace(fileId))
        {
            throw new InvalidOperationException($"文件上传返回空 FileId，发送未确认。scene={scene}, mode={uploadMode}");
        }
    }

    /// <summary>把 QQ 合并转发内容包装为可经通用发送接口透传的 RawSegment。</summary>
    public static RawSegment BuildForwardSegment(
        IReadOnlyList<QqForwardedMessage> messages,
        string? title,
        IReadOnlyList<string>? preview,
        string? summary,
        string? prompt)
    {
        return new RawSegment("qq", "forward", new QqForwardOutgoing(messages)
        {
            Title = title,
            Preview = preview,
            Summary = summary,
            Prompt = prompt,
        });
    }

    public static long GetBotOrSenderId(MessageEvent message) =>
        long.TryParse(message.Sender.Id, out var senderId) ? senderId : 0;

    public static string GetMessageScene(MessageEvent message) => message.Channel.Type switch
    {
        ChannelType.Group => "group",
        ChannelType.Direct => "friend",
        ChannelType.Other => "temp",
        _ => "unknown",
    };
}
