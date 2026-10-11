using System.Collections.Concurrent;
using System.Globalization;
using ShiroBot.SDK.Models;
using ShiroBot.SDK.Plugin;

namespace MyParser.Provider.BiliBili.Parsing;

internal static class BilibiliMultiPageReplyCache
{
    private const int Capacity = 1024;
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);
    private static readonly ConcurrentDictionary<ReplyKey, Entry> Entries = new();

    public static bool TryParsePage(string text, out int page)
    {
        var value = text.Trim();
        if (value.StartsWith("p", StringComparison.OrdinalIgnoreCase)) value = value[1..];
        return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out page) && page > 0;
    }

    public static void Register(MessageEvent source, string messageId, string bvid, int pageCount,
        IMessageContext? messages = null, Func<MessageEvent, string, Task>? previewHandler = null)
    {
        RegisterSelections(source, messageId,
            Enumerable.Range(1, pageCount).Select(page => $"https://www.bilibili.com/video/{bvid}/?p={page}").ToArray(),
            messages, previewHandler);
    }

    public static void RegisterSelections(MessageEvent source, string messageId, IReadOnlyList<string> urls,
        IMessageContext? messages = null, Func<MessageEvent, string, Task>? previewHandler = null)
    {
        if (string.IsNullOrWhiteSpace(messageId) || urls.Count == 0) return;
        var now = DateTimeOffset.UtcNow;
        foreach (var pair in Entries.Where(pair => pair.Value.ExpiresAt <= now)) Remove(pair.Key);
        var key = Key(source, messageId);
        Remove(key);
        IReplySubscription? subscription = null;
        if (messages is not null && previewHandler is not null)
        {
            var reference = new MessageReference(source.InstanceId ?? source.Platform, source.Channel, messageId);
            subscription = messages.SubscribeReply(reference, Lifetime, async reply =>
            {
                if (!TryParsePage(reply.GetPlainText(), out var page)) return;
                var selection = Resolve(reply, messageId, page);
                if (selection is not null) await previewHandler(reply, selection).ConfigureAwait(false);
            }, disposeOnReply: false);
        }
        Entries[key] = new Entry(urls.ToArray(), now.Add(Lifetime), subscription);
        foreach (var pair in Entries.OrderBy(pair => pair.Value.ExpiresAt).Take(Math.Max(0, Entries.Count - Capacity)))
            Remove(pair.Key);
    }

    public static string? Resolve(MessageEvent message, string quotedMessageId, int page, bool allowSubscribed = true)
    {
        var key = Key(message, quotedMessageId);
        if (!Entries.TryGetValue(key, out var entry)) return null;
        if (entry.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            Remove(key);
            return null;
        }
        // The Bot API callback already queues these replies before ordinary message dispatch.
        if (!allowSubscribed && entry.Subscription is not null) return null;
        return page > 0 && page <= entry.Urls.Count
            ? entry.Urls[page - 1]
            : null;
    }

    public static void Clear()
    {
        foreach (var key in Entries.Keys) Remove(key);
    }

    private static void Remove(ReplyKey key)
    {
        if (Entries.TryRemove(key, out var entry)) entry.Subscription?.Dispose();
    }

    private static ReplyKey Key(MessageEvent message, string id) =>
        new(message.InstanceId, message.Platform, message.Channel.Type, message.Channel.Id, id);

    private readonly record struct ReplyKey(string? InstanceId, string Platform, ChannelType ChannelType, string ChannelId, string MessageId);
    private sealed record Entry(IReadOnlyList<string> Urls, DateTimeOffset ExpiresAt, IReplySubscription? Subscription);
}
