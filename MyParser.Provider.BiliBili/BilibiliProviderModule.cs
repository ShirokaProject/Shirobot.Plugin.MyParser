using ShiroBot.SDK.Models;
using ShiroBot.SDK.Core;
using ShiroBot.SDK.Plugin;
using Shirobot.Plugin.MyParser.Parsing;
using MyParser.Provider.BiliBili.Parsing;
using MyParser.Provider.BiliBili.MessageHandling;
using MyParser.Provider.BiliBili.Services;
using MyParser.Provider.BiliBili.Utilities;

namespace MyParser.Provider.BiliBili;

[MyParserProvider("bilibili")]
public sealed class BilibiliProviderModule : MyParserProviderModuleBase, IProviderMessageHandlerFactory, IProviderTextNormalizer, IIncomingProviderTextNormalizer, ICookieValidator, IProviderCookieStore, IProviderAutoParsePolicy, IProviderResultMessageClassifier, IProviderReplyParseTextBuilder
{
    public override string Id => "bilibili";

    public override string DisplayName => "Bilibili";

    public IReadOnlyList<ProviderCookieDescriptor> CookieDescriptors =>
    [
        new(
            Id,
            DisplayName,
            "bilibili.txt",
            cookie => MyParserRuntime.BilibiliCookie = cookie,
            LooksLikeCookie,
            EmptyHint: "请编辑 cookies/bilibili.txt，保存后自动重载。",
            InvalidHint: "请确保文件内容包含 SESSDATA/bili_jct 等 Cookie。")
    ];

    public override IReadOnlyList<IParseProvider> CreateProviders(PluginConfig config)
    {
        var parser = new BilibiliParser(config);
        return
        [
            new BilibiliArticleParseProvider(parser),
            new BilibiliBangumiParseProvider(new BilibiliBangumiParser(parser.HttpClient, config)),
            new BilibiliLiveParseProvider(new BilibiliLiveParser(parser.HttpClient)),
            new BilibiliParseProvider(parser),
        ];
    }

    public IProviderMessageHandler? CreateMessageHandler(ProviderMessageHandlerContext context)
    {
        return new BilibiliMessageHandler(context.BotContext, context.Config, context.ProviderRegistry, context.PrimaryProvider, context.HostServices);
    }

    public string? NormalizeParseText(string text)
    {
        return BilibiliUrlParser.ExtractStrictBilibiliUrl(text)
               ?? BilibiliUrlParser.NormalizeStandaloneBilibiliId(text);
    }

    public string? NormalizeParseText(IncomingMessage message)
    {
        var text = message.GetPlainText();
        return BilibiliUrlParser.ExtractStrictBilibiliUrl(text)
               ?? BilibiliUrlParser.NormalizeStandaloneBvid(text)
               ?? (BilibiliLightAppUrlExtractor.ExtractParseText(message) is { } extracted
                   ? BilibiliUrlParser.ExtractStrictBilibiliUrl(extracted)
                   : null);
    }

    public bool LooksLikeCookie(string cookie) => BilibiliParser.LooksLikeBilibiliCookie(cookie);

    public bool IsAutoParseEnabled(PluginConfig config) => config.AutoParseBilibiliLinks;

    public bool IsPluginResultMessage(string text)
    {
        return text.StartsWith("Bilibili 视频解析", StringComparison.OrdinalIgnoreCase)
               || text.StartsWith("Bilibili 直播解析", StringComparison.OrdinalIgnoreCase)
               || text.StartsWith("Bilibili 图文解析", StringComparison.OrdinalIgnoreCase)
               || text.StartsWith("Bilibili 专栏解析", StringComparison.OrdinalIgnoreCase);
    }

    public string? TryBuildParseText(IncomingMessage message)
    {
        var text = message.GetPlainText().Trim();
        if (!int.TryParse(text, out var page) || page <= 0)
        {
            return null;
        }

        var reply = message.GetQqReply();
        if (reply is null)
        {
            return null;
        }

        var repliedText = reply.GetPlainText().Trim();
        return IsDeferredParseText(repliedText) ? repliedText + page : null;
    }

    public bool IsDeferredParseText(string text)
    {
        return System.Text.RegularExpressions.Regex.IsMatch(
            text.Trim(),
            @"^https?://www\.bilibili\.com/video/BV[0-9A-Za-z]{10}/?\?p=\s*$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }

}
