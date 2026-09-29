using ShiroBot.SDK.Models;
using ShiroBot.SDK.Abstractions;

namespace Shirobot.Plugin.MyParser.Services;

internal sealed class ProviderFailureReporter(ProviderMessageSender messageSender)
{
    public async Task ReportAsync(
        PluginConfig config,
        MessageEvent message,
        string providerName,
        ProviderFailureKind kind,
        Exception? exception = null,
        string? diagnosticContext = null)
    {
        var contextText = string.IsNullOrWhiteSpace(diagnosticContext) ? string.Empty : $", context={diagnosticContext}";
        if (exception is null)
            BotLog.Warning($"MyParser provider failure: provider={providerName}, kind={kind}{contextText}");
        else
            BotLog.Error($"MyParser provider failure: provider={providerName}, kind={kind}{contextText}, exception={exception}");

        if (!config.SendProviderFailureMessages) return;
        var messageText = kind switch
        {
            ProviderFailureKind.Parse => $"{providerName}解析失败，请稍后重试。",
            ProviderFailureKind.Timeout => $"{providerName}请求超时，请稍后重试。",
            ProviderFailureKind.AuthenticationRequired => $"{providerName}需要有效登录态或 Cookie，请检查插件配置。",
            ProviderFailureKind.UnsupportedContent when string.Equals(providerName, "抖音", StringComparison.OrdinalIgnoreCase)
                => "暂不支持该抖音作品类型，目前支持公开的视频和图集。",
            ProviderFailureKind.UnsupportedContent => $"{providerName}暂不支持此内容类型。",
            ProviderFailureKind.MediaDelivery => $"{providerName}内容已解析，但媒体发送未完成。",
            _ => (string.IsNullOrWhiteSpace(config.ProviderFailureMessageTemplate)
                    ? "{provider}处理失败，请稍后重试。"
                    : config.ProviderFailureMessageTemplate)
                .Replace("{provider}", providerName, StringComparison.OrdinalIgnoreCase),
        };
        await messageSender.ReplyTextAsync(config, message, messageText).ConfigureAwait(false);
    }
}
