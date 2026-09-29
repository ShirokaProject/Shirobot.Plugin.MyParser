using Avalonia.Media.Imaging;
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

        if (purpose == ProviderCardPurpose.Main && media.Kind == ParsedMediaKind.Live)
        {
            return await RenderLiveAsync(media, cancellationToken).ConfigureAwait(false);
        }

        if (purpose != ProviderCardPurpose.Main || media.Kind != ParsedMediaKind.Video) return null;

        Bitmap? cover = null;
        try
        {
            if (!string.IsNullOrWhiteSpace(media.CoverUrl))
            {
                var image = await context.HostServices.BuildProviderImageAsync(new ProviderImageBuildRequest(
                    media.ProviderName, media.CoverUrl, media.SourceUrl, $"bilibili_{media.MediaId}_cover", PersistLocalFile: true), cancellationToken).ConfigureAwait(false);
                cover = !string.IsNullOrWhiteSpace(image.LocalPath)
                    ? context.HostServices.DecodeImageFileForRender(image.LocalPath)
                    : context.HostServices.DecodeBase64ImageForRender(image.Uri);
            }

            var stream = media.Assets.FirstOrDefault(asset => asset.Kind == MediaAssetKind.Video);
            var duration = ParseDuration(media.Attributes.GetValueOrDefault("duration_seconds"));
            var viewModel = new BiliCardViewModel
            {
                Cover = cover,
                Title = media.Title ?? "Bilibili 视频",
                Description = media.Description ?? string.Empty,
                AuthorName = media.AuthorName ?? "未知 UP",
                AuthorMeta = $"播放 {Value(media, "views")}  ·  时长 {duration}",
                DurationText = duration,
                TagsText = string.IsNullOrWhiteSpace(stream?.Label) ? "# 视频" : $"# {stream.Label}",
                LikeCount = Value(media, "likes"),
                CoinCount = Value(media, "coins"),
                CollectCount = Value(media, "favorites"),
                ShareCount = Value(media, "shares"),
            };
            var png = await context.BotContext.RenderControlPngAsync<BiliCard>(viewModel, new ControlRenderOptions(RenderTheme.Auto)).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return "base64://" + Convert.ToBase64String(png);
        }
        finally
        {
            cover?.Dispose();
        }
    }

    private async Task<string> RenderLiveAsync(ParsedMedia media, CancellationToken cancellationToken)
    {
        Bitmap? cover = null;
        Bitmap? avatar = null;
        try
        {
            cover = await LoadImageAsync(media.CoverUrl, media, "live_cover", cancellationToken).ConfigureAwait(false);
            avatar = await LoadImageAsync(Value(media, "anchor_avatar_url"), media, "live_avatar", cancellationToken).ConfigureAwait(false);
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
            var png = await context.BotContext.RenderControlPngAsync<BiliLiveCard>(model, new ControlRenderOptions(RenderTheme.Auto)).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return "base64://" + Convert.ToBase64String(png);
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
            var avatar = await LoadImageAsync(Value(media, "author_avatar_url"), media, "avatar", cancellationToken).ConfigureAwait(false);
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
            var png = await context.BotContext.RenderControlPngAsync<BiliArticleDocument>(model, new ControlRenderOptions(RenderTheme.Dark)).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return "base64://" + Convert.ToBase64String(png);
        }
        finally
        {
            foreach (var bitmap in bitmaps) bitmap.Dispose();
        }
    }

    private async Task<Bitmap?> LoadImageAsync(string? url, ParsedMedia media, string suffix, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        var image = await context.HostServices.BuildProviderImageAsync(new ProviderImageBuildRequest(
            media.ProviderName, url, media.SourceUrl, $"bilibili_{media.MediaId}_{suffix}", PersistLocalFile: true), cancellationToken).ConfigureAwait(false);
        return !string.IsNullOrWhiteSpace(image.LocalPath)
            ? context.HostServices.DecodeImageFileForRender(image.LocalPath)
            : context.HostServices.DecodeBase64ImageForRender(image.Uri);
    }

    private static string Value(ParsedMedia media, string key) => media.Attributes.GetValueOrDefault(key, string.Empty);

    private static string ParseDuration(string? value) => long.TryParse(value, out var seconds)
        ? TimeSpan.FromSeconds(Math.Max(0, seconds)).ToString(seconds >= 3600 ? "h\\:mm\\:ss" : "m\\:ss")
        : "--:--";
}
