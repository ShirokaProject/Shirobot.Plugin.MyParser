using ShiroBot.SDK.Models;
using ShiroBot.SDK.Abstractions;

namespace Shirobot.Plugin.MyParser.Parsing;

public sealed class ParseProviderRegistry(IEnumerable<IParseProvider> providers)
{
    private readonly IReadOnlyList<IParseProvider> _providers = providers.ToArray();

    public IParseProvider? FindProvider(string text)
    {
        return FindProvider(text, isAutoParse: false, out _);
    }

    public IParseProvider? FindProvider(string text, bool isAutoParse, out string parseText)
    {
        parseText = text;
        var context = new ProviderParseTextContext(isAutoParse, IsUrlLike(text));
        foreach (var provider in _providers)
        {
            var candidate = provider is IProviderParseTextMatcher matcher
                ? matcher.TryNormalizeParseText(text, context)
                : context.IsUrlLike ? text : null;
            if (string.IsNullOrWhiteSpace(candidate) || !provider.CanHandle(candidate))
            {
                continue;
            }

            BotLog.Info($"MyParser 入站 provider 选中: provider={provider.Id}, normalized={TrimLogValue(candidate)}");
            parseText = candidate;
            return provider;
        }

        return null;
    }

    public IParseProvider? FindProvider(MessageEvent message, out string parseText)
    {
        var plainText = GetPlainText(message);
        var context = new ProviderParseTextContext(IsAutoParse: true, IsUrlLike: IsUrlLike(plainText));
        foreach (var provider in _providers)
        {
            var candidate = provider is IIncomingMessageParseProvider incomingProvider
                ? incomingProvider.ExtractParseText(message)
                : null;
            candidate ??= provider is IProviderParseTextMatcher matcher
                ? matcher.TryNormalizeParseText(plainText, context)
                : context.IsUrlLike ? plainText : null;
            if (string.IsNullOrWhiteSpace(candidate) || !provider.CanHandle(candidate))
            {
                continue;
            }

            BotLog.Info($"MyParser 入站消息 provider 选中: provider={provider.Id}, normalized={TrimLogValue(candidate)}");
            parseText = candidate;
            return provider;
        }

        parseText = plainText;
        return null;
    }

    private static string TrimLogValue(string value)
    {
        value = value.ReplaceLineEndings(" ").Trim();
        return value.Length <= 220 ? value : value[..220] + "...";
    }

    public static bool IsProviderMismatch(Exception ex)
    {
        var message = ex.Message;
        return message.Contains("无法从输入中提取", StringComparison.OrdinalIgnoreCase)
               || message.Contains("不是视频", StringComparison.OrdinalIgnoreCase)
               || message.Contains("不是专栏", StringComparison.OrdinalIgnoreCase)
               || message.Contains("不是图文", StringComparison.OrdinalIgnoreCase)
               || message.Contains("不是动态", StringComparison.OrdinalIgnoreCase);
    }

    private static string GetPlainText(MessageEvent message) => message.GetPlainText();

    private static bool IsUrlLike(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var value = text.Trim();
        return value.Contains("://", StringComparison.Ordinal)
               || value.StartsWith("www.", StringComparison.OrdinalIgnoreCase)
               || value.Contains(".com/", StringComparison.OrdinalIgnoreCase)
               || value.Contains(".cn/", StringComparison.OrdinalIgnoreCase)
               || value.Contains(".tv/", StringComparison.OrdinalIgnoreCase);
    }
}
