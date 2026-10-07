using MyParser.Provider.Douyin.Models;
using MyParser.Provider.Douyin.Infrastructure;
using Shirobot.Plugin.MyParser.Parsing;
using MyParser.Provider.Douyin.Parsing;
namespace MyParser.Provider.Douyin.Parsing;

public sealed class DouyinParseProvider(DouyinParser parser) : IParseProviderWithParser, IProviderPriority, IDisposable
{
    public DouyinParser Parser { get; } = parser;
    public object ParserObject => Parser;

    public string Id => "douyin";
    public string Name => "抖音";
    public int Priority => 10;

    public bool CanHandle(string text) => DouyinParser.ContainsDouyinUrl(text);

    public async Task<ParsedMedia> ParseAsync(string text, CancellationToken cancellationToken = default)
    {
        var result = await Parser.ParseAsync(text, cancellationToken);
        if (result.IsIgnored)
        {
            return new ParsedMedia
            {
                ProviderId = Id,
                ProviderName = Name,
                MediaId = result.AwemeId,
                Kind = ParsedMediaKind.Other,
                SourceUrl = result.SourceUrl,
                Title = result.Title,
            };
        }

        return new ParsedMedia
        {
            ProviderId = Id,
            ProviderName = Name,
            MediaId = result.AwemeId,
            SourceUrl = result.SourceUrl,
            Title = result.Title,
            AuthorName = result.AuthorName,
            AuthorId = result.AuthorId,
            CoverUrl = result.CoverUrl,
            MusicUrl = result.MusicUrl,
            Tags = result.Tags,
            Kind = result.IsGallery ? ParsedMediaKind.Gallery : result.IsVideo ? ParsedMediaKind.Video : ParsedMediaKind.Other,
            Assets = result.Qualities.Select(quality => new MediaAsset
                { Kind = MediaAssetKind.Video, Url = quality.Url, Label = quality.Label, Referer = result.SourceUrl, CacheKey = $"douyin:{result.AwemeId}:{quality.GearName}:{quality.Ratio}:{quality.Codec}", QualityId = quality.BitRate, Width = quality.Width, Height = quality.Height, FrameRate = quality.Fps, Codec = quality.Codec, FileNamePrefix = "douyin", DownloadDirectory = MyParserRuntime.DownloadDirectory, RequestHeaders = CreateMediaHeaders(result.SourceUrl) })
                .Concat(result.Images.Select(image => new MediaAsset { Kind = MediaAssetKind.Image, Url = image.Url, Referer = result.SourceUrl }))
                .Concat(result.IsGallery && !string.IsNullOrWhiteSpace(result.MusicUrl)
                    ? [new MediaAsset
                    {
                        Kind = MediaAssetKind.Audio,
                        Url = result.MusicUrl,
                        Label = result.MusicTitle,
                        CacheKey = $"douyin:{result.AwemeId}:gallery-music",
                        FileNamePrefix = "douyin-gallery-music",
                        DownloadDirectory = MyParserRuntime.DownloadDirectory,
                        RequestHeaders = CreateMediaHeaders(result.SourceUrl),
                    }]
                    : [])
                .ToArray(),
            Attributes = new Dictionary<string, string>
            {
                ["duration_ms"] = result.DurationMilliseconds.ToString(), ["likes"] = result.LikeCount.ToString(),
                ["comments"] = result.CommentCount.ToString(), ["shares"] = result.ShareCount.ToString(), ["plays"] = result.PlayCount.ToString(),
                ["music_title"] = result.MusicTitle ?? string.Empty, ["music_author"] = result.MusicAuthor ?? string.Empty,
                ["author_avatar_url"] = result.AuthorAvatarUrl ?? string.Empty,
                ["author_followers"] = result.AuthorFollowerCount.ToString(), ["author_region"] = result.AuthorRegion ?? string.Empty,
                ["create_time"] = result.CreateTimeUnixSeconds.ToString(), ["collects"] = result.CollectCount.ToString(),
            },
            Content = result.Comments.Select(comment => new MediaContentBlock
            {
                Kind = MediaContentKind.Text,
                Text = comment.Text,
                Caption = comment.UserName,
                Attributes = new Dictionary<string, string>
                {
                    ["category"] = "comment",
                    ["user_id"] = comment.DisplayUserId ?? comment.UserId ?? string.Empty,
                    ["ip"] = comment.IpLabel ?? string.Empty,
                    ["likes"] = comment.LikeCount.ToString(),
                    ["replies"] = comment.ReplyCount.ToString(),
                    ["created"] = comment.CreateTimeUnixSeconds.ToString(),
                    ["is_author"] = comment.IsAuthor.ToString(),
                    ["avatar_url"] = comment.UserAvatarUrl ?? string.Empty,
                    ["image_url"] = comment.ImageUrls.FirstOrDefault() ?? string.Empty,
                },
            }).ToArray(),
        };
    }

    public void Dispose() => Parser.Dispose();

    private static IReadOnlyDictionary<string, string> CreateMediaHeaders(string? referer)
    {
        var headers = new Dictionary<string, string>
        {
            ["User-Agent"] = DouyinConstants.UserAgent,
            ["Accept"] = "application/json, text/plain, */*",
            ["Accept-Language"] = "zh-CN,zh;q=0.9,en;q=0.8",
            ["Referer"] = referer ?? DouyinConstants.HomeUrl,
        };
        if (!string.IsNullOrWhiteSpace(MyParserRuntime.DouyinCookie)) headers["Cookie"] = MyParserRuntime.DouyinCookie;
        return headers;
    }
}
