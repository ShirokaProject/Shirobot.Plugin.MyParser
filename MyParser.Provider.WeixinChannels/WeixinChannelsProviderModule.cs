using MyParser.Provider.WeixinChannels.Infrastructure;
using MyParser.Provider.WeixinChannels.Parsing;
using ShiroBot.SDK.Models;
using ShiroBot.SDK.Plugin;

namespace MyParser.Provider.WeixinChannels;

[MyParserProvider("weixinchannels")]
public sealed class WeixinChannelsProviderModule : MyParserProviderModuleBase, ICookieValidator, IProviderCookieStore, IProviderAutoParsePolicy, IProviderResultMessageClassifier, IProviderMediaCardRendererFactory
{
    public override string Id => WeixinChannelsConstants.ProviderId;

    public override string DisplayName => WeixinChannelsConstants.DisplayName;

    public IProviderMediaCardRenderer CreateMediaCardRenderer(ProviderCardRenderContext context) => new WeixinChannelsMediaCardRenderer(context);

    public IReadOnlyList<ProviderCookieDescriptor> CookieDescriptors =>
    [
        new(
            Id,
            "腾讯元宝",
            "weixinchannels-yuanbao.txt",
            cookie => MyParserRuntime.WeixinChannelsYuanbaoCookie = cookie,
            LooksLikeCookie,
            EmptyHint: "请在插件配置的 Cookie 分组填入腾讯元宝 Cookie；保存后自动重载。",
            InvalidHint: "请填入 yuanbao.tencent.com 请求头 Cookie: 后面的完整值。")
    ];

    public override IReadOnlyList<IParseProvider> CreateProviders(PluginConfig config)
    {
        return [new WeixinChannelsParseProvider(new WeixinChannelsParser(config))];
    }

    public bool LooksLikeCookie(string cookie) => WeixinChannelsParser.LooksLikeYuanbaoCookie(cookie);

    public bool IsAutoParseEnabled(PluginConfig config) => config.AutoParseWeixinChannelsLinks;

    public bool IsPluginResultMessage(string text)
    {
        return text.StartsWith("微信视频号解析", StringComparison.OrdinalIgnoreCase)
               || text.StartsWith("视频号解析", StringComparison.OrdinalIgnoreCase);
    }

    private static string GetPlainText(IncomingMessage message) => message.GetPlainText();
}
