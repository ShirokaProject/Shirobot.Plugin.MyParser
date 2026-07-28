using MyParser.Provider.Heybox.Models;
using MyParser.Provider.Heybox.Utilities;
using ShiroBot.SDK.Models;

namespace MyParser.Provider.Heybox.Parsing;

public sealed class HeyboxParseProvider(HeyboxParser parser) : IIncomingMessageParseProvider, IParseProviderWithParser, IProviderPriority, IDisposable
{
    public HeyboxParser Parser { get; } = parser;
    public object ParserObject => Parser;

    public string Id => "heybox";
    public string Name => "小黑盒";
    public int Priority => 60;

    public bool CanHandle(string text) => HeyboxParser.ContainsHeyboxUrl(text);

    public string? ExtractParseText(IncomingMessage message)
    {
        return HeyboxLightAppUrlExtractor.ExtractParseText(message);
    }

    public async Task<MediaParseResult> ParseAsync(string text, CancellationToken cancellationToken = default)
    {
        var result = await Parser.ParseAsync(text, cancellationToken);
        return new MediaParseResult
        {
            ProviderId = Id,
            ProviderName = Name,
            MediaId = result.LinkId,
            SourceUrl = result.SourceUrl,
            Title = result.Title,
            AuthorName = result.AuthorName,
            AuthorId = result.AuthorId,
            CoverUrl = result.CoverUrl,
            MusicUrl = null,
            Tags = [],
            IsGallery = result.ImageUrls.Count > 0,
            IsVideo = result.VideoUrls.Count > 0,
            ProviderPayload = result,
        };
    }

    public void Dispose() => Parser.Dispose();
}
