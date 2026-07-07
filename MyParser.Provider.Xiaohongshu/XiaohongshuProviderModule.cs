using Shirobot.Plugin.MyParser.Parsing;
using MyParser.Provider.Xiaohongshu.Parsing;
using MyParser.Provider.Xiaohongshu.MessageHandling;
using ShiroBot.Model.Common;

namespace MyParser.Provider.Xiaohongshu;

[MyParserProvider("xiaohongshu")]
public sealed class XiaohongshuProviderModule : MyParserProviderModuleBase, IProviderMessageHandlerFactory, IProviderCookieStore, IProviderAutoParsePolicy, IProviderResultMessageClassifier, IProviderCommandContributor
{
    public override string Id => "xiaohongshu";

    public override string DisplayName => "小红书";

    public IReadOnlyList<ProviderCookieDescriptor> CookieDescriptors =>
    [
        new(
            Id,
            DisplayName,
            "xiaohongshu.txt",
            cookie => MyParserRuntime.XiaohongshuCookie = cookie,
            XiaohongshuParser.LooksLikeLoginCookie,
            EmptyHint: "可发送 #xhs-login 扫码登录，或编辑文件后重启。",
            InvalidHint: "请确保文件内容包含 web_session 和 a1。")
    ];

    public override IReadOnlyList<IParseProvider> CreateProviders(PluginConfig config)
    {
        return [new XiaohongshuParseProvider(new XiaohongshuParser(config))];
    }

    public IProviderMessageHandler? CreateMessageHandler(ProviderMessageHandlerContext context)
    {
        return new XiaohongshuMessageHandler(context.BotContext, context.Config, context.ProviderRegistry, context.PrimaryProvider, context.HostServices);
    }

    public bool IsAutoParseEnabled(PluginConfig config) => config.AutoParseXiaohongshuLinks;

    public bool IsPluginResultMessage(string text)
    {
        return text.StartsWith("小红书解析", StringComparison.OrdinalIgnoreCase)
               || text.StartsWith("Xiaohongshu", StringComparison.OrdinalIgnoreCase);
    }

    public IReadOnlyList<ProviderCommandDescriptor> CreateCommands(ProviderCommandContext context)
    {
        return
        [
            new ProviderCommandDescriptor("#xhs-login", message => HandleLoginAsync(context, message), AdminOnly: true),
            new ProviderCommandDescriptor("#xhs-cookie-check", message => HandleCookieCheckAsync(context, message), AdminOnly: true),
        ];
    }

    private static Task HandleLoginAsync(ProviderCommandContext context, IncomingMessage message)
    {
        return context.MessageHandler?.HandleLoginAsync(message)
               ?? context.BotContext.Message.ReplyAsync(message, "小红书解析器尚未初始化或不支持登录。");
    }

    private static async Task HandleCookieCheckAsync(ProviderCommandContext context, IncomingMessage message)
    {
        if (context.PrimaryProvider is not IProviderLoginStatusProvider loginStatusProvider)
        {
            await context.BotContext.Message.ReplyAsync(message, "小红书解析器尚未初始化或不支持 Cookie 检查。");
            return;
        }

        var status = await loginStatusProvider.CheckLoginStatusAsync();
        var detail = status.IsLogin
            ? $"有效 / 已登录：{status.UserName ?? "未知用户"}" + (string.IsNullOrWhiteSpace(status.UserId) ? string.Empty : $" ({status.UserId})")
            : status.NeedVerify ? "触发安全验证：" + status.Message : "无效 / 未登录：" + status.Message;
        await context.BotContext.Message.ReplyAsync(message, "小红书Cookie 状态：" + detail);
    }
}
