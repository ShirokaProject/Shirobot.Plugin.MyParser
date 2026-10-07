using ShiroBot.SDK.Models;
using ShiroBot.SDK.Plugin;
using Shirobot.Plugin.MyParser.Parsing;

namespace Shirobot.Plugin.MyParser;

internal sealed class MessageParseCoordinator(
    IBotContext botContext,
    PluginConfig config,
    ProviderCatalog catalog,
    ParseTaskManager taskManager)
{
    public IReadOnlyList<ProviderCommandDescriptor> ProviderCommands => catalog.CreateCommands();

    public bool ShouldAutoParse(MessageEvent message)
    {
        if (!catalog.HasAnyAutoParseProviderEnabled()) return false;
        if (catalog.TryBuildReplyParseText(message, out _)) return true;

        var text = GetPlainText(message);
        if (!string.IsNullOrWhiteSpace(text))
        {
            var trimmed = text.TrimStart();
            if (catalog.IsPluginResultMessage(trimmed)
                || catalog.IsDeferredParseText(trimmed)
                || ProviderCommands.Any(command => IsProviderCommand(trimmed, command)))
            {
                return false;
            }
        }

        if (!string.IsNullOrWhiteSpace(catalog.GetStrictAutoParseText(message))) return true;
        if (ContainsHttpUrl(text)) return true;

        return message.Segments.Any(segment => segment is not TextSegment and not QuoteSegment
            and not MentionSegment and not MentionAllSegment);
    }

    public Task HandleAutoParseAsync(MessageEvent message)
    {
        if (catalog.TryBuildReplyParseText(message, out var replyParseText))
        {
            var replyProvider = catalog.Registry.FindProvider(replyParseText, isAutoParse: false, out _);
            if (!catalog.IsAutoParseEnabled(replyProvider)) return Task.CompletedTask;
            return QueueParse(message, replyParseText, silentProviderMismatch: true, isAutoParse: false);
        }

        var normalizedText = catalog.GetStrictAutoParseText(message);
        if (!string.IsNullOrWhiteSpace(normalizedText))
        {
            return QueueParse(message, normalizedText, silentProviderMismatch: true, isAutoParse: true);
        }

        if (catalog.Registry.FindProvider(message, out var incomingParseText) is { } incomingProvider
            && !catalog.HasIncomingProviderNormalizer(incomingProvider.Id)
            && !string.IsNullOrWhiteSpace(incomingParseText))
        {
            return QueueParse(message, incomingParseText, silentProviderMismatch: true, isAutoParse: true);
        }

        var plainText = GetPlainText(message);
        if (ContainsHttpUrl(plainText))
        {
            return QueueParse(message, plainText, silentProviderMismatch: true, isAutoParse: true);
        }

        return Task.CompletedTask;
    }

    public bool IsProviderCommand(MessageEvent message, ProviderCommandDescriptor descriptor) =>
        IsProviderCommand(GetPlainText(message).TrimStart(), descriptor);

    public static bool IsProviderCommand(string text, ProviderCommandDescriptor descriptor)
    {
        if (!text.StartsWith(descriptor.Command, StringComparison.OrdinalIgnoreCase)) return false;
        return text.Length == descriptor.Command.Length || char.IsWhiteSpace(text[descriptor.Command.Length]);
    }

    public async Task HandleProviderCommandAsync(MessageEvent message, ProviderCommandDescriptor descriptor)
    {
        await descriptor.HandleAsync(message).ConfigureAwait(false);
    }

    public Task HandleMessageDeletedAsync(MessageDeletedEvent evt) => taskManager.HandleMessageDeletedAsync(evt);
    public Task HandleGroupMuteAsync(PlatformEvent evt) => taskManager.HandleGroupMuteAsync(evt);
    public Task HandleGroupWholeMuteAsync(PlatformEvent evt) => taskManager.HandleGroupWholeMuteAsync(evt);

    public Task StopAsync() => taskManager.StopAsync();

    private Task QueueParse(MessageEvent message, string text, bool silentProviderMismatch, bool isAutoParse) =>
        taskManager.QueueAsync(message, cancellationToken =>
            DispatchParseAsync(message, text, silentProviderMismatch, isAutoParse, cancellationToken));

    private async Task DispatchParseAsync(
        MessageEvent message,
        string text,
        bool silentProviderMismatch,
        bool isAutoParse,
        CancellationToken cancellationToken)
    {
        if (taskManager.IsResponseSuppressed(message))
        {
            BotLog.Info($"MyParser 忽略禁言状态下的解析请求: channel={message.Channel.Id}, sender={message.Sender.Id}, message_id={message.MessageId}");
            return;
        }

        text = await UrlRedirectResolver.ResolveTextAsync(text, cancellationToken).ConfigureAwait(false);
        text = catalog.NormalizeParseText(text);
        if (catalog.IsDeferredParseText(text)) return;

        var provider = catalog.Registry.FindProvider(text, isAutoParse, out var parseText);
        if (provider is null)
        {
            if (!silentProviderMismatch)
                await botContext.Message.ReplyAsync(message, "未找到可处理该链接的解析提供商。").ConfigureAwait(false);
            return;
        }

        if (!catalog.IsProviderEnabled(provider))
        {
            if (!silentProviderMismatch)
                await botContext.Message.ReplyAsync(message, $"{provider.Name} 解析已关闭。").ConfigureAwait(false);
            return;
        }

        if (isAutoParse && !catalog.IsAutoParseEnabled(provider)) return;
        if (provider.Id == "youtube" && config.YouTubeAdminOnly && !botContext.IsAdmin(message.Sender.Id)) return;

        var handler = catalog.GetHandler(provider.Id);
        if (handler is null)
        {
            await botContext.Message.ReplyAsync(message, $"{provider.Name} 已识别，但该 provider 未接入消息发送流程。").ConfigureAwait(false);
            return;
        }

        if (taskManager.TryEnterProviderCooldown(provider.Id, parseText, out var workIdentity, out var remaining))
        {
            BotLog.Info($"MyParser 冷却期内静默忽略重复作品: provider={provider.Id}, work={workIdentity}, remaining_seconds={Math.Ceiling(remaining.TotalSeconds):F0}");
            return;
        }

        if (taskManager.IsResponseSuppressed(message)) return;

        try
        {
            await handler.ParseAndReplyAsync(message, parseText, silentProviderMismatch, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            BotLog.Info($"MyParser 解析任务已取消: channel={message.Channel.Id}, message_id={message.MessageId}");
        }
    }

    private static bool ContainsHttpUrl(string? text) =>
        !string.IsNullOrWhiteSpace(text)
        && (text.Contains("https://", StringComparison.OrdinalIgnoreCase)
            || text.Contains("http://", StringComparison.OrdinalIgnoreCase));

    private static string GetPlainText(MessageEvent message) => message.GetPlainText();
}
