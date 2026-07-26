using Shirobot.Plugin.MyParser.Parsing;
using Shirobot.Plugin.MyParser.Providers.Bilibili.Models;
using Shirobot.Plugin.MyParser.Providers.Bilibili.Utilities;
using ShiroBot.SDK.Models;

namespace Shirobot.Plugin.MyParser.Providers.Bilibili.Facade;

internal sealed class BilibiliParseProvider(BilibiliParser parser) : IIncomingMessageParseProvider, IDisposable
{
    public BilibiliParser Parser { get; } = parser;

    public string Id => "bilibili";
    public string Name => "Bilibili";

    public bool CanHandle(string text)
    {
        return BilibiliUrlParser.ExtractBvid(text) is not null
               || BilibiliUrlParser.ExtractAid(text) is not null
               || BilibiliUrlParser.ExtractB23Url(text) is not null;
    }

    public string? ExtractParseText(MessageEvent message)
    {
        var text = BilibiliLightAppUrlExtractor.ExtractParseText(message);
        return BilibiliUrlParser.ExtractStrictBilibiliUrl(text ?? string.Empty);
    }

    public async Task<MediaParseResult> ParseAsync(string text, CancellationToken cancellationToken = default)
    {
        var result = await Parser.ParseMediaAsync(text, cancellationToken);
        return result switch
        {
            BilibiliMultiPageParseResult multi => new MediaParseResult
            {
                ProviderId = Id,
                ProviderName = Name,
                MediaId = multi.Bvid,
                SourceUrl = multi.SourceUrl,
                Title = multi.Title,
                AuthorName = multi.AuthorName,
                AuthorId = multi.AuthorId,
                CoverUrl = multi.CoverUrl,
                MusicUrl = null,
                Tags = [],
                IsGallery = true,
                IsVideo = false,
                ProviderPayload = multi,
            },
            BilibiliParseResult video => new MediaParseResult
            {
                ProviderId = Id,
                ProviderName = Name,
                MediaId = video.Bvid,
                SourceUrl = video.SourceUrl,
                Title = video.Title,
                AuthorName = video.AuthorName,
                AuthorId = video.AuthorId,
                CoverUrl = video.CoverUrl,
                MusicUrl = null,
                Tags = [],
                IsGallery = false,
                IsVideo = video.IsVideo,
                ProviderPayload = video,
            },
            _ => throw new BilibiliParseException("Bilibili 视频解析返回了未知结果类型。"),
        };
    }

    public void Dispose() => Parser.Dispose();
}
