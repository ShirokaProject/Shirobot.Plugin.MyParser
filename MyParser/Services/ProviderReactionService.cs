using System.Collections.Concurrent;
using ShiroBot.Model.QQ;
using ShiroBot.SDK.Abstractions;
using ShiroBot.SDK.Models;
using ShiroBot.SDK.Plugin;

namespace Shirobot.Plugin.MyParser.Services;

internal sealed class ProviderReactionService(IBotContext context)
{
    private static readonly ConcurrentDictionary<string, byte> SentReactions = new(StringComparer.Ordinal);

    public async Task AddAsync(MessageEvent message, string faceId, string platformName)
    {
        if (!TryGetGroupMessage(message, out var groupApi, out var groupId, out var messageSeq)) return;
        var key = $"{groupId}:{messageSeq}:{faceId}";
        if (!SentReactions.TryAdd(key, 0)) return;

        try
        {
            await groupApi.SendMessageReactionAsync(groupId, messageSeq, faceId).ConfigureAwait(false);
            if (!string.Equals(faceId, "351", StringComparison.OrdinalIgnoreCase))
                await RemoveAsync(message, "351", platformName).ConfigureAwait(false);
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

    public async Task RemoveAsync(MessageEvent message, string faceId, string platformName)
    {
        if (!TryGetGroupMessage(message, out var groupApi, out var groupId, out var messageSeq)) return;
        var key = $"{groupId}:{messageSeq}:{faceId}";
        if (!SentReactions.ContainsKey(key)) return;

        try
        {
            await groupApi.SendMessageReactionAsync(groupId, messageSeq, faceId, isAdd: false).ConfigureAwait(false);
            SentReactions.TryRemove(key, out _);
        }
        catch (Exception ex)
        {
            BotLog.Warning($"MyParser {platformName} 消息取消表情失败: group_id={groupId}, message_seq={messageSeq}, face={faceId}, error={ex.Message}");
        }
    }

    public static void ClearCache() => SentReactions.Clear();

    private bool TryGetGroupMessage(MessageEvent message, out IQGroupApi groupApi, out long groupId, out long messageSeq)
    {
        groupApi = null!;
        groupId = 0;
        messageSeq = 0;
        if (message.Channel.Type != ChannelType.Group
            || !long.TryParse(message.Channel.Id, out groupId)
            || !long.TryParse(message.MessageId, out messageSeq)) return false;

        groupApi = context.GetAdapterExtension<IQGroupApi>()!;
        return groupApi is not null;
    }

    private static bool IsAlreadyReactedError(Exception ex)
    {
        for (var current = ex; current is not null; current = current.InnerException)
        {
            if (current.Message.Contains("已经设置过", StringComparison.OrdinalIgnoreCase)
                || current.Message.Contains("already", StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }
}
