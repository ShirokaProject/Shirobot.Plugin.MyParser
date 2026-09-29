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
            var cover = await LoadImageAsync(media.CoverUrl, media, "comments_cover", cancellationToken).ConfigureAwait(false);
            if (cover is not null) bitmaps.Add(cover);
            var comments = new List<DouyinCommentItemViewModel>(blocks.Length);
            foreach (var (block, index) in blocks.Select((value, index) => (value, index)))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var avatar = await LoadImageAsync(block.Attributes.GetValueOrDefault("avatar_url"), media, $"comment_{index + 1}_avatar", cancellationToken).ConfigureAwait(false);
                var image = await LoadImageAsync(block.Attributes.GetValueOrDefault("image_url"), media, $"comment_{index + 1}_image", cancellationToken).ConfigureAwait(false);
                if (avatar is not null) bitmaps.Add(avatar);
                if (image is not null) bitmaps.Add(image);
                var created = long.TryParse(block.Attributes.GetValueOrDefault("created"), out var timestamp) && timestamp > 0
                    ? DateTimeOffset.FromUnixTimeSeconds(timestamp).ToLocalTime().ToString("MM-dd HH:mm") : "时间未知";
                comments.Add(new DouyinCommentItemViewModel
                {
                    Avatar = avatar,
                    CommentImage = image,
                    HasImage = image is not null,
                    UserName = block.Caption,
                    UserIdText = "抖音号 " + block.Attributes.GetValueOrDefault("user_id", "--"),
                    IpText = string.IsNullOrWhiteSpace(block.Attributes.GetValueOrDefault("ip")) ? "IP 未知" : "IP " + block.Attributes.GetValueOrDefault("ip"),
                    Message = block.Text,
                    LikeText = block.Attributes.GetValueOrDefault("likes", "0"),
                    ReplyText = block.Attributes.GetValueOrDefault("replies", "0"),
                    TimeText = created,
                    IndexText = (index + 1).ToString("D2"),
                    IsAuthor = block.Attributes.GetValueOrDefault("is_author") == bool.TrueString,
                });
            }

            var model = new DouyinCommentCardViewModel
            {
                Cover = cover,
                Title = $"{media.Title ?? "抖音作品"} · 热门评论",
                MetaText = media.AuthorName ?? string.Empty,
                StatsText = $"赞 {Value(media, "likes")}  ·  评论 {blocks.Length}",
                Comments = comments,
                CanvasHeight = Math.Clamp(280 + blocks.Length * 220, 520, 9000),
            };
            var png = await context.BotContext.RenderControlPngAsync<DouyinCommentCard>(model, new ControlRenderOptions(RenderTheme.Dark)).ConfigureAwait(false);
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
}
