using MyParser.Provider.Heybox.MessageHandling;
using MyParser.Provider.Heybox.Parsing;

namespace MyParser.Provider.Heybox;

[MyParserProvider("heybox")]
public sealed class HeyboxProviderModule : MyParserProviderModuleBase, IProviderMessageHandlerFactory, ICookieValidator, IProviderCookieStore, IProviderAutoParsePolicy, IProviderResultMessageClassifier
{
    public override string Id => "heybox";

    public override string DisplayName => "小黑盒";

    public IReadOnlyList<ProviderCookieDescriptor> CookieDescriptors =>
    [
        new(
            Id,
            DisplayName,
            "heybox.txt",
            cookie => MyParserRuntime.HeyboxCookie = cookie,
            LooksLikeCookie,
            EmptyHint: "可编辑 cookies/heybox.txt 后重启或等待热重载；未配置 Cookie 时会以游客态解析。",
            InvalidHint: "请确保文件内容是小黑盒网页或接口请求头 Cookie: 后面的完整值。")
    ];

    public override IReadOnlyList<IParseProvider> CreateProviders(PluginConfig config)
    {
        if (!config.EnableHeybox)
        {
            return [];
        }

        return [new HeyboxParseProvider(new HeyboxParser(config))];
    }

    public IProviderMessageHandler? CreateMessageHandler(ProviderMessageHandlerContext context)
    {
        return new HeyboxMessageHandler(context);
    }

    public bool IsAutoParseEnabled(PluginConfig config) => config.EnableHeybox && config.AutoParseHeyboxLinks;

    public bool LooksLikeCookie(string cookie) => HeyboxParser.LooksLikeCookie(cookie);

    public bool IsPluginResultMessage(string text)
    {
        return text.StartsWith("小黑盒解析", StringComparison.OrdinalIgnoreCase)
               || text.StartsWith("Heybox 解析", StringComparison.OrdinalIgnoreCase);
    }
}
