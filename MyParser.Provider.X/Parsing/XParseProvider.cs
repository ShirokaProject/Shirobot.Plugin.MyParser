using MyParser.Provider.X.Models;

namespace MyParser.Provider.X.Parsing;

internal sealed class XParseProvider(XParser parser) : IParseProvider, IParseProviderWithParser, IDisposable
{
    private static readonly IReadOnlyDictionary<string, string> MediaRequestHeaders =
        new Dictionary<string, string>
        {
            ["User-Agent"] = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0 Safari/537.36",
        };

    public string Id => "x";
    public string Name => "X (Twitter)";

    public object ParserObject => parser;

    public bool CanHandle(string text) => XUrlExtractor.TryExtractStatusId(text, out _);

    public void Dispose() => parser.Dispose();

    public async Task<ParsedMedia> ParseAsync(string text, CancellationToken cancellationToken = default)
    {
        var tweet = await parser.ParseAsync(text, cancellationToken).ConfigureAwait(false);
        return BuildMedia(tweet);
    }

    private static ParsedMedia BuildMedia(XTweetData tweet)
    {
        var videos = tweet.Media.Where(media => media.Kind is "video" or "gif").ToArray();
        var photos = tweet.Media.Where(media => media.Kind == "photo").ToArray();
        var screenName = tweet.ScreenName;
        var sourceUrl = string.IsNullOrWhiteSpace(screenName)
            ? $"https://x.com/i/status/{tweet.Id}"
            : $"https://x.com/{screenName}/status/{tweet.Id}";

        var attributes = BuildAttributes(tweet);
        var downloadDirectory = MyParserRuntime.XDownloadDirectory;
        var cacheKey = $"x:{tweet.Id}";

        if (videos.Length > 0)
        {
            var item = videos[0];
            var variants = item.Variants.Where(variant =>
                    variant.ContentType.Contains("mp4", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(variant => variant.Bitrate)
                .ToArray();
            if (variants.Length == 0)
            {
                throw new InvalidOperationException("X 推文视频没有可用的 MP4 地址。");
            }

            var primary = variants[0];
            var assets = new List<MediaAsset>
            {
                new()
                {
                    Kind = MediaAssetKind.Video,
                    Url = primary.Url,
                    BackupUrls = variants.Skip(1).Select(variant => variant.Url).ToArray(),
                    CacheKey = cacheKey,
                    FileNamePrefix = "x",
                    DownloadDirectory = downloadDirectory,
                    FileExtension = "mp4",
                    Width = item.Width,
                    Height = item.Height,
                    QualityId = primary.Bitrate,
                    Codec = "H264",
                    Label = $"X {item.Height}p",
                    Referer = "https://x.com/",
                    RequestHeaders = MediaRequestHeaders,
                },
            };
            assets.AddRange(BuildPhotoAssets(photos, cacheKey, downloadDirectory));

            return new ParsedMedia
            {
                ProviderId = "x",
                ProviderName = "X (Twitter)",
                MediaId = tweet.Id,
                SourceUrl = sourceUrl,
                Kind = ParsedMediaKind.Video,
                Title = tweet.AuthorName,
                AuthorName = "@" + screenName,
                Description = tweet.Text,
                CoverUrl = !string.IsNullOrWhiteSpace(item.ThumbnailUrl)
                    ? item.ThumbnailUrl
                    : photos.FirstOrDefault()?.Url ?? item.Url,
                Attributes = attributes,
                Assets = assets,
            };
        }

        if (photos.Length > 0)
        {
            var assets = BuildPhotoAssets(photos, cacheKey, downloadDirectory);

            return new ParsedMedia
            {
                ProviderId = "x",
                ProviderName = "X (Twitter)",
                MediaId = tweet.Id,
                SourceUrl = sourceUrl,
                Kind = ParsedMediaKind.Gallery,
                Title = tweet.AuthorName,
                AuthorName = "@" + screenName,
                Description = tweet.Text,
                CoverUrl = photos[0].Url,
                Attributes = attributes,
                Assets = assets,
            };
        }

        return new ParsedMedia
        {
            ProviderId = "x",
            ProviderName = "X (Twitter)",
            MediaId = tweet.Id,
            SourceUrl = sourceUrl,
            Kind = ParsedMediaKind.Article,
            Title = tweet.AuthorName,
            AuthorName = "@" + screenName,
            Description = tweet.Text,
            CoverUrl = !string.IsNullOrWhiteSpace(screenName) ? tweet.AvatarUrl : null,
            Attributes = attributes,
            Content =
            [
                new MediaContentBlock { Kind = MediaContentKind.Heading, Text = $"{tweet.AuthorName} (@{screenName})" },
                new MediaContentBlock { Kind = MediaContentKind.Text, Text = tweet.Text },
                new MediaContentBlock { Kind = MediaContentKind.Text, Text = BuildTextFooter(tweet, sourceUrl) },
            ],
        };
    }

    private static List<MediaAsset> BuildPhotoAssets(
        IReadOnlyList<XMediaItem> photos,
        string cacheKey,
        string downloadDirectory)
    {
        var assets = new List<MediaAsset>(photos.Count);
        for (var index = 0; index < photos.Count; index++)
        {
            var photo = photos[index];
            assets.Add(new MediaAsset
            {
                Kind = MediaAssetKind.Image,
                Url = photo.Url ?? string.Empty,
                CacheKey = $"{cacheKey}:{index}",
                FileNamePrefix = "x_image",
                DownloadDirectory = downloadDirectory,
                FileExtension = "jpg",
                Width = photo.Width,
                Height = photo.Height,
                Referer = "https://x.com/",
                RequestHeaders = MediaRequestHeaders,
            });
        }

        return assets;
    }

    private static Dictionary<string, string> BuildAttributes(XTweetData tweet)
    {
        var attributes = new Dictionary<string, string>();
        if (tweet.Likes is > 0) attributes["点赞"] = tweet.Likes.Value.ToString();
        if (tweet.Retweets is > 0) attributes["转推"] = tweet.Retweets.Value.ToString();
        if (tweet.Replies is > 0) attributes["回复"] = tweet.Replies.Value.ToString();
        if (tweet.Views is > 0) attributes["浏览"] = tweet.Views.Value.ToString();
        if (tweet.CreatedAt is { } created) attributes["发布于"] = created.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
        if (!string.IsNullOrWhiteSpace(tweet.AvatarUrl)) attributes["author_avatar_url"] = tweet.AvatarUrl;
        if (!string.IsNullOrWhiteSpace(tweet.ScreenName)) attributes["screen_name"] = tweet.ScreenName;
        attributes["blue_verified"] = tweet.IsBlueVerified ? "1" : "0";
        return attributes;
    }

    private static string BuildTextFooter(XTweetData tweet, string sourceUrl)
    {
        var parts = new List<string>();
        if (tweet.Likes is > 0) parts.Add($"赞 {tweet.Likes.Value}");
        if (tweet.Retweets is > 0) parts.Add($"转 {tweet.Retweets.Value}");
        if (tweet.Views is > 0) parts.Add($"浏览 {tweet.Views.Value}");
        return parts.Count == 0 ? sourceUrl : $"{string.Join(" · ", parts)}  {sourceUrl}";
    }
}
