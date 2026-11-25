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
        try
        {
            await Task.WhenAll(coverTask, avatarTask).ConfigureAwait(false);
            var cover = await coverTask.ConfigureAwait(false);
            var avatar = await avatarTask.ConfigureAwait(false);

            if (media.ProviderId == "bilibili-bangumi")
            {
                var model = CreateBangumiViewModel(media, cover);
                return await context.HostServices.RenderCardUriAsync<BiliBangumiCard>(model, media.ProviderId, media.MediaId, cancellationToken).ConfigureAwait(false);
            }

            var viewModel = CreateVideoViewModel(media, cover, avatar);
            return await context.HostServices.RenderCardUriAsync<MediaCard>(viewModel, media.ProviderId, media.MediaId, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (coverTask.IsCompletedSuccessfully) coverTask.Result?.Dispose();
            if (avatarTask.IsCompletedSuccessfully) avatarTask.Result?.Dispose();
        }
    }

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

    private async Task<Bitmap?> LoadOptionalImageAsync(string? url, ParsedMedia media, string name, CancellationToken cancellationToken, double avatarSize = 0)
    {
        try
        {
            return await LoadImageAsync(url, media, name, cancellationToken, avatarSize).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            global::Shirobot.Plugin.MyParser.DiagnosticLog.Warning($"MyParser Bilibili 评论图片加载失败: media_id={media.MediaId}, name={name}, error={ex.GetType().Name}");
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

    internal static MediaCardViewModel CreateVideoViewModel(ParsedMedia media, Bitmap? cover, Bitmap? avatar)
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
            AuthorName = media.AuthorName ?? "未知 UP",
            AuthorMeta = FormatLocation(Value(media, "author_location")),
        };
    }

    internal static BiliCardViewModel CreateBangumiViewModel(ParsedMedia media, Bitmap? cover)
    {
        var count = int.TryParse(Value(media, "pages"), out var pages) ? Math.Max(1, pages) : 1;
        var current = int.TryParse(Value(media, "requested_page"), out var page) ? Math.Clamp(page, 1, count) : 1;
        return new BiliCardViewModel
        {
            Cover = cover,
            Title = media.Title ?? "Bilibili 番剧",
            Description = media.Description ?? string.Empty,
            DescriptionMaxLines = 6,
            CoverHeight = 290,
            ContentMargin = new Avalonia.Thickness(14, 175, 14, 14),
            VideoMeta = string.Join(" · ", new[] { Value(media, "publish"), Value(media, "rating") }
                .Where(value => !string.IsNullOrWhiteSpace(value))),
            HasMultiPages = true,
            CurrentPageText = $"当前解析 第{current}/{count}集",
            Tags = media.Tags.Where(tag => !string.IsNullOrWhiteSpace(tag)).Select(tag => "#" + tag.Trim().TrimStart('#')).ToArray(),
        };
    }

    private async Task<string> RenderLiveAsync(ParsedMedia media, CancellationToken cancellationToken)
    {
        Bitmap? cover = null;
        Bitmap? avatar = null;
        try
        {
            cover = await LoadImageAsync(media.CoverUrl, media, "live_cover", cancellationToken).ConfigureAwait(false);
            avatar = await LoadImageAsync(Value(media, "anchor_avatar_url"), media, "live_avatar", cancellationToken, avatarSize: 44).ConfigureAwait(false);
            var status = Value(media, "live_status") == "1" ? "直播中" : "未开播";
            var model = new BiliLiveCardViewModel
            {
                Cover = cover,
                Avatar = avatar,
                StatusText = status,
                RoomIdText = Value(media, "room_id"),
                Title = media.Title ?? "Bilibili 直播",
                AnchorName = media.AuthorName ?? string.Empty,
                AudienceText = Value(media, "audience_text"),
                WatchedText = Value(media, "watched"),
                LiveStartTimeText = Value(media, "live_start"),
                LiveDurationText = Value(media, "duration"),
            };
            return await context.HostServices.RenderCardUriAsync<BiliLiveCard>(model, media.ProviderId, media.MediaId, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            cover?.Dispose();
            avatar?.Dispose();
        }
    }

    private async Task<string> RenderArticleAsync(ParsedMedia media, CancellationToken cancellationToken)
    {
        var bitmaps = new List<Bitmap>();
        try
        {
            var avatar = await LoadImageAsync(Value(media, "author_avatar_url"), media, "avatar", cancellationToken, avatarSize: 46).ConfigureAwait(false);
            if (avatar is not null) bitmaps.Add(avatar);
            var blocks = new List<BiliArticleDocumentBlockViewModel>();
            foreach (var block in media.Content)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (block.Kind == MediaContentKind.Image)
                {
                    var image = await LoadImageAsync(block.Url, media, $"article_{blocks.Count:D2}", cancellationToken).ConfigureAwait(false);
                    if (image is not null) bitmaps.Add(image);
                    blocks.Add(new BiliArticleDocumentBlockViewModel
                    {
                        Image = image, IsImage = image is not null, Text = block.Text, Caption = block.Caption,
                        Height = image is null ? 0 : 260,
                    });
                    continue;
                }

                if (string.IsNullOrWhiteSpace(block.Text)) continue;
                var fontSize = block.Kind == MediaContentKind.Heading ? 23 : 15;
                blocks.Add(new BiliArticleDocumentBlockViewModel
                {
                    Text = block.Text,
                    IsHeading = block.Kind == MediaContentKind.Heading,
                    IsQuote = block.Kind == MediaContentKind.Quote,
                    FontSize = fontSize,
                    Height = Math.Clamp((block.Text.Length / 30 + 1) * 26, 42, 420),
                });
            }

            var canvasHeight = Math.Clamp(blocks.Sum(block => block.Height) + 220, 520, 12000);
            var model = new BiliArticleDocumentViewModel
            {
                CanvasHeight = canvasHeight,
                Avatar = avatar,
                KindText = "Bilibili 专栏",
                Title = media.Title ?? "Bilibili 专栏",
                AuthorName = media.AuthorName ?? string.Empty,
                MetaText = $"{media.Attributes.GetValueOrDefault("publish_time", string.Empty)} · {media.Attributes.GetValueOrDefault("words", string.Empty)} 字",
                StatsText = $"阅读 {Value(media, "views")}  ·  点赞 {Value(media, "likes")}",
                Blocks = blocks,
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
