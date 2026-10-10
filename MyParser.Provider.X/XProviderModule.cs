using MyParser.Provider.X.Parsing;

namespace MyParser.Provider.X;

[MyParserProvider("x")]
public sealed class XProviderModule : MyParserProviderModuleBase, IProviderAutoParsePolicy, IProviderResultMessageClassifier, IProviderMediaCardRendererFactory
{
    public override string Id => "x";
    public override string DisplayName => "X (Twitter)";
    public override string? Description => "X (Twitter) 推文解析：视频、动图、图集与纯文本。";
    public override IReadOnlyList<string> Tags => ["twitter"];

    public override IReadOnlyList<IParseProvider> CreateProviders(PluginConfig config) => [new XParseProvider(new XParser(config))];

    public IProviderMediaCardRenderer CreateMediaCardRenderer(ProviderCardRenderContext context) => new XMediaCardRenderer(context);

    public bool IsAutoParseEnabled(PluginConfig config) => config.AutoParseXLinks;

    public bool IsPluginResultMessage(string text)
    {
        return text.StartsWith("X 推文", StringComparison.OrdinalIgnoreCase)
               || text.StartsWith("X (Twitter)", StringComparison.OrdinalIgnoreCase)
               || text.StartsWith("Twitter 推文", StringComparison.OrdinalIgnoreCase);
    }
}
