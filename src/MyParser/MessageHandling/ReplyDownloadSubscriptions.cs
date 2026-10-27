using ShiroBot.SDK.Models;
using ShiroBot.SDK.Plugin;

namespace Shirobot.Plugin.MyParser.MessageHandling;

// Keep subscriptions bounded and release them when the plugin is unloaded.
internal sealed class ReplyDownloadSubscriptions : IDisposable
{
    private const int Capacity = 1024;
    private readonly Lock _gate = new();
    private readonly Queue<(DateTimeOffset ExpiresAt, IReplySubscription Subscription)> _entries = new();
    private bool _disposed;

    public void Register(IMessageContext messages, MessageEvent source, string messageId,
        TimeSpan lifetime, Func<MessageEvent, Task> upload)
    {
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
                if (!reply.GetPlainText().Trim().Equals("dl", StringComparison.OrdinalIgnoreCase)) return;
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

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            while (_entries.TryDequeue(out var entry)) entry.Subscription.Dispose();
        }
    }
}
