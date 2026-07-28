using MyParser.Provider.WeixinChannels.Infrastructure;
using MyParser.Provider.WeixinChannels.Models;
using MyParser.Provider.WeixinChannels.Utilities;
using ShiroBot.SDK.Models;

namespace MyParser.Provider.WeixinChannels.Parsing;

public sealed class WeixinChannelsParseProvider(WeixinChannelsParser parser) : IParseProviderWithParser, IProviderPriority, IDisposable
{
    public WeixinChannelsParser Parser { get; } = parser;
    public object ParserObject => Parser;

    public string Id => WeixinChannelsConstants.ProviderId;
    public string Name => WeixinChannelsConstants.DisplayName;
    public int Priority => 62;

    public bool CanHandle(string text) => WeixinChannelsParser.ContainsWeixinChannelsUrl(text);

    public async Task<MediaParseResult> ParseAsync(string text, CancellationToken cancellationToken = default)
    {
        var result = await Parser.ParseAsync(text, cancellationToken).ConfigureAwait(false);
        return new MediaParseResult
        {
            ProviderId = Id,
            ProviderName = Name,
            MediaId = result.ObjectId ?? result.ExportId ?? result.SphId,
            SourceUrl = result.ShareUrl,
            Title = result.Title,
            AuthorName = result.AuthorName,
            AuthorId = null,
            CoverUrl = result.CoverUrl,
            MusicUrl = null,
            Tags = [],
            IsGallery = false,
            IsVideo = !string.IsNullOrWhiteSpace(result.VideoUrl),
            ProviderPayload = result,
        };
    }

    public void Dispose() => Parser.Dispose();
}
