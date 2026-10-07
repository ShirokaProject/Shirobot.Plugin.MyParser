using MyParser.Provider.WeixinChannels.Infrastructure;
using MyParser.Provider.WeixinChannels.Models;
using MyParser.Provider.WeixinChannels.Parsing;
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

    public async Task<ParsedMedia> ParseAsync(string text, CancellationToken cancellationToken = default)
    {
        var result = await Parser.ParseAsync(text, cancellationToken).ConfigureAwait(false);
        return new ParsedMedia
        {
            ProviderId = Id,
            ProviderName = Name,
            MediaId = result.ObjectId ?? result.ExportId ?? result.SphId,
            SourceUrl = result.ShareUrl,
            Title = result.Title,
            AuthorName = result.AuthorName,
            AuthorId = null,
            CoverUrl = result.CoverUrl,
            Description = result.Description,
            Tags = [],
            Kind = !string.IsNullOrWhiteSpace(result.VideoUrl) ? ParsedMediaKind.Video : ParsedMediaKind.Other,
            Assets = new[] { result.VideoUrl, result.H264VideoUrl, result.H265VideoUrl, result.OriginVideoUrl }
                .Where(url => !string.IsNullOrWhiteSpace(url)).Distinct(StringComparer.Ordinal)
                .Select(url => new MediaAsset { Kind = MediaAssetKind.Video, Url = url!, Referer = result.ShareUrl, CacheKey = $"wxchannels:{result.SphId}:{result.ExportId}", FileNamePrefix = "wxchannels", DownloadDirectory = MyParserRuntime.WeixinChannelsDownloadDirectory, RequestHeaders = new Dictionary<string, string> { ["User-Agent"] = WeixinChannelsConstants.UserAgent, ["Referer"] = result.ShareUrl, ["Accept"] = "video/webm,video/mp4,video/*;q=0.9,*/*;q=0.8" } }).ToArray(),
            Attributes = new Dictionary<string, string>
            {
                ["duration_seconds"] = result.DurationSeconds.ToString(), ["file_size"] = result.FileSize?.ToString() ?? string.Empty,
                ["likes"] = result.LikeCountText ?? string.Empty, ["favorites"] = result.FavoriteCountText ?? string.Empty,
                ["comments"] = result.CommentCountText ?? string.Empty, ["shares"] = result.ForwardCountText ?? string.Empty,
                ["author_avatar_url"] = result.AuthorAvatarUrl ?? string.Empty, ["publish_time"] = result.PublishTime?.ToString("O") ?? string.Empty,
            },
        };
    }

    public void Dispose() => Parser.Dispose();
}
