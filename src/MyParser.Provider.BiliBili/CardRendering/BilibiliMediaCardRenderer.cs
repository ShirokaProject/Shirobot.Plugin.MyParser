using Shirobot.Plugin.MyParser.CardRendering;
using Avalonia.Media.Imaging;
using System.Collections.Concurrent;
using System.Globalization;
using MyParser.Provider.BiliBili.Views;
using ShiroBot.AvaloniaSdk;
using ShiroBot.SDK.Plugin;
using Shirobot.Plugin.MyParser.Parsing;

namespace MyParser.Provider.BiliBili;

internal sealed class BilibiliMediaCardRenderer(ProviderCardRenderContext context) : IProviderMediaCardRenderer
{
    public async Task<string?> RenderAsync(ParsedMedia media, ProviderCardPurpose purpose, CancellationToken cancellationToken = default)
    {
        if (purpose == ProviderCardPurpose.Article && media.Kind == ParsedMediaKind.Article)
        {
            return await RenderArticleAsync(media, cancellationToken).ConfigureAwait(false);
        }

        if (purpose == ProviderCardPurpose.Comments)
        {
            return await RenderCommentsAsync(media, cancellationToken).ConfigureAwait(false);
        }

        if (purpose == ProviderCardPurpose.Main && media.Kind == ParsedMediaKind.Live)
        {
            return await RenderLiveAsync(media, cancellationToken).ConfigureAwait(false);
        }

        if (purpose != ProviderCardPurpose.Main || media.Kind is not (ParsedMediaKind.Video or ParsedMediaKind.Collection)) return null;

        var coverTask = LoadImageAsync(media.CoverUrl, media, "cover", cancellationToken);
        var avatarTask = media.ProviderId == "bilibili-bangumi"
            ? Task.FromResult<Bitmap?>(null)
            : LoadVideoAvatarAsync(media, cancellationToken);
        var pendantTask = media.ProviderId == "bilibili-bangumi"
            ? Task.FromResult<Bitmap?>(null)
            : LoadOptionalImageAsync(Value(media, "author_pendant_url"), media, "video_pendant", cancellationToken, avatarSize: PendantSize);
        try
        {
            await Task.WhenAll(coverTask, avatarTask, pendantTask).ConfigureAwait(false);
            var cover = await coverTask.ConfigureAwait(false);
            var avatar = await avatarTask.ConfigureAwait(false);

            if (media.ProviderId == "bilibili-bangumi")
            {
                var model = CreateBangumiViewModel(media, cover, MediaCardViewModel.BilibiliBangumiAvatar);
                return await context.HostServices.RenderCardUriAsync<MediaCard>(model, media.ProviderId, media.MediaId, cancellationToken).ConfigureAwait(false);
            }

            var viewModel = CreateVideoViewModel(media, cover, avatar, await pendantTask.ConfigureAwait(false));
            return await context.HostServices.RenderCardUriAsync<MediaCard>(viewModel, media.ProviderId, media.MediaId, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (coverTask.IsCompletedSuccessfully) coverTask.Result?.Dispose();
            if (avatarTask.IsCompletedSuccessfully) avatarTask.Result?.Dispose();
            if (pendantTask.IsCompletedSuccessfully) pendantTask.Result?.Dispose();
        }
    }

    /// <summary>头像框按 B 站官方比例显示为头像（32）的 1.75 倍。</summary>
    private const double PendantSize = 56;

    private async Task<string?> RenderCommentsAsync(ParsedMedia media, CancellationToken cancellationToken)
    {
        var blocks = media.Content.Where(block => block.Attributes.GetValueOrDefault("category") == "comment").Take(30).ToArray();
        if (blocks.Length == 0) return null;
        var bitmaps = new ConcurrentBag<Bitmap>();
        try
        {
            var coverTask = LoadOptionalImageAsync(media.CoverUrl, media, "cover", cancellationToken);
            var comments = await CommentCardLoader.LoadItemsAsync(blocks, bitmaps,
                (url, name, size, token) => LoadOptionalImageAsync(url, media, name, token, size), cancellationToken).ConfigureAwait(false);
            var cover = await coverTask.ConfigureAwait(false);
            if (cover is not null) bitmaps.Add(cover);

            var total = Value(media, "replies");
            var model = new CommentCardViewModel
            {
                Platform = MediaCardPlatform.Bilibili,
                Cover = cover,
                SourceTitle = media.Title ?? "Bilibili 视频",
                CountText = $"热评 {comments.Count}",
                SourceMeta = long.TryParse(total, out var totalCount) && totalCount > 0
                    ? $"共 {FormatCount(total)} 条评论" : string.Empty,
                Comments = comments,
            };
            return await context.HostServices.RenderCardUriAsync<CommentCard>(model, media.ProviderId, media.MediaId, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            foreach (var bitmap in bitmaps) bitmap.Dispose();
        }
    }

    /// <summary>图文动态：帖子样式卡片，图片按宫格尺寸解码（多图裁成方块），最多 9 张。</summary>
    private async Task<string> RenderDynamicAsync(ParsedMedia media, CancellationToken cancellationToken)
    {
        var bitmaps = new ConcurrentBag<Bitmap>();
        using var slots = new SemaphoreSlim(4);
        var imageUrls = media.Content.Where(block => block.Kind == MediaContentKind.Image && !string.IsNullOrWhiteSpace(block.Url))
            .Select(block => block.Url!).ToArray();
        var tileSize = BiliDynamicCardViewModel.TileSizeFor(imageUrls.Length);

        async Task<Bitmap?> LoadAsync(string? url, string name, double avatarSize)
        {
            if (string.IsNullOrWhiteSpace(url)) return null;
            await slots.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var bitmap = await LoadOptionalImageAsync(url, media, name, cancellationToken, avatarSize).ConfigureAwait(false);
                if (bitmap is not null) bitmaps.Add(bitmap);
                return bitmap;
            }
            finally { slots.Release(); }
        }

        try
        {
            var avatarTask = LoadAsync(Value(media, "author_avatar_url"), "dynamic_avatar", 40);
            var pendantTask = LoadAsync(Value(media, "author_pendant_url"), "dynamic_pendant", 70);
            // 单图保持原比例整张解码；多图按格子边长裁成方块解码，省内存。
            var imagesTask = Task.WhenAll(imageUrls.Take(BiliDynamicCardViewModel.MaxTiles)
                .Select((url, index) => LoadAsync(url, $"dynamic_{index:D2}", imageUrls.Length == 1 ? 0 : tileSize)));
            await Task.WhenAll(avatarTask, pendantTask, imagesTask).ConfigureAwait(false);

            var time = DateTimeOffset.TryParse(Value(media, "publish_time"), CultureInfo.InvariantCulture, DateTimeStyles.None, out var published)
                ? published.ToOffset(TimeSpan.FromHours(8)).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)
                : string.Empty;
            var official = Value(media, "author_official");
            var text = string.Join("\n", media.Content.Where(block => block.Kind != MediaContentKind.Image && !string.IsNullOrWhiteSpace(block.Text))
                .Select(block => block.Text!.Trim()));
            var model = new BiliDynamicCardViewModel
            {
                Avatar = await avatarTask.ConfigureAwait(false),
                AvatarPendant = await pendantTask.ConfigureAwait(false),
                AuthorName = string.IsNullOrWhiteSpace(media.AuthorName) ? "B 站用户" : media.AuthorName,
                AuthorMeta = string.Join(" · ", new[] { time, official }.Where(part => !string.IsNullOrWhiteSpace(part))),
                Text = text,
                Images = (await imagesTask.ConfigureAwait(false)).OfType<Bitmap>().ToArray(),
                ImageCount = imageUrls.Length,
                Metrics = DynamicMetrics(media),
            };
            return await context.HostServices.RenderCardUriAsync<BiliDynamicCard>(model, media.ProviderId, media.MediaId, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            foreach (var bitmap in bitmaps) bitmap.Dispose();
        }
    }

    /// <summary>动态底部数据：转发、评论、点赞；投币只有大于 0 时才显示（大多数动态不能投币，B 站也不展示）。</summary>
    internal static IReadOnlyList<MediaCardMetric> DynamicMetrics(ParsedMedia media)
    {
        var metrics = new List<MediaCardMetric>
        {
            new(MediaCardIcons.Share, "转发", FormatCount(Value(media, "shares"))),
            new(MediaCardIcons.Comment, "评论", FormatCount(Value(media, "replies"))),
            new(MediaCardIcons.ThumbUp, "点赞", FormatCount(Value(media, "likes"))),
        };
        if (long.TryParse(Value(media, "coins"), out var coins) && coins > 0)
            metrics.Add(new(MediaCardIcons.Coin, "投币", FormatCount(Value(media, "coins"))));
        return metrics;
    }

    private async Task<Bitmap?> LoadOptionalImageAsync(string? url, ParsedMedia media, string name, CancellationToken cancellationToken, double avatarSize = 0)
    {
        try
        {
            return await LoadImageAsync(url, media, name, cancellationToken, avatarSize).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            global::Shirobot.Plugin.MyParser.DiagnosticLog.Warning($"MyParser Bilibili 卡片图片加载失败: media_id={media.MediaId}, name={name}, error={ex.GetType().Name}");
            return null;
        }
    }

    private async Task<Bitmap?> LoadVideoAvatarAsync(ParsedMedia media, CancellationToken cancellationToken)
    {
        try
        {
            return await LoadImageAsync(Value(media, "author_avatar_url"), media, "video_avatar", cancellationToken, avatarSize: 32).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            global::Shirobot.Plugin.MyParser.DiagnosticLog.Warning($"MyParser Bilibili 视频头像加载失败: media_id={media.MediaId}, error={ex.GetType().Name}");
            return null;
        }
    }

    internal static MediaCardViewModel CreateVideoViewModel(ParsedMedia media, Bitmap? cover, Bitmap? avatar, Bitmap? pendant)
    {
        var duration = ParseDuration(media.Attributes.GetValueOrDefault("duration_seconds"));
        var hasMultiPages = int.TryParse(Value(media, "pages"), out var pageCount) && pageCount > 1;
        var currentPage = int.TryParse(Value(media, "requested_page"), out var page) ? Math.Clamp(page, 1, Math.Max(1, pageCount)) : 1;
        var badgeText = media.Kind == ParsedMediaKind.Collection
            ? $"共 {Value(media, "pages")} P · {duration}"
            : duration;
        return new MediaCardViewModel
        {
            Platform = MediaCardPlatform.Bilibili,
            Cover = cover,
            Avatar = avatar,
            BadgeText = badgeText,
            BadgeIcon = MediaCardIcons.Play,
            Title = media.Title ?? "Bilibili 视频",
            Meta = BuildVideoMeta(media),
            Description = media.Description ?? string.Empty,
            DescriptionMaxLines = 0,
            Tags = media.Tags.Where(tag => !string.IsNullOrWhiteSpace(tag))
                .Select(tag => "#" + tag.Trim().TrimStart('#')).ToArray(),
            Metrics =
            [
                new(MediaCardIcons.Play, "播放", FormatCount(Value(media, "views"))),
                new(MediaCardIcons.ThumbUp, "点赞", FormatCount(Value(media, "likes"))),
                new(MediaCardIcons.Coin, "投币", FormatCount(Value(media, "coins"))),
                new(MediaCardIcons.Star, "收藏", FormatCount(Value(media, "favorites"))),
            ],
            ExtraIcon = MediaCardIcons.Playlist,
            ExtraText = hasMultiPages ? $"当前解析 P{currentPage}/{pageCount}" : string.Empty,
            ExtraHint = hasMultiPages ? "10min 内回复数字解析对应 P" : string.Empty,
            AvatarRing = false,
            AvatarPendant = pendant,
            AuthorName = media.AuthorName ?? "未知 UP",
            AuthorMeta = FormatLocation(Value(media, "author_location")),
        };
    }

    /// <summary>番剧：与视频卡片同一版式，角标显示总集数，数据栏为播放 / 追番 / 评分。</summary>
    internal static MediaCardViewModel CreateBangumiViewModel(ParsedMedia media, Bitmap? cover, Bitmap? officialAvatar)
    {
        var count = int.TryParse(Value(media, "pages"), out var pages) ? Math.Max(1, pages) : 1;
        var current = int.TryParse(Value(media, "requested_page"), out var page) ? Math.Clamp(page, 1, count) : 1;
        var score = Value(media, "rating_score");
        var ratingCount = long.TryParse(Value(media, "rating_count"), out var raters) ? raters : 0;
        var metrics = new List<MediaCardMetric>
        {
            new(MediaCardIcons.Play, "播放", FormatCount(Value(media, "play_count"))),
            new(MediaCardIcons.Heart, "追番", FormatCount(Value(media, "follow_count"))),
            new(MediaCardIcons.Star, "评分", string.IsNullOrWhiteSpace(score) ? "暂无" : score),
        };
        if (ratingCount > 0) metrics.Add(new(MediaCardIcons.Comment, "评分人数", FormatCount(ratingCount.ToString(CultureInfo.InvariantCulture))));
        return new MediaCardViewModel
        {
            Platform = MediaCardPlatform.Bilibili,
            Cover = cover,
            BadgeText = $"全 {count} 集",
            BadgeIcon = MediaCardIcons.Playlist,
            Title = media.Title ?? "Bilibili 番剧",
            Meta = string.Join(" · ", new[] { Value(media, "episode_title"), Value(media, "publish") }
                .Where(value => !string.IsNullOrWhiteSpace(value))),
            Description = media.Description ?? string.Empty,
            DescriptionMaxLines = 5,
            Tags = media.Tags.Where(tag => !string.IsNullOrWhiteSpace(tag)).Select(tag => "#" + tag.Trim().TrimStart('#')).ToArray(),
            Metrics = metrics,
            ExtraIcon = MediaCardIcons.Playlist,
            ExtraText = count > 1 ? $"当前解析 第{current}/{count}集" : string.Empty,
            ExtraHint = count > 1 ? "10min 内回复数字解析对应集" : string.Empty,
            Avatar = officialAvatar,
            CompactAvatar = true,
            AuthorName = "哔哩哔哩番剧",
            AuthorMeta = string.Empty,
        };
    }

    /// <summary>直播：与视频卡片同一版式，角标显示直播状态与已播时长，作者位是主播。</summary>
    internal static MediaCardViewModel CreateLiveViewModel(ParsedMedia media, Bitmap? cover, Bitmap? avatar, Bitmap? pendant)
    {
        var live = Value(media, "live_status") == "1";
        var duration = TimeSpan.TryParse(Value(media, "duration"), CultureInfo.InvariantCulture, out var span) && span > TimeSpan.Zero
            ? MediaCardViewModel.FormatDuration(TimeSpan.FromSeconds(Math.Floor(span.TotalSeconds)))
            : string.Empty;
        var meta = new List<string> { "房间号 " + Value(media, "room_id") };
        var area = string.Join(" / ", new[] { Value(media, "parent_area"), Value(media, "area") }
            .Where(value => !string.IsNullOrWhiteSpace(value)).Distinct());
        if (!string.IsNullOrWhiteSpace(area)) meta.Add(area);
        if (live && DateTimeOffset.TryParse(Value(media, "live_start"), CultureInfo.InvariantCulture, DateTimeStyles.None, out var start))
            meta.Add(start.ToOffset(TimeSpan.FromHours(8)).ToString("MM-dd HH:mm", CultureInfo.InvariantCulture) + " 开播");

        // 未开播只剩「看过」一项，单独占一整栏太空，改为放进元信息行，不显示数据栏。
        var watched = FormatCount(Value(media, "watched_count"));
        var metrics = new List<MediaCardMetric>();
        if (live)
        {
            metrics.Add(new(MediaCardIcons.Play, "看过", watched));
            metrics.Add(new(MediaCardIcons.Fire, "高能用户", FormatCount(Value(media, "audience_count"))));
            metrics.Add(new(MediaCardIcons.Clock, "已播", string.IsNullOrWhiteSpace(duration) ? "--" : duration));
        }
        else if (watched != "--")
        {
            meta.Add(watched + "人看过");
        }

        return new MediaCardViewModel
        {
            Platform = MediaCardPlatform.Bilibili,
            Cover = cover,
            Avatar = avatar,
            BadgeText = live ? "直播中" : "未开播",
            BadgeIcon = live ? MediaCardIcons.Waveform : MediaCardIcons.Clock,
            Title = media.Title ?? "Bilibili 直播",
            Meta = string.Join(" · ", meta),
            Description = string.Empty,
            Tags = media.Tags.Where(tag => !string.IsNullOrWhiteSpace(tag)).Select(tag => "#" + tag.Trim().TrimStart('#')).ToArray(),
            Metrics = metrics,
            AvatarRing = false,
            AvatarPendant = pendant,
            AuthorName = string.IsNullOrWhiteSpace(media.AuthorName) ? "主播" : media.AuthorName,
            AuthorMeta = live ? "正在直播" : "主播",
        };
    }

    private async Task<string> RenderLiveAsync(ParsedMedia media, CancellationToken cancellationToken)
    {
        var coverTask = LoadOptionalImageAsync(media.CoverUrl, media, "live_cover", cancellationToken);
        var avatarTask = LoadOptionalImageAsync(Value(media, "anchor_avatar_url"), media, "live_avatar", cancellationToken, avatarSize: 32);
        var pendantTask = LoadOptionalImageAsync(Value(media, "anchor_pendant_url"), media, "live_pendant", cancellationToken, avatarSize: PendantSize);
        try
        {
            await Task.WhenAll(coverTask, avatarTask, pendantTask).ConfigureAwait(false);
            var model = CreateLiveViewModel(media, await coverTask.ConfigureAwait(false), await avatarTask.ConfigureAwait(false),
                await pendantTask.ConfigureAwait(false));
            return await context.HostServices.RenderCardUriAsync<MediaCard>(model, media.ProviderId, media.MediaId, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (coverTask.IsCompletedSuccessfully) coverTask.Result?.Dispose();
            if (avatarTask.IsCompletedSuccessfully) avatarTask.Result?.Dispose();
            if (pendantTask.IsCompletedSuccessfully) pendantTask.Result?.Dispose();
        }
    }

    private async Task<string> RenderArticleAsync(ParsedMedia media, CancellationToken cancellationToken)
    {
        if (Value(media, "is_dynamic") == "true") return await RenderDynamicAsync(media, cancellationToken).ConfigureAwait(false);

        var bitmaps = new ConcurrentBag<Bitmap>();
        using var slots = new SemaphoreSlim(4);

        async Task<Bitmap?> LoadAsync(string? url, string name, double avatarSize = 0)
        {
            if (string.IsNullOrWhiteSpace(url)) return null;
            await slots.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var bitmap = await LoadOptionalImageAsync(url, media, name, cancellationToken, avatarSize).ConfigureAwait(false);
                if (bitmap is not null) bitmaps.Add(bitmap);
                return bitmap;
            }
            finally { slots.Release(); }
        }

        try
        {
            var coverTask = LoadAsync(media.CoverUrl, "article_cover");
            var avatarTask = LoadAsync(Value(media, "author_avatar_url"), "article_avatar", 40);
            var pendantTask = LoadAsync(Value(media, "author_pendant_url"), "article_pendant", 70);
            var blocksTask = Task.WhenAll(media.Content.Select(async (block, index) =>
            {
                switch (block.Kind)
                {
                    case MediaContentKind.Image:
                        var bitmap = await LoadAsync(block.Url, $"article_{index:D2}").ConfigureAwait(false);
                        return bitmap is null ? null : BiliArticleBlockViewModel.Picture(bitmap, block.Caption ?? string.Empty);
                    case MediaContentKind.Heading:
                        return string.IsNullOrWhiteSpace(block.Text) ? null : BiliArticleBlockViewModel.Heading(block.Text.Trim(), block.Level);
                    case MediaContentKind.Quote:
                        return string.IsNullOrWhiteSpace(block.Text) ? null : BiliArticleBlockViewModel.Quote(block.Text.Trim());
                    case MediaContentKind.Video:
                        return null;
                    default:
                        return string.IsNullOrWhiteSpace(block.Text) ? null : BiliArticleBlockViewModel.Paragraph(block.Text.Trim());
                }
            }));
            // Observe every image load before the bitmaps are disposed, including when one fails.
            await Task.WhenAll(coverTask, avatarTask, pendantTask, blocksTask).ConfigureAwait(false);

            var meta = new List<string>();
            if (DateTimeOffset.TryParse(Value(media, "publish_time"), CultureInfo.InvariantCulture, DateTimeStyles.None, out var published))
                meta.Add(published.ToOffset(TimeSpan.FromHours(8)).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + " 发布");
            if (long.TryParse(Value(media, "words"), out var words) && words > 0) meta.Add(words.ToString("N0", CultureInfo.InvariantCulture) + " 字");
            var fans = long.TryParse(Value(media, "author_fans"), out var fanCount) && fanCount > 0
                ? FormatCount(fanCount.ToString(CultureInfo.InvariantCulture)) + "粉丝" : string.Empty;

            var model = new BiliArticleDocumentViewModel
            {
                Cover = await coverTask.ConfigureAwait(false),
                Avatar = await avatarTask.ConfigureAwait(false),
                AvatarPendant = await pendantTask.ConfigureAwait(false),
                KindText = media.MediaId.StartsWith("opus", StringComparison.Ordinal) ? "动态" : "专栏",
                Title = string.IsNullOrWhiteSpace(media.Title) ? "Bilibili 专栏" : media.Title.Trim(),
                AuthorName = media.AuthorName ?? string.Empty,
                AuthorMeta = fans,
                MetaText = string.Join(" · ", meta),
                Tags = media.Tags.Where(tag => !string.IsNullOrWhiteSpace(tag)).Select(tag => "#" + tag.Trim().TrimStart('#')).ToArray(),
                Metrics =
                [
                    new(MediaCardIcons.Play, "阅读", FormatCount(Value(media, "views"))),
                    new(MediaCardIcons.ThumbUp, "点赞", FormatCount(Value(media, "likes"))),
                    new(MediaCardIcons.Coin, "投币", FormatCount(Value(media, "coins"))),
                    new(MediaCardIcons.Star, "收藏", FormatCount(Value(media, "favorites"))),
                    new(MediaCardIcons.Comment, "评论", FormatCount(Value(media, "replies"))),
                ],
                Blocks = (await blocksTask.ConfigureAwait(false)).OfType<BiliArticleBlockViewModel>().ToArray(),
            };
            return await context.HostServices.RenderCardUriAsync<BiliArticleDocument>(model, media.ProviderId, media.MediaId, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            foreach (var bitmap in bitmaps) bitmap.Dispose();
        }
    }

    private async Task<Bitmap?> LoadImageAsync(string? url, ParsedMedia media, string suffix, CancellationToken cancellationToken, double avatarSize = 0)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        var image = await context.HostServices.BuildProviderImageAsync(new ProviderImageBuildRequest(
            media.ProviderName, url, media.SourceUrl, $"bilibili_{media.MediaId}_{suffix}", PersistLocalFile: true), cancellationToken).ConfigureAwait(false);
        if (avatarSize > 0)
            return !string.IsNullOrWhiteSpace(image.LocalPath)
                ? AvatarBitmapResizer.DecodeFile(image.LocalPath, avatarSize)
                : AvatarBitmapResizer.DecodeBase64(image.Uri, avatarSize);
        return !string.IsNullOrWhiteSpace(image.LocalPath)
            ? context.HostServices.DecodeImageFileForRender(image.LocalPath)
            : context.HostServices.DecodeBase64ImageForRender(image.Uri);
    }

    private static string Value(ParsedMedia media, string key) => media.Attributes.GetValueOrDefault(key, string.Empty);

    private static string FormatLocation(string location) => string.IsNullOrWhiteSpace(location)
        ? string.Empty
        : location.StartsWith("IP属地", StringComparison.Ordinal) ? location : "IP属地：" + location;

    private static string BuildVideoMeta(ParsedMedia media)
    {
        var parts = new List<string>();
        if (long.TryParse(Value(media, "publish_timestamp"), out var timestamp) && timestamp > 0)
        {
            var published = DateTimeOffset.FromUnixTimeSeconds(timestamp).ToOffset(TimeSpan.FromHours(8));
            parts.Add(published.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + " 发布");
        }
        var category = Value(media, "category");
        if (!string.IsNullOrWhiteSpace(category)) parts.Add(category);
        parts.Add(media.MediaId);
        return string.Join(" · ", parts);
    }

    private static string FormatCount(string value) => MediaCardViewModel.FormatCount(value);

    private static string ParseDuration(string? value) => long.TryParse(value, out var seconds)
        ? TimeSpan.FromSeconds(Math.Max(0, seconds)).ToString(seconds >= 3600 ? "h\\:mm\\:ss" : "m\\:ss")
        : "--:--";
}
