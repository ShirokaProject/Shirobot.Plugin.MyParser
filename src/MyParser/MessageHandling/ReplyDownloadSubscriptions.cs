using ShiroBot.SDK.Models;
using ShiroBot.SDK.Plugin;

namespace Shirobot.Plugin.MyParser.MessageHandling;

// 回复关键词触发的订阅（回复 mp3 取音频、回复 原图 取全部内容）：数量有上限，插件卸载时全部释放。
internal sealed class ReplyDownloadSubscriptions : IDisposable
{
    private const int Capacity = 1024;
    private readonly Lock _gate = new();
    private readonly Queue<(DateTimeOffset ExpiresAt, IReplySubscription Subscription)> _entries = new();
    private bool _disposed;

    public void Register(IMessageContext messages, MessageEvent source, string messageId,
        TimeSpan lifetime, Func<MessageEvent, Task> upload, Func<string, bool>? matches = null)
    {
        matches ??= IsDownloadKeyword;
        if (string.IsNullOrWhiteSpace(messageId) || lifetime <= TimeSpan.Zero) return;
        lock (_gate)
        {
            if (_disposed) return;
            var now = DateTimeOffset.UtcNow;
            while (_entries.TryPeek(out var entry) && (entry.ExpiresAt <= now || _entries.Count >= Capacity))
                _entries.Dequeue().Subscription.Dispose();
            var expiresAt = now.Add(lifetime);
            var reference = new MessageReference(source.InstanceId ?? source.Platform, source.Channel, messageId);
            var subscription = messages.SubscribeReply(reference, lifetime, async reply =>
            {
                if (!matches(reply.GetPlainText())) return;
                if (reply.InstanceId != source.InstanceId || reply.Platform != source.Platform
                    || reply.Channel.Type != source.Channel.Type || reply.Channel.Id != source.Channel.Id) return;
                lock (_gate)
                {
                    if (_disposed || DateTimeOffset.UtcNow >= expiresAt) return;
                }
                await upload(reply).ConfigureAwait(false);
            }, disposeOnReply: false);
            _entries.Enqueue((expiresAt, subscription));
        }
    }

    /// <summary>回复「mp3」或「dl」（不区分大小写）获取音频文件。卡片上提示的是 mp3，dl 保留兼容。</summary>
    public static bool IsDownloadKeyword(string text)
    {
        var value = text.Trim();
        return value.Equals("mp3", StringComparison.OrdinalIgnoreCase) || value.Equals("dl", StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            while (_entries.TryDequeue(out var entry)) entry.Subscription.Dispose();
        }
    }
}
