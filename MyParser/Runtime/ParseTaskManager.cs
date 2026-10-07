using System.Collections.Concurrent;
using ShiroBot.Model.QQ;
using ShiroBot.SDK.Models;

namespace Shirobot.Plugin.MyParser;

internal sealed class ParseTaskManager
{
    private static readonly TimeSpan ProviderWorkCooldown = TimeSpan.FromSeconds(15);
    private readonly ConcurrentDictionary<ParseTaskKey, ParseTaskState> _parseTasks = new();
    private readonly ConcurrentDictionary<ChannelTaskKey, DateTimeOffset> _mutedChannels = new();
    private readonly ConcurrentDictionary<ChannelTaskKey, byte> _wholeMutedChannels = new();
    private readonly ConcurrentDictionary<MemberTaskKey, DateTimeOffset> _mutedMembers = new();
    private readonly ConcurrentDictionary<ProviderCooldownKey, DateTimeOffset> _providerCooldowns = new();
    private readonly Lock _backgroundTasksLock = new();
    private readonly HashSet<Task> _backgroundTasks = [];
    private bool _isStopping;

    public bool TryEnterProviderCooldown(string providerId, string parseText, out string workIdentity, out TimeSpan remaining)
    {
        var key = new ProviderCooldownKey(providerId, ProviderWorkIdentity.Normalize(providerId, parseText));
        workIdentity = key.WorkIdentity;
        while (true)
        {
            var now = DateTimeOffset.UtcNow;
            if (_providerCooldowns.TryGetValue(key, out var expiresAt))
            {
                if (expiresAt > now)
                {
                    remaining = expiresAt - now;
                    return true;
                }
                if (_providerCooldowns.TryUpdate(key, now.Add(ProviderWorkCooldown), expiresAt))
                {
                    remaining = TimeSpan.Zero;
                    return false;
                }
                continue;
            }

            if (_providerCooldowns.TryAdd(key, now.Add(ProviderWorkCooldown)))
            {
                remaining = TimeSpan.Zero;
                return false;
            }
        }
    }

    public bool IsResponseSuppressed(MessageEvent message)
    {
        if (message.IsDirect) return false;
        var now = DateTimeOffset.UtcNow;
        var channelKey = ChannelTaskKey.From(message.Platform, message.Channel);
        if (_wholeMutedChannels.ContainsKey(channelKey)) return true;

        if (_mutedChannels.TryGetValue(channelKey, out var channelUntil))
        {
            if (channelUntil > now) return true;
            _mutedChannels.TryRemove(channelKey, out _);
        }

        var memberKey = new MemberTaskKey(channelKey, message.Sender.Id);
        if (_mutedMembers.TryGetValue(memberKey, out var memberUntil))
        {
            if (memberUntil > now) return true;
            _mutedMembers.TryRemove(memberKey, out _);
        }

        return false;
    }

    public Task QueueAsync(MessageEvent message, Func<CancellationToken, Task> action)
    {
        ParseTaskKey key = ParseTaskKey.From(message);
        ParseTaskState state;
        Task task;
        lock (_backgroundTasksLock)
        {
            if (_isStopping) return Task.CompletedTask;
            var cancellation = CancellationTokenSource.CreateLinkedTokenSource(MyParserRuntime.BackgroundCancellationToken);
            state = new ParseTaskState(message.Sender.Id, cancellation);
            if (!_parseTasks.TryAdd(key, state))
            {
                cancellation.Dispose();
                return Task.CompletedTask;
            }

            if (IsResponseSuppressed(message))
            {
                _parseTasks.TryRemove(key, out _);
                cancellation.Cancel();
                cancellation.Dispose();
                return Task.CompletedTask;
            }

            task = Task.Run(() => ExecuteAsync(key, state, action));
            _backgroundTasks.Add(task);
        }

        _ = task.ContinueWith(completedTask =>
        {
            lock (_backgroundTasksLock) _backgroundTasks.Remove(completedTask);
            if (completedTask.Exception is { } exception)
                BotLog.Error($"MyParser 后台解析失败: {exception.GetBaseException().Message}");
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return Task.CompletedTask;
    }

    public Task HandleMessageDeletedAsync(MessageDeletedEvent evt)
    {
        var key = ParseTaskKey.From(evt);
        CancelTasks(pair => pair.Key == key, "message-deleted");
        return Task.CompletedTask;
    }

    public Task HandleGroupMuteAsync(PlatformEvent evt)
    {
        if (evt.Raw is not QGroupMute mute || evt.Channel is null) return Task.CompletedTask;
        var botMuted = string.Equals(mute.UserId.ToString(), evt.SelfId, StringComparison.Ordinal);
        var channelKey = ChannelTaskKey.From(evt.Platform, evt.Channel);
        if (botMuted)
        {
            if (mute.IsUnmute) _mutedChannels.TryRemove(channelKey, out _);
            else _mutedChannels[channelKey] = DateTimeOffset.UtcNow.Add(mute.Duration);
        }
        else
        {
            var memberKey = new MemberTaskKey(channelKey, mute.UserId.ToString());
            if (mute.IsUnmute) _mutedMembers.TryRemove(memberKey, out _);
            else _mutedMembers[memberKey] = DateTimeOffset.UtcNow.Add(mute.Duration);
        }

        if (!mute.IsUnmute)
        {
            CancelTasks(pair => ParseTaskKey.IsSameChannel(pair.Key, evt.Platform, evt.Channel)
                                && (botMuted || string.Equals(pair.Value.SenderId, mute.UserId.ToString(), StringComparison.Ordinal)),
                botMuted ? "bot-muted" : "sender-muted");
        }

        return Task.CompletedTask;
    }

    public Task HandleGroupWholeMuteAsync(PlatformEvent evt)
    {
        if (evt.Raw is QGroupWholeMute wholeMute && evt.Channel is not null)
        {
            var channelKey = ChannelTaskKey.From(evt.Platform, evt.Channel);
            if (wholeMute.IsMute)
            {
                _wholeMutedChannels[channelKey] = 0;
                CancelTasks(pair => ParseTaskKey.IsSameChannel(pair.Key, evt.Platform, evt.Channel), "group-whole-muted");
            }
            else
            {
                _wholeMutedChannels.TryRemove(channelKey, out _);
            }
        }

        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        lock (_backgroundTasksLock) _isStopping = true;
        MyParserRuntime.BeginUnload();
        CancelTasks(_ => true, "plugin-unload");
        Task[] tasks;
        lock (_backgroundTasksLock) tasks = [.. _backgroundTasks];
        await Task.WhenAll(tasks).ConfigureAwait(false);

        _mutedChannels.Clear();
        _wholeMutedChannels.Clear();
        _mutedMembers.Clear();
        _providerCooldowns.Clear();
    }

    private async Task ExecuteAsync(ParseTaskKey key, ParseTaskState state, Func<CancellationToken, Task> action)
    {
        try
        {
            await action(state.Cancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (state.Cancellation.IsCancellationRequested)
        {
            // Task cancellation is expected on deletion, mute, or plugin unload.
        }
        finally
        {
            lock (_backgroundTasksLock)
            {
                if (_parseTasks.TryGetValue(key, out var current) && ReferenceEquals(current, state))
                    _parseTasks.TryRemove(key, out _);
                state.Cancellation.Dispose();
            }
        }
    }

    private void CancelTasks(Func<KeyValuePair<ParseTaskKey, ParseTaskState>, bool> predicate, string reason)
    {
        lock (_backgroundTasksLock)
        {
            foreach (var pair in _parseTasks.Where(predicate).ToArray())
            {
                if (_parseTasks.TryRemove(pair.Key, out var state))
                {
                    state.Cancellation.Cancel();
                    BotLog.Info($"MyParser 清除解析任务: reason={reason}, channel={pair.Key.ChannelId}, message_id={pair.Key.MessageId}");
                }
            }
        }
    }

    private sealed record ParseTaskState(string SenderId, CancellationTokenSource Cancellation);

    private readonly record struct ParseTaskKey(string Platform, ChannelType ChannelType, string ChannelId, string MessageId)
    {
        public static ParseTaskKey From(MessageEvent message) => new(message.Platform, message.Channel.Type, message.Channel.Id, message.MessageId);
        public static ParseTaskKey From(MessageDeletedEvent evt) => new(evt.Platform, evt.Channel.Type, evt.Channel.Id, evt.MessageId);
        public static bool IsSameChannel(ParseTaskKey key, string platform, Channel channel) =>
            string.Equals(key.Platform, platform, StringComparison.OrdinalIgnoreCase)
            && key.ChannelType == channel.Type
            && string.Equals(key.ChannelId, channel.Id, StringComparison.Ordinal);
    }

    private readonly record struct ChannelTaskKey(string Platform, ChannelType ChannelType, string ChannelId)
    {
        public static ChannelTaskKey From(string platform, Channel channel) => new(platform, channel.Type, channel.Id);
    }

    private readonly record struct MemberTaskKey(ChannelTaskKey Channel, string UserId);
    private readonly record struct ProviderCooldownKey(string ProviderId, string WorkIdentity);
}
