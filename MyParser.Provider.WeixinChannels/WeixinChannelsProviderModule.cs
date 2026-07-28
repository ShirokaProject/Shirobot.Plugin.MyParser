using System.Text;
using MyParser.Provider.WeixinChannels.Infrastructure;
using MyParser.Provider.WeixinChannels.MessageHandling;
using MyParser.Provider.WeixinChannels.Parsing;
using ShiroBot.SDK.Models;
using ShiroBot.SDK.Plugin;

namespace MyParser.Provider.WeixinChannels;

[MyParserProvider("weixinchannels")]
public sealed class WeixinChannelsProviderModule : MyParserProviderModuleBase, IProviderMessageHandlerFactory, ICookieValidator, IProviderCookieStore, IProviderAutoParsePolicy, IProviderResultMessageClassifier, IProviderCommandContributor
{
    public override string Id => WeixinChannelsConstants.ProviderId;

    public override string DisplayName => WeixinChannelsConstants.DisplayName;

    public IReadOnlyList<ProviderCookieDescriptor> CookieDescriptors =>
    [
        new(
            Id,
            "腾讯元宝",
            "weixinchannels-yuanbao.txt",
            cookie => MyParserRuntime.WeixinChannelsYuanbaoCookie = cookie,
            LooksLikeCookie,
            EmptyHint: "可私信机器人发送 #wx-cookie <腾讯元宝 Cookie> 写入；用于解析 weixin.qq.com/sph 视频号分享链接。",
            InvalidHint: "请填入 yuanbao.tencent.com 请求头 Cookie: 后面的完整值。")
    ];

    public override IReadOnlyList<IParseProvider> CreateProviders(PluginConfig config)
    {
        return [new WeixinChannelsParseProvider(new WeixinChannelsParser(config))];
    }

    public IProviderMessageHandler? CreateMessageHandler(ProviderMessageHandlerContext context)
    {
        return new WeixinChannelsMessageHandler(context);
    }

    public bool LooksLikeCookie(string cookie) => WeixinChannelsParser.LooksLikeYuanbaoCookie(cookie);

    public bool IsAutoParseEnabled(PluginConfig config) => config.AutoParseWeixinChannelsLinks;

    public bool IsPluginResultMessage(string text)
    {
        return text.StartsWith("微信视频号解析", StringComparison.OrdinalIgnoreCase)
               || text.StartsWith("视频号解析", StringComparison.OrdinalIgnoreCase);
    }

    public IReadOnlyList<ProviderCommandDescriptor> CreateCommands(ProviderCommandContext context)
    {
        return
        [
            new ProviderCommandDescriptor("#wx-cookie", message => HandleCookieWriteAsync(context, message), AdminOnly: true),
        ];
    }

    private static async Task HandleCookieWriteAsync(ProviderCommandContext context, IncomingMessage message)
    {
        var text = GetPlainText(message).Trim();
        var cookie = text.Length <= "#wx-cookie".Length ? string.Empty : text["#wx-cookie".Length..].Trim();
        if (string.IsNullOrWhiteSpace(cookie))
        {
            await context.BotContext.Message.ReplyAsync(message, "用法：#wx-cookie <腾讯元宝网页 Cookie>\n请私信机器人发送，不要在群里发送 Cookie。");
            return;
        }

        cookie = NormalizeCookieInput(cookie);
        if (!WeixinChannelsParser.LooksLikeYuanbaoCookie(cookie))
        {
            await context.BotContext.Message.ReplyAsync(message, "Cookie 格式看起来不正确。请复制 yuanbao.tencent.com 请求头里 Cookie: 后面的完整内容。");
            return;
        }

        var path = context.HostServices.ResolveCookiePath("weixinchannels-yuanbao.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, cookie, Encoding.UTF8).ConfigureAwait(false);
        MyParserRuntime.WeixinChannelsYuanbaoCookie = cookie;
        await context.BotContext.Message.ReplyAsync(message, "微信视频号腾讯元宝 Cookie 已写入：cookies/weixinchannels-yuanbao.txt\n现在可以解析 weixin.qq.com/sph/... 链接。");
    }

    private static string NormalizeCookieInput(string cookie)
    {
        cookie = cookie.Trim();
        return cookie.StartsWith("Cookie:", StringComparison.OrdinalIgnoreCase) ? cookie[7..].Trim() : cookie;
    }

    private static string GetPlainText(IncomingMessage message) => message.GetPlainText();
}
