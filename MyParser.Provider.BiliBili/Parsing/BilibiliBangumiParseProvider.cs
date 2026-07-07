using Shirobot.Plugin.MyParser.Parsing;
using MyParser.Provider.BiliBili.Services;
using MyParser.Provider.BiliBili.Models;
using MyParser.Provider.BiliBili.Utilities;

namespace MyParser.Provider.BiliBili.Parsing;

public sealed class BilibiliBangumiParseProvider(BilibiliBangumiParser parser) : IProviderParseTextMatcher, IParseProvider, IProviderPriority
{
    public BilibiliBangumiParser Parser { get; } = parser;

    public string Id => "bilibili-bangumi";
    public string Name => "Bilibili 番剧";
    public int Priority => 30;

    public bool CanHandle(string text)
    {
        return BilibiliUrlParser.ExtractStrictBilibiliUrl(text) is not null
               && BilibiliUrlParser.ExtractBangumiIds(text).HasAny;
    }

    public string? TryNormalizeParseText(string text, ProviderParseTextContext context)
    {
        if (context.IsUrlLike)
        {
            return BilibiliUrlParser.ExtractStrictBilibiliUrl(text);
        }

        return context.IsAutoParse ? null : BilibiliUrlParser.NormalizeStandaloneBangumiId(text);
    }

    public async Task<MediaParseResult> ParseAsync(string text, CancellationToken cancellationToken = default)
    {
        var result = await Parser.ParseAsync(text, cancellationToken);
        return result switch
        {
            BilibiliBangumiEpisodeVideoParseResult episode => new MediaParseResult
            {
                ProviderId = Id,
                ProviderName = Name,
                MediaId = episode.Video.Bvid,
                SourceUrl = episode.Video.SourceUrl,
                Title = episode.Video.Title,
                AuthorName = episode.Video.AuthorName,
                AuthorId = episode.Video.AuthorId,
                CoverUrl = episode.Video.CoverUrl,
                MusicUrl = null,
                Tags = episode.Bangumi.Styles.ToList(),
                IsGallery = false,
                IsVideo = episode.Video.IsVideo,
                ProviderPayload = episode,
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
            BilibiliBangumiParseResult bangumi => new MediaParseResult
            {
                ProviderId = Id,
                ProviderName = Name,
                MediaId = bangumi.MediaId?.ToString() ?? bangumi.SeasonId?.ToString() ?? bangumi.RequestedEpId?.ToString() ?? string.Empty,
                SourceUrl = bangumi.MediaUrl ?? bangumi.SeasonUrl,
                Title = bangumi.Title,
                AuthorName = "Bilibili 番剧",
                AuthorId = null,
                CoverUrl = bangumi.CoverUrl,
                MusicUrl = null,
                Tags = bangumi.Styles.ToList(),
                IsGallery = true,
                IsVideo = false,
                ProviderPayload = bangumi,
            },
            _ => throw new BilibiliParseException("Bilibili 番剧解析返回了未知结果类型。"),
        };
    }
}
