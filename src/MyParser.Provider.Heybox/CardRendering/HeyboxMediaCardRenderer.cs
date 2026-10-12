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

            var stats = new[]
            {
                Pair("浏览", Value(media, "views")), Pair("点赞", Value(media, "likes")),
                Pair("收藏", Value(media, "favorites")), Pair("评论", Value(media, "comments")),
            }.Where(value => value.Length > 0);
            var mediaText = $"视频 {Value(media, "video_count")}  ·  图片 {Value(media, "image_count")}";
            var model = new HeyboxCardViewModel
            {
                Cover = cover,
                Title = media.Title ?? "小黑盒帖子",
                Description = media.Description ?? string.Empty,
                AuthorName = media.AuthorName ?? "小黑盒用户",
                StatsText = string.Join("  ·  ", stats),
                MediaText = mediaText,
                LinkId = media.MediaId,
                SourceText = Value(media, "source_kind"),
            };
            return await context.HostServices.RenderCardUriAsync<HeyboxCard>(model, media.ProviderId, media.MediaId, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            cover?.Dispose();
        }
    }

    private async Task<string?> RenderArticleAsync(ParsedMedia media, CancellationToken cancellationToken)
    {
        var bitmaps = new System.Collections.Concurrent.ConcurrentBag<Bitmap>();
        using var slots = new SemaphoreSlim(4);
        async Task<Bitmap?> LoadAsync(string? url, string label)
        {
            if (string.IsNullOrWhiteSpace(url)) return null;
            await slots.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var image = await context.HostServices.BuildProviderImageAsync(new ProviderImageBuildRequest(
                    media.ProviderName, url, media.SourceUrl, $"heybox_{media.MediaId}_{label}", PersistLocalFile: true), cancellationToken).ConfigureAwait(false);
                var bitmap = !string.IsNullOrWhiteSpace(image.LocalPath)
                    ? context.HostServices.DecodeImageFileForRender(image.LocalPath)
                    : context.HostServices.DecodeBase64ImageForRender(image.Uri);
                if (bitmap is not null) bitmaps.Add(bitmap);
                return bitmap;
            }
            finally { slots.Release(); }
        }
        try
        {
            var coverTask = LoadAsync(media.CoverUrl, "article_cover");
            var blocksTask = Task.WhenAll(media.Content.Select(async (block, index) =>
            {
                if (block.Kind == MediaContentKind.Image)
                {
                    var bitmap = await LoadAsync(block.Url, $"article_{index}").ConfigureAwait(false);
                    if (bitmap is not null)
                        return new HeyboxArticleDocumentBlockViewModel { IsImage = true, Image = bitmap,
                            Caption = block.Caption ?? string.Empty, Height = Math.Clamp((int)(624d * bitmap.PixelSize.Height / Math.Max(1, bitmap.PixelSize.Width)), 120, 1600) };
                }
                var text = block.Kind == MediaContentKind.Video ? $"视频直链：{block.Url}"
                    : block.Kind == MediaContentKind.Image ? $"图片：{block.Url}" : block.Text;
                var heading = block.Kind == MediaContentKind.Heading;
                var fontSize = heading ? 23 : 15;
                var lineHeight = heading ? 32 : 24;
                var lineCount = (text ?? string.Empty).Split('\n').Sum(line => Math.Max(1, (int)Math.Ceiling(line.Length / (624d / fontSize))));
                return new HeyboxArticleDocumentBlockViewModel { Text = text ?? string.Empty, HeadingLevel = block.Level,
                    FontSize = fontSize, LineHeight = lineHeight, FontWeight = heading ? "Bold" : "Normal",
                    Height = lineCount * lineHeight + 28,
                    AccentWidth = block.Kind == MediaContentKind.Quote ? 5 : 0, AccentBrush = "#FF38BDF8" };
            }));
            // Observe all image loads before disposing their bitmaps, including when a load fails.
            await Task.WhenAll(coverTask, blocksTask).ConfigureAwait(false);
            var blocks = await blocksTask.ConfigureAwait(false);
            var model = new HeyboxArticleDocumentViewModel
            {
                CanvasHeight = Math.Max(980, 596 + blocks.Sum(block => block.Height + 42)),
                Cover = await coverTask.ConfigureAwait(false), KindText = "小黑盒文章",
                Title = media.Title ?? "小黑盒文章", AuthorName = media.AuthorName ?? "小黑盒用户",
                MetaText = media.SourceUrl ?? string.Empty,
                StatsText = string.Join("  ·  ", new[] { Pair("浏览", Value(media, "views")), Pair("点赞", Value(media, "likes")),
                    Pair("收藏", Value(media, "favorites")), Pair("评论", Value(media, "comments")) }.Where(value => value.Length > 0)),
                Blocks = blocks,
            };
            return await context.HostServices.RenderCardUriAsync<HeyboxArticleDocument>(model, media.ProviderId,
                media.MediaId, cancellationToken, new ControlRenderOptions(RenderTheme.Dark)).ConfigureAwait(false);
        }
        finally { foreach (var bitmap in bitmaps) bitmap.Dispose(); }
    }

    private static string Value(ParsedMedia media, string key) => media.Attributes.GetValueOrDefault(key, string.Empty);
    private static string Pair(string label, string value) => string.IsNullOrWhiteSpace(value) ? string.Empty : $"{label} {value}";
}
