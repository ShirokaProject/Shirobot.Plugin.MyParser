using Avalonia.Media.Imaging;
using MyParser.Provider.Douyin.Views;
using ShiroBot.AvaloniaSdk;
using ShiroBot.SDK.Plugin;
using Shirobot.Plugin.MyParser.Parsing;

namespace MyParser.Provider.Douyin;

internal sealed class DouyinMediaCardRenderer(ProviderCardRenderContext context) : IProviderMediaCardRenderer
{
    public async Task<string?> RenderAsync(ParsedMedia media, ProviderCardPurpose purpose, CancellationToken cancellationToken = default)
    {
        if (purpose == ProviderCardPurpose.Comments)
        {
            return await RenderCommentsAsync(media, cancellationToken).ConfigureAwait(false);
        }

        if (purpose != ProviderCardPurpose.Main || media.Kind is not (ParsedMediaKind.Video or ParsedMediaKind.Gallery)) return null;

        Bitmap? cover = null;
        Bitmap? avatar = null;
        try
        {
            cover = await LoadImageAsync(media.CoverUrl, media, "cover", cancellationToken).ConfigureAwait(false);
            avatar = await LoadImageAsync(media.Attributes.GetValueOrDefault("author_avatar_url"), media, "avatar", cancellationToken).ConfigureAwait(false);
            var createTime = long.TryParse(media.Attributes.GetValueOrDefault("create_time"), out var timestamp) && timestamp > 0
                ? DateTimeOffset.FromUnixTimeSeconds(timestamp).ToLocalTime().ToString("yyyy-MM-dd HH:mm")
                : string.Empty;
            var duration = long.TryParse(media.Attributes.GetValueOrDefault("duration_ms"), out var durationMs)
                ? TimeSpan.FromMilliseconds(Math.Max(0, durationMs)).ToString(durationMs >= 3_600_000 ? "h\\:mm\\:ss" : "m\\:ss")
                : string.Empty;
            var model = new DouyinCardViewModel
            {
                Cover = cover,
                Avatar = avatar,
                CoverUri = media.CoverUrl ?? string.Empty,
                AlbumId = media.MediaId,
                Title = media.Title ?? "抖音作品",
                Description = media.Description ?? string.Empty,
                AuthorName = media.AuthorName ?? "未知用户",
                AuthorMeta = $"粉丝 {Value(media, "author_followers")} · {Value(media, "author_region")}",
                DurationText = duration,
                PublishTimeText = createTime,
                HasPublishTime = !string.IsNullOrWhiteSpace(createTime),
                PageText = media.Kind == ParsedMediaKind.Gallery ? $"1/{media.Assets.Count(a => a.Kind == MediaAssetKind.Image)}" : string.Empty,
                LikeCount = Value(media, "likes"),
                CollectCount = Value(media, "collects"),
                CommentCount = Value(media, "comments"),
                ShareCount = Value(media, "shares"),
                MusicText = $"{Value(media, "music_title")} - {Value(media, "music_author")}".Trim(' ', '-'),
                TagsText = string.Join(' ', media.Tags.Select(tag => tag.StartsWith('#') ? tag : "#" + tag)),
            };
            var png = await context.BotContext.RenderControlPngAsync<DouyinCard>(model, new ControlRenderOptions(RenderTheme.Auto)).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return "base64://" + Convert.ToBase64String(png);
        }
        finally
        {
            cover?.Dispose();
            avatar?.Dispose();
        }
    }

    private async Task<string?> RenderCommentsAsync(ParsedMedia media, CancellationToken cancellationToken)
    {
        var blocks = media.Content.Where(block => block.Attributes.GetValueOrDefault("category") == "comment").Take(30).ToArray();
        if (blocks.Length == 0) return null;
        var bitmaps = new List<Bitmap>();
        try
        {
            var comments = new List<DouyinCommentItemViewModel>(blocks.Length);
            foreach (var (block, index) in blocks.Select((value, index) => (value, index)))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var avatar = await LoadImageAsync(block.Attributes.GetValueOrDefault("avatar_url"), media, $"comment_{index + 1}_avatar", cancellationToken).ConfigureAwait(false);
                var image = await LoadImageAsync(block.Attributes.GetValueOrDefault("image_url"), media, $"comment_{index + 1}_image", cancellationToken).ConfigureAwait(false);
                if (avatar is not null) bitmaps.Add(avatar);
                if (image is not null) bitmaps.Add(image);
                var createdAt = long.TryParse(block.Attributes.GetValueOrDefault("created"), out var timestamp) && timestamp > 0
                    ? DateTimeOffset.FromUnixTimeSeconds(timestamp).ToLocalTime()
                    : (DateTimeOffset?)null;
                var timeText = createdAt is { } value ? FormatRelativeTime(value) : string.Empty;
                var location = block.Attributes.GetValueOrDefault("ip");
                var metadata = string.Join("  ·  ", new[] { timeText, location }.Where(value => !string.IsNullOrWhiteSpace(value)));
                var message = block.Text.Trim();
                var replyCount = long.TryParse(block.Attributes.GetValueOrDefault("replies"), out var count) ? count : 0;
                comments.Add(new DouyinCommentItemViewModel
                {
                    Avatar = avatar,
                    CommentImage = image,
                    HasImage = image is not null,
                    UserName = block.Caption,
                    TimeLocationText = metadata,
                    Message = message,
                    LikeText = FormatCount(block.Attributes.GetValueOrDefault("likes", "0")),
                    HasReplies = replyCount > 0,
                    ReplyCountText = $"查看 {FormatCount(replyCount.ToString())} 条回复 ›",
                    EstimatedHeight = 120 + Math.Max(0, (message.Length - 1) / 42) * 20 + (image is null ? 0 : 145) + (replyCount > 0 ? 30 : 0),
                    IsAuthor = block.Attributes.GetValueOrDefault("is_author") == bool.TrueString,
                });
            }

            var model = new DouyinCommentCardViewModel
            {
                SourceTitle = media.Title ?? "抖音作品",
                CommentCountText = $"{blocks.Length} 条评论",
                Comments = comments,
                CanvasHeight = Math.Clamp(92 + comments.Sum(comment => comment.EstimatedHeight + 1), 400, 9000),
            };
            var png = await context.BotContext.RenderControlPngAsync<DouyinCommentCard>(model, new ControlRenderOptions(RenderTheme.Light)).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return "base64://" + Convert.ToBase64String(png);
        }
        finally
        {
            foreach (var bitmap in bitmaps) bitmap.Dispose();
        }
    }

    private async Task<Bitmap?> LoadImageAsync(string? url, ParsedMedia media, string name, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        var image = await context.HostServices.BuildProviderImageAsync(new ProviderImageBuildRequest(
            media.ProviderName, url, media.SourceUrl, $"douyin_{media.MediaId}_{name}", PersistLocalFile: true), cancellationToken).ConfigureAwait(false);
        return !string.IsNullOrWhiteSpace(image.LocalPath)
            ? context.HostServices.DecodeImageFileForRender(image.LocalPath)
            : context.HostServices.DecodeBase64ImageForRender(image.Uri);
    }

    private static string Value(ParsedMedia media, string key) => media.Attributes.GetValueOrDefault(key, string.Empty);

    private static string FormatRelativeTime(DateTimeOffset timestamp)
    {
        var elapsed = DateTimeOffset.Now - timestamp;
        if (elapsed < TimeSpan.FromMinutes(1)) return "刚刚";
        if (elapsed < TimeSpan.FromHours(1)) return $"{Math.Max(1, (int)elapsed.TotalMinutes)} 分钟前";
        if (elapsed < TimeSpan.FromDays(1)) return $"{Math.Max(1, (int)elapsed.TotalHours)} 小时前";
        if (elapsed < TimeSpan.FromDays(7)) return $"{Math.Max(1, (int)elapsed.TotalDays)} 天前";
        return timestamp.ToString("MM-dd");
    }

    private static string FormatCount(string? value)
    {
        if (!long.TryParse(value, out var count) || count < 0) return value ?? "0";
        return count >= 100_000_000
            ? $"{count / 100_000_000d:0.#} 亿"
            : count >= 10_000 ? $"{count / 10_000d:0.#} 万" : count.ToString();
    }
}
