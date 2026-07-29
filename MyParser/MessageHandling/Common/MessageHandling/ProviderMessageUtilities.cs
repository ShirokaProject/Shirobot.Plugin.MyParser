using System.Collections.Concurrent;
using System.Diagnostics;
using ShiroBot.Qq.Model;
using ShiroBot.SDK.Abstractions;
using ShiroBot.SDK.Models;
using ShiroBot.SDK.Plugin;

namespace Shirobot.Plugin.MyParser.MessageHandling;

internal static class ProviderMessageUtilities
{
    private static readonly ConcurrentDictionary<string, byte> SentReactions = new(StringComparer.Ordinal);

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

    public static async Task SendImageAsync(IBotContext context, IncomingMessage message, ImageOutgoingSegment segment)
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

        var fileApi = context.GetAdapterExtension<IQqFileApi>()
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
        out IQqGroupApi groupApi,
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

        groupApi = context.GetAdapterExtension<IQqGroupApi>()!;
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
