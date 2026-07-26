using ShiroBot.SDK.Models;

namespace Shirobot.Plugin.MyParser.Parsing;

internal sealed class ParseProviderRegistry(IEnumerable<IParseProvider> providers)
{
    private readonly IReadOnlyList<IParseProvider> _providers = providers.ToArray();

    public IParseProvider? FindProvider(string text)
    {
        return _providers.FirstOrDefault(provider => provider.CanHandle(text));
    }

    public IParseProvider? FindProvider(MessageEvent message, out string parseText)
    {
        var plainText = GetPlainText(message);
        foreach (var provider in _providers)
        {
            var candidate = provider is IIncomingMessageParseProvider incomingProvider
                ? incomingProvider.ExtractParseText(message)
                : plainText;
            if (string.IsNullOrWhiteSpace(candidate) || !provider.CanHandle(candidate))
            {
                continue;
            }

            parseText = candidate;
            return provider;
        }

        parseText = plainText;
        return null;
    }

    public async Task<MediaParseResult> ParseAsync(string text, CancellationToken cancellationToken = default)
    {
        var candidates = _providers.Where(provider => provider.CanHandle(text)).ToArray();
        if (candidates.Length == 0)
        {
            throw new InvalidOperationException("未找到可处理该链接的解析提供商。");
        }

        Exception? lastError = null;
        foreach (var provider in candidates)
        {
            try
            {
                return await provider.ParseAsync(text, cancellationToken);
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

    private static string GetPlainText(MessageEvent message)
    {
        return message.GetPlainText();
    }
}
