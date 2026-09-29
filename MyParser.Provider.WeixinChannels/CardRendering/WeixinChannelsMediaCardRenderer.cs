using Avalonia.Media.Imaging;
using MyParser.Provider.WeixinChannels.Views;
using ShiroBot.AvaloniaSdk;
using ShiroBot.SDK.Plugin;
using Shirobot.Plugin.MyParser.Parsing;

namespace MyParser.Provider.WeixinChannels;

internal sealed class WeixinChannelsMediaCardRenderer(ProviderCardRenderContext context) : IProviderMediaCardRenderer
{
    public async Task<string?> RenderAsync(ParsedMedia media, ProviderCardPurpose purpose, CancellationToken cancellationToken = default)
    {
        if (purpose != ProviderCardPurpose.Main) return null;
        Bitmap? cover = null;
        Bitmap? avatar = null;
        try
        {
            cover = await LoadImageAsync(media.CoverUrl, media, "cover", cancellationToken).ConfigureAwait(false);
            avatar = await LoadImageAsync(Value(media, "author_avatar_url"), media, "avatar", cancellationToken).ConfigureAwait(false);
            var duration = int.TryParse(Value(media, "duration_seconds"), out var seconds)
                ? TimeSpan.FromSeconds(Math.Max(0, seconds)).ToString(@"mm\:ss") : string.Empty;
            var published = DateTimeOffset.TryParse(Value(media, "publish_time"), out var time)
                ? time.ToLocalTime().ToString("yyyy-MM-dd HH:mm") : "微信视频号";
            var model = new WeixinChannelsCardViewModel
            {
                Cover = cover,
                Avatar = avatar,
                Title = media.Title ?? "微信视频号视频",
                AuthorName = media.AuthorName ?? "视频号作者",
                Description = media.Description ?? string.Empty,
                DurationText = duration,
                PublishText = published,
                LikeCount = "赞 " + Value(media, "likes"),
                CommentCount = "评论 " + Value(media, "comments"),
                FavoriteCount = "收藏 " + Value(media, "favorites"),
                ForwardCount = "转发 " + Value(media, "shares"),
            };
            var png = await context.BotContext.RenderControlPngAsync<WeixinChannelsCard>(model, new ControlRenderOptions(RenderTheme.Auto)).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return "base64://" + Convert.ToBase64String(png);
        }
        finally
        {
            cover?.Dispose();
            avatar?.Dispose();
        }
    }

    private async Task<Bitmap?> LoadImageAsync(string? url, ParsedMedia media, string suffix, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        var image = await context.HostServices.BuildProviderImageAsync(new ProviderImageBuildRequest(
            media.ProviderName, url, media.SourceUrl, $"weixinchannels_{media.MediaId}_{suffix}", PersistLocalFile: true), cancellationToken).ConfigureAwait(false);
        return !string.IsNullOrWhiteSpace(image.LocalPath)
            ? context.HostServices.DecodeImageFileForRender(image.LocalPath)
            : context.HostServices.DecodeBase64ImageForRender(image.Uri);
    }

    private static string Value(ParsedMedia media, string key) => media.Attributes.GetValueOrDefault(key, string.Empty);
}
