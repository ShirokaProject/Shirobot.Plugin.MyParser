using Shirobot.Plugin.MyParser.CardRendering;
using Avalonia.Media.Imaging;
using System.Collections.Concurrent;
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
            avatar = await LoadImageAsync(media.Attributes.GetValueOrDefault("author_avatar_url"), media, "avatar", cancellationToken, avatarSize: 32).ConfigureAwait(false);
            var model = CreateViewModel(media, cover, avatar);
            return await context.HostServices.RenderCardUriAsync<MediaCard>(model, media.ProviderId, media.MediaId, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            cover?.Dispose();
            avatar?.Dispose();
        }
    }

    internal static MediaCardViewModel CreateViewModel(ParsedMedia media, Bitmap? cover, Bitmap? avatar)
    {
        var isGallery = media.Kind == ParsedMediaKind.Gallery;
        var imageCount = media.Assets.Count(asset => asset.Kind == MediaAssetKind.Image);
        var badgeText = isGallery
            ? (imageCount > 0 ? $"{imageCount} 张" : "图文")
            : long.TryParse(Value(media, "duration_ms"), out var durationMs) && durationMs > 0
                ? MediaCardViewModel.FormatDuration(TimeSpan.FromMilliseconds(durationMs))
                : string.Empty;

        var metaParts = new List<string>();
        if (long.TryParse(Value(media, "create_time"), out var timestamp) && timestamp > 0)
        {
            metaParts.Add(DateTimeOffset.FromUnixTimeSeconds(timestamp).ToOffset(TimeSpan.FromHours(8))
                .ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture) + " 发布");
        }
        metaParts.Add(isGallery ? "图文" : "视频");
        if (!string.IsNullOrWhiteSpace(media.MediaId)) metaParts.Add(media.MediaId);

        var title = string.IsNullOrWhiteSpace(media.Title) ? "抖音作品" : media.Title.Trim();
        var description = media.Description?.Trim() ?? string.Empty;
        if (description == title || title.StartsWith(description, StringComparison.Ordinal)) description = string.Empty;

        var authorMeta = new List<string>();
        if (long.TryParse(Value(media, "author_followers"), out var followers) && followers > 0)
            authorMeta.Add("粉丝 " + MediaCardViewModel.FormatCount(followers.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        var region = Value(media, "author_region");
        if (!string.IsNullOrWhiteSpace(region))
            authorMeta.Add(region.StartsWith("IP属地", StringComparison.Ordinal) ? region[4..].TrimStart('：', ':', ' ') : region);

        var music = string.Join(" - ", new[] { Value(media, "music_title"), Value(media, "music_author") }
            .Where(part => !string.IsNullOrWhiteSpace(part)));

        return new MediaCardViewModel
        {
            Platform = MediaCardPlatform.Douyin,
            Cover = cover,
            Avatar = avatar,
            BadgeText = badgeText,
            BadgeIcon = isGallery ? MediaCardIcons.Image : MediaCardIcons.Play,
            Title = title,
            Meta = string.Join(" · ", metaParts),
            Description = description,
            DescriptionMaxLines = 6,
            Tags = media.Tags.Where(tag => !string.IsNullOrWhiteSpace(tag))
                .Select(tag => "#" + tag.Trim().TrimStart('#')).Distinct().ToArray(),
            Metrics =
            [
                new(MediaCardIcons.Heart, "点赞", MediaCardViewModel.FormatCount(Value(media, "likes"))),
                new(MediaCardIcons.Comment, "评论", MediaCardViewModel.FormatCount(Value(media, "comments"))),
                new(MediaCardIcons.Star, "收藏", MediaCardViewModel.FormatCount(Value(media, "collects"))),
                new(MediaCardIcons.Share, "分享", MediaCardViewModel.FormatCount(Value(media, "shares"))),
            ],
            ExtraIcon = MediaCardIcons.Music,
            ExtraText = music,
            AuthorName = media.AuthorName ?? "未知用户",
            AuthorMeta = string.Join(" · ", authorMeta),
        };
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

            var total = Value(media, "comments");
            var model = new CommentCardViewModel
            {
                Platform = MediaCardPlatform.Douyin,
                Cover = cover,
                SourceTitle = string.IsNullOrWhiteSpace(media.Title) ? "抖音作品" : media.Title.Trim(),
                CountText = $"热评 {comments.Count}",
                SourceMeta = long.TryParse(total, out var totalCount) && totalCount > 0
                    ? $"共 {MediaCardViewModel.FormatCount(total)} 条评论" : string.Empty,
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
            global::Shirobot.Plugin.MyParser.DiagnosticLog.Warning($"MyParser 抖音评论图片加载失败: media_id={media.MediaId}, name={name}, error={ex.GetType().Name}");
            return null;
        }
    }

    private async Task<Bitmap?> LoadImageAsync(string? url, ParsedMedia media, string name, CancellationToken cancellationToken, double avatarSize = 0)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        var image = await context.HostServices.BuildProviderImageAsync(new ProviderImageBuildRequest(
            media.ProviderName, url, media.SourceUrl, $"douyin_{media.MediaId}_{name}", PersistLocalFile: true), cancellationToken).ConfigureAwait(false);
        if (avatarSize > 0)
            return !string.IsNullOrWhiteSpace(image.LocalPath)
                ? AvatarBitmapResizer.DecodeFile(image.LocalPath, avatarSize)
                : AvatarBitmapResizer.DecodeBase64(image.Uri, avatarSize);
        return !string.IsNullOrWhiteSpace(image.LocalPath)
            ? context.HostServices.DecodeImageFileForRender(image.LocalPath)
            : context.HostServices.DecodeBase64ImageForRender(image.Uri);
    }

    private static string Value(ParsedMedia media, string key) => media.Attributes.GetValueOrDefault(key, string.Empty);
}
