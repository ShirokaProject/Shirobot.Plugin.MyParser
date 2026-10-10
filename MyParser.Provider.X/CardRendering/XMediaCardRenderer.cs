using Avalonia.Media.Imaging;
using MyParser.Provider.X.Views;
using ShiroBot.AvaloniaSdk;
using ShiroBot.SDK.Plugin;
using Shirobot.Plugin.MyParser.Parsing;

namespace MyParser.Provider.X;

internal sealed class XMediaCardRenderer(ProviderCardRenderContext context) : IProviderMediaCardRenderer
{
    public async Task<string?> RenderAsync(ParsedMedia media, ProviderCardPurpose purpose, CancellationToken cancellationToken = default)
    {
        if (purpose != ProviderCardPurpose.Main) return null;

        Bitmap? cover = null;
        Bitmap? avatar = null;
        try
        {
            cover = await LoadImageAsync(media.CoverUrl, media, "cover", cancellationToken).ConfigureAwait(false);
            avatar = await LoadImageAsync(media.Attributes.GetValueOrDefault("author_avatar_url"), media, "avatar", cancellationToken).ConfigureAwait(false);

            var kindText = media.Kind switch
            {
                ParsedMediaKind.Video when media.Assets.Any(asset => IsGif(asset.Url)) => "动图",
                ParsedMediaKind.Video => "视频",
                ParsedMediaKind.Gallery => "图集",
                _ => "推文",
            };
            var page = media.Kind == ParsedMediaKind.Gallery
                ? $"1/{media.Assets.Count(asset => asset.Kind == MediaAssetKind.Image)}"
                : string.Empty;

            var model = new XCardViewModel
            {
                Cover = cover,
                Avatar = avatar,
                HasCover = cover is not null,
                ShowCoverPlaceholder = cover is null,
                HasAvatar = avatar is not null,
                ShowAvatarPlaceholder = avatar is null,
                Verified = media.Attributes.GetValueOrDefault("blue_verified") == "1",
                DisplayName = media.Title ?? string.Empty,
                ScreenName = media.AuthorName ?? string.Empty,
                PublishTime = media.Attributes.GetValueOrDefault("发布于") ?? string.Empty,
                KindText = kindText,
                MediaPage = page,
                Description = media.Description ?? string.Empty,
                ReplyCount = FormatCount(media.Attributes.GetValueOrDefault("回复")),
                RetweetCount = FormatCount(media.Attributes.GetValueOrDefault("转推")),
                LikeCount = FormatCount(media.Attributes.GetValueOrDefault("点赞")),
                ViewCount = FormatCount(media.Attributes.GetValueOrDefault("浏览")),
                SourceUrl = FormatSourceUrl(media.SourceUrl),
            };
            var png = await context.BotContext.RenderControlPngAsync<XCard>(model, new ControlRenderOptions(RenderTheme.Auto)).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return "base64://" + Convert.ToBase64String(png);
        }
        finally
        {
            cover?.Dispose();
            avatar?.Dispose();
        }
    }

    private async Task<Bitmap?> LoadImageAsync(string? url, ParsedMedia media, string name, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        var image = await context.HostServices.BuildProviderImageAsync(new ProviderImageBuildRequest(
            media.ProviderName, url, media.SourceUrl, $"x_{media.MediaId}_{name}", PersistLocalFile: true), cancellationToken).ConfigureAwait(false);
        return !string.IsNullOrWhiteSpace(image.LocalPath)
            ? context.HostServices.DecodeImageFileForRender(image.LocalPath)
            : context.HostServices.DecodeBase64ImageForRender(image.Uri);
    }

    private static bool IsGif(string? url) =>
        !string.IsNullOrWhiteSpace(url)
        && url.Contains("tweet_video/", StringComparison.OrdinalIgnoreCase);

    private static string FormatSourceUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return string.Empty;
        return url.Replace("https://", string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    private static string FormatCount(string? value)
    {
        if (!long.TryParse(value, out var count) || count < 0) return value ?? "0";
        return count >= 100_000_000
            ? $"{count / 100_000_000d:0.#} 亿"
            : count >= 10_000 ? $"{count / 10_000d:0.#} 万" : count.ToString();
    }
}
