using Shirobot.Plugin.MyParser.CardRendering;
using Avalonia.Media.Imaging;
using MyParser.Provider.Heybox.Views;
using ShiroBot.AvaloniaSdk;
using ShiroBot.SDK.Plugin;
using Shirobot.Plugin.MyParser.Parsing;

namespace MyParser.Provider.Heybox;

internal sealed class HeyboxMediaCardRenderer(ProviderCardRenderContext context) : IProviderMediaCardRenderer
{
    public async Task<string?> RenderAsync(ParsedMedia media, ProviderCardPurpose purpose, CancellationToken cancellationToken = default)
    {
        if (purpose == ProviderCardPurpose.Article) return await RenderArticleAsync(media, cancellationToken).ConfigureAwait(false);
        if (purpose != ProviderCardPurpose.Main) return null;
        Bitmap? cover = null;
        Bitmap? avatar = null;
        try
        {
            if (!string.IsNullOrWhiteSpace(media.CoverUrl))
            {
                var image = await context.HostServices.BuildProviderImageAsync(new ProviderImageBuildRequest(
                    media.ProviderName, media.CoverUrl, media.SourceUrl, $"heybox_{media.MediaId}_cover", PersistLocalFile: true), cancellationToken).ConfigureAwait(false);
                cover = !string.IsNullOrWhiteSpace(image.LocalPath)
                    ? context.HostServices.DecodeImageFileForRender(image.LocalPath)
                    : context.HostServices.DecodeBase64ImageForRender(image.Uri);
            }

            if (Value(media, "author_avatar_url") is { Length: > 0 } avatarUrl)
            {
                try
                {
                    var image = await context.HostServices.BuildProviderImageAsync(new ProviderImageBuildRequest(
                        media.ProviderName, avatarUrl, media.SourceUrl, $"heybox_{media.MediaId}_avatar", PersistLocalFile: true), cancellationToken).ConfigureAwait(false);
                    avatar = !string.IsNullOrWhiteSpace(image.LocalPath)
                        ? AvatarBitmapResizer.DecodeFile(image.LocalPath, 72)
                        : AvatarBitmapResizer.DecodeBase64(image.Uri, 72);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // 头像只是点缀，取不到时显示昵称首字。
                }
            }

            var model = CreateViewModel(media, cover, avatar, DateTimeOffset.Now);
            return await context.HostServices.RenderCardUriAsync<HeyboxCard>(model, media.ProviderId, media.MediaId, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            cover?.Dispose();
            avatar?.Dispose();
        }
    }

    internal static HeyboxCardViewModel CreateViewModel(ParsedMedia media, Bitmap? cover, Bitmap? avatar, DateTimeOffset now)
    {
        var kind = media.Kind switch
        {
            ParsedMediaKind.Article => "文章",
            ParsedMediaKind.Video => "视频",
            ParsedMediaKind.Gallery => "图文",
            _ => "帖子",
        };
        var published = DateTimeOffset.TryParse(Value(media, "publish_time"), out var time)
            ? CommentCardViewModel.FormatRelativeTime(time, now) : null;
        static string Count(string value) => int.TryParse(value, out var count) && count > 0 ? count.ToString() : string.Empty;
        return new HeyboxCardViewModel
        {
            Cover = cover,
            Avatar = avatar,
            AuthorName = string.IsNullOrWhiteSpace(media.AuthorName) ? "小黑盒用户" : media.AuthorName,
            MetaText = published is null ? kind : $"{published} · {kind}",
            Title = string.IsNullOrWhiteSpace(media.Title) ? "小黑盒帖子" : media.Title.Trim(),
            Description = media.Description ?? string.Empty,
            Topics = Value(media, "topics").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct().Take(4).ToArray(),
            ImageCountText = Count(Value(media, "image_count")),
            VideoCountText = Count(Value(media, "video_count")),
            Stats = HeyboxCardViewModel.CreateStats(Value(media, "views"), Value(media, "likes"),
                Value(media, "favorites"), Value(media, "comments")),
        };
    }

    private async Task<string?> RenderArticleAsync(ParsedMedia media, CancellationToken cancellationToken)
    {
        var bitmaps = new System.Collections.Concurrent.ConcurrentBag<Bitmap>();
        using var slots = new SemaphoreSlim(4);
        async Task<Bitmap?> LoadAsync(string? url, string label, double avatarSize = 0)
        {
            if (string.IsNullOrWhiteSpace(url)) return null;
            await slots.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var image = await context.HostServices.BuildProviderImageAsync(new ProviderImageBuildRequest(
                    media.ProviderName, url, media.SourceUrl, $"heybox_{media.MediaId}_{label}", PersistLocalFile: true), cancellationToken).ConfigureAwait(false);
                var bitmap = avatarSize > 0
                    ? !string.IsNullOrWhiteSpace(image.LocalPath)
                        ? AvatarBitmapResizer.DecodeFile(image.LocalPath, avatarSize)
                        : AvatarBitmapResizer.DecodeBase64(image.Uri, avatarSize)
                    : !string.IsNullOrWhiteSpace(image.LocalPath)
                        ? context.HostServices.DecodeImageFileForRender(image.LocalPath)
                        : context.HostServices.DecodeBase64ImageForRender(image.Uri);
                if (bitmap is not null) bitmaps.Add(bitmap);
                return bitmap;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // 单张图片失败只少一张图，不影响整篇文章。
                return null;
            }
            finally { slots.Release(); }
        }
        try
        {
            var coverTask = LoadAsync(media.CoverUrl, "article_cover");
            var avatarTask = LoadAsync(Value(media, "author_avatar_url"), "article_avatar", 72);
            var blocksTask = Task.WhenAll(media.Content.Select(async (block, index) =>
            {
                switch (block.Kind)
                {
                    case MediaContentKind.Image:
                        var bitmap = await LoadAsync(block.Url, $"article_{index}").ConfigureAwait(false);
                        return bitmap is null ? null : HeyboxArticleBlockViewModel.Picture(bitmap, block.Caption ?? string.Empty);
                    case MediaContentKind.Video:
                        return string.IsNullOrWhiteSpace(block.Url) ? null : HeyboxArticleBlockViewModel.Video(block.Url);
                    case MediaContentKind.Heading:
                        return string.IsNullOrWhiteSpace(block.Text) ? null : HeyboxArticleBlockViewModel.Heading(block.Text.Trim(), block.Level);
                    case MediaContentKind.Quote:
                        return string.IsNullOrWhiteSpace(block.Text) ? null : HeyboxArticleBlockViewModel.Quote(block.Text.Trim());
                    default:
                        return string.IsNullOrWhiteSpace(block.Text) ? null : HeyboxArticleBlockViewModel.Paragraph(block.Text.Trim());
                }
            }));
            // Observe all image loads before disposing their bitmaps, including when a load fails.
            await Task.WhenAll(coverTask, avatarTask, blocksTask).ConfigureAwait(false);
            var card = CreateViewModel(media, null, null, DateTimeOffset.Now);
            var model = new HeyboxArticleDocumentViewModel
            {
                Cover = await coverTask.ConfigureAwait(false),
                Avatar = await avatarTask.ConfigureAwait(false),
                KindText = "文章",
                Title = string.IsNullOrWhiteSpace(media.Title) ? "小黑盒文章" : media.Title.Trim(),
                AuthorName = card.AuthorName,
                MetaText = DateTimeOffset.TryParse(Value(media, "publish_time"), out var time)
                    ? time.ToOffset(TimeSpan.FromHours(8)).ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture) + " 发布"
                    : "小黑盒文章",
                Topics = card.Topics,
                Stats = card.Stats,
                Blocks = (await blocksTask.ConfigureAwait(false)).OfType<HeyboxArticleBlockViewModel>().ToArray(),
            };
            return await context.HostServices.RenderCardUriAsync<HeyboxArticleDocument>(model, media.ProviderId,
                media.MediaId, cancellationToken).ConfigureAwait(false);
        }
        finally { foreach (var bitmap in bitmaps) bitmap.Dispose(); }
    }

    private static string Value(ParsedMedia media, string key) => media.Attributes.GetValueOrDefault(key, string.Empty);
}
