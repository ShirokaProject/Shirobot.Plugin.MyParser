using ShiroBot.SDK.Models;
using ShiroBot.SDK.Plugin;

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
    protected ParseProviderRegistry ProviderRegistry { get; } = context.ProviderRegistry;
    protected IParseProvider PrimaryProvider { get; } = context.PrimaryProvider;
    protected IProviderHostServices HostServices { get; } = context.HostServices;

    public abstract string ProviderId { get; }

    public abstract Task ParseAndReplyAsync(
        MessageEvent message,
        string text,
        bool silentProviderMismatch = false,
        CancellationToken cancellationToken = default);

    public virtual Task HandleLoginAsync(MessageEvent message)
    {
        return ReplyAsync(message, $"{ProviderId} provider 不支持扫码登录。");
    }

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

    protected Task SendImageAsync(MessageEvent message, ImageSegment segment)
    {
        return HostServices.SendImageAsync(message, segment);
    }

    protected string ResolveCookiePath(string fileName)
    {
        return HostServices.ResolveCookiePath(fileName);
    }

    protected string GetMessageScene(MessageEvent message)
    {
        return HostServices.GetMessageScene(message);
    }

    protected static long GetBotOrSenderId(MessageEvent message)
    {
        return ProviderTextUtilities.GetBotOrSenderId(message);
    }

    public virtual void Dispose()
    {
    }
}

public static class ProviderTextUtilities
{
    public static long GetBotOrSenderId(MessageEvent message)
    {
        return long.TryParse(message.Sender.Id, out var senderId) ? senderId : 0;
    }

    public static string TrimLine(string value, int maxLength)
    {
        value = value.ReplaceLineEndings(" ").Trim();
        return value.Length <= maxLength ? value : value[..maxLength] + "…";
    }

    public static IEnumerable<string> SplitText(string text, int chunkSize)
    {
        if (string.IsNullOrEmpty(text))
        {
            yield break;
        }

        for (var index = 0; index < text.Length; index += chunkSize)
        {
            yield return text.Substring(index, Math.Min(chunkSize, text.Length - index));
        }
    }

    public static string FormatSize(long? bytes)
    {
        if (bytes is null)
        {
            return "unknown";
        }

        if (bytes.Value >= 1024L * 1024L * 1024L)
        {
            return $"{bytes.Value / 1024d / 1024d / 1024d:F2}GB";
        }

        if (bytes.Value >= 1024L * 1024L)
        {
            return $"{bytes.Value / 1024d / 1024d:F2}MB";
        }

        if (bytes.Value >= 1024L)
        {
            return $"{bytes.Value / 1024d:F1}KB";
        }

        return bytes.Value + "B";
    }

    public static string SanitizeFileName(string value, int? maxLength = null)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
        {
            value = value.Replace(c, '_');
        }

        return maxLength is > 0 && value.Length > maxLength.Value ? value[..maxLength.Value] : value;
    }
}
