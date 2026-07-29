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

    public async Task<MediaParseResult> ParseAsync(string text, CancellationToken cancellationToken = default)
    {
        var context = new ProviderParseTextContext(IsAutoParse: false, IsUrlLike: IsUrlLike(text));
        var candidates = _providers
            .Select(provider => new
            {
                Provider = provider,
                ParseText = provider is IProviderParseTextMatcher matcher
                    ? matcher.TryNormalizeParseText(text, context)
                    : context.IsUrlLike ? text : null,
            })
            .Where(item => !string.IsNullOrWhiteSpace(item.ParseText) && item.Provider.CanHandle(item.ParseText))
            .ToArray();
        if (candidates.Length == 0)
        {
            throw new InvalidOperationException("未找到可处理该链接的解析提供商。");
        }

        Exception? lastError = null;
        foreach (var provider in candidates)
        {
            try
            {
                return await provider.Provider.ParseAsync(provider.ParseText!, cancellationToken);
            }
            catch (Exception ex) when (candidates.Length > 1 && IsProviderMismatch(ex))
            {
                lastError = ex;
            }
        }

        if (lastError is not null)
        {
            throw lastError;
        }

        throw new InvalidOperationException("未找到可处理该链接的解析提供商。");
    }

    private static bool IsProviderMismatch(Exception ex)
    {
        var message = ex.Message;
        return message.Contains("短链接跳转后未找到", StringComparison.OrdinalIgnoreCase)
               || message.Contains("无法从输入中提取", StringComparison.OrdinalIgnoreCase)
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
