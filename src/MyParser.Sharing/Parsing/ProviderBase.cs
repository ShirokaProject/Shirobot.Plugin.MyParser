using ShiroBot.SDK.Models;
using ShiroBot.SDK.Plugin;
using ShiroBot.SDK.Abstractions;

namespace Shirobot.Plugin.MyParser.Parsing;

public abstract class MyParserProviderModuleBase : IMyParserProviderModule
{
    public abstract string Id { get; }
    public virtual string DisplayName => Id;
    public virtual string? Description => null;
    public virtual IReadOnlyList<string> Tags => [];

    public abstract IReadOnlyList<IParseProvider> CreateProviders(PluginConfig config);
}

public abstract class ProviderMessageHandlerBase(ProviderMessageHandlerContext context) : IProviderMessageHandler
{
    protected IBotContext BotContext { get; } = context.BotContext;
    protected PluginConfig Config { get; } = context.Config;
    protected IParseProvider PrimaryProvider { get; } = context.PrimaryProvider;
    protected IProviderHostServices HostServices { get; } = context.HostServices;
    protected IProviderMediaCardRenderer? CardRenderer { get; } = context.CardRenderer;
    protected virtual string ReactionPlatformName => PrimaryProvider.Id.StartsWith("bilibili", StringComparison.OrdinalIgnoreCase)
        ? "Bilibili"
        : PrimaryProvider.Name;

    public abstract string ProviderId { get; }

    public async Task ParseAndReplyAsync(
        MessageEvent message,
        string text,
        bool silentProviderMismatch = false,
        CancellationToken cancellationToken = default)
    {
        await ReactAsync(message, "351", ReactionPlatformName).ConfigureAwait(false);
        try
        {
            var media = await PrimaryProvider.ParseAsync(text, cancellationToken).ConfigureAwait(false);
            await PresentAsync(message, media, silentProviderMismatch, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (silentProviderMismatch && ParseProviderRegistry.IsProviderMismatch(ex))
        {
            await RemoveReactionAsync(message, "351", ReactionPlatformName).ConfigureAwait(false);
            BotLog.Info($"MyParser 自动解析忽略不匹配链接: provider={PrimaryProvider.Id}, error={ex.Message}");
        }
        catch (Exception ex)
        {
            await ReactAsync(message, "9", ReactionPlatformName).ConfigureAwait(false);
            var failureKind = ex is IProviderClassifiedException classified
                ? classified.FailureKind
                : ex is TimeoutException or TaskCanceledException
                    ? ProviderFailureKind.Timeout
                    : ex is InvalidOperationException
                        ? ProviderFailureKind.Parse
                        : ProviderFailureKind.Unexpected;
            await ReportFailureAsync(message, failureKind, ex).ConfigureAwait(false);
        }
    }

    protected abstract Task PresentAsync(
        MessageEvent message,
        ParsedMedia media,
        bool silentProviderMismatch,
        CancellationToken cancellationToken);

    protected Task ReactAsync(MessageEvent message, string faceId, string platformName)
    {
        return HostServices.ReactAsync(message, faceId, platformName);
    }

    protected Task RemoveReactionAsync(MessageEvent message, string faceId, string platformName)
    {
        return HostServices.RemoveReactionAsync(message, faceId, platformName);
    }

    protected Task<SentMessage> ReplyAsync(MessageEvent message, string text)
    {
        return HostServices.ReplyTextAsync(Config, message, text);
    }

    protected Task ReportFailureAsync(MessageEvent message, ProviderFailureKind kind,
        Exception? exception = null, string? diagnosticContext = null)
    {
        return HostServices.ReportFailureAsync(Config, message, ReactionPlatformName, kind, exception, diagnosticContext);
    }

    protected Task<SentMessage> SendImageAsync(MessageEvent message, ImageSegment segment)
    {
        return HostServices.SendImageAsync(message, segment);
    }

    protected Task<SentMessage> SendSegmentsAsync(MessageEvent message, IReadOnlyList<MessageSegment> segments)
    {
        return HostServices.SendSegmentsAsync(message, segments);
    }

    protected string ResolveCookiePath(string fileName)
    {
        return HostServices.ResolveCookiePath(fileName);
    }

    protected string GetMessageScene(MessageEvent message)
    {
        return HostServices.GetMessageScene(message);
    }

    public virtual void Dispose()
    {
    }
}
