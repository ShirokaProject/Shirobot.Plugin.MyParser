using Shirobot.Plugin.MyParser.CardRendering;
using Avalonia.Media.Imaging;
using System.Text.RegularExpressions;
using ShiroBot.AvaloniaSdk;
using ShiroBot.SDK.Plugin;
using Shirobot.Plugin.MyParser.Parsing;

namespace MyParser.Provider.WeixinChannels;

internal sealed partial class WeixinChannelsMediaCardRenderer(ProviderCardRenderContext context) : IProviderMediaCardRenderer
{
    public async Task<string?> RenderAsync(ParsedMedia media, ProviderCardPurpose purpose, CancellationToken cancellationToken = default)
    {
        if (purpose != ProviderCardPurpose.Main) return null;
        Bitmap? cover = null;
        Bitmap? avatar = null;
        try
        {
            cover = await LoadImageAsync(media.CoverUrl, media, "cover", cancellationToken).ConfigureAwait(false);
            avatar = await LoadImageAsync(Value(media, "author_avatar_url"), media, "avatar", cancellationToken, avatarSize: 32).ConfigureAwait(false);
            var model = CreateViewModel(media, cover, avatar, DateTimeOffset.Now);
            return await context.HostServices.RenderCardUriAsync<MediaCard>(model, media.ProviderId, media.MediaId, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            cover?.Dispose();
            avatar?.Dispose();
        }
    }

    internal static MediaCardViewModel CreateViewModel(ParsedMedia media, Bitmap? cover, Bitmap? avatar, DateTimeOffset now)
    {
        var title = string.IsNullOrWhiteSpace(media.Title) ? media.Description?.Trim() : media.Title.Trim();
        if (string.IsNullOrWhiteSpace(title)) title = "微信视频号视频";
        var description = media.Description?.Trim() ?? string.Empty;
        if (description == title || title.Contains(description, StringComparison.Ordinal)) description = string.Empty;

        var metaParts = new List<string>();
        if (DateTimeOffset.TryParse(Value(media, "publish_time"), out var time))
            metaParts.Add(time.ToOffset(TimeSpan.FromHours(8)).ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture) + " 发布");
        metaParts.Add("视频");

        // 视频号没有独立的话题字段，话题写在文案里（#露营 #星空）
        var tags = TopicRegex().Matches(title + " " + description).Select(match => match.Value).Distinct().Take(6).ToArray();

        return new MediaCardViewModel
        {
            Platform = MediaCardPlatform.WeixinChannels,
            Cover = cover,
            Avatar = avatar,
            BadgeText = int.TryParse(Value(media, "duration_seconds"), out var seconds) && seconds > 0
                ? MediaCardViewModel.FormatDuration(TimeSpan.FromSeconds(seconds)) : string.Empty,
            BadgeIcon = MediaCardIcons.Play,
            Title = TopicRegex().Replace(title, string.Empty).Trim() is { Length: > 0 } clean ? clean : title,
            Meta = string.Join(" · ", metaParts),
            Description = TopicRegex().Replace(description, string.Empty).Trim(),
            DescriptionMaxLines = 6,
            Tags = tags,
            Metrics =
            [
                new(MediaCardIcons.Heart, "点赞", MediaCardViewModel.FormatCount(Value(media, "likes"))),
                new(MediaCardIcons.Comment, "评论", MediaCardViewModel.FormatCount(Value(media, "comments"))),
                new(MediaCardIcons.Star, "收藏", MediaCardViewModel.FormatCount(Value(media, "favorites"))),
                new(MediaCardIcons.Share, "转发", MediaCardViewModel.FormatCount(Value(media, "shares"))),
            ],
            ExtraText = string.Empty,
            AuthorName = string.IsNullOrWhiteSpace(media.AuthorName) ? "视频号作者" : media.AuthorName,
            AuthorMeta = string.Empty,
        };
    }

    private async Task<Bitmap?> LoadImageAsync(string? url, ParsedMedia media, string suffix, CancellationToken cancellationToken, double avatarSize = 0)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        var image = await context.HostServices.BuildProviderImageAsync(new ProviderImageBuildRequest(
            media.ProviderName, url, media.SourceUrl, $"weixinchannels_{media.MediaId}_{suffix}", PersistLocalFile: true), cancellationToken).ConfigureAwait(false);
        if (avatarSize > 0)
            return !string.IsNullOrWhiteSpace(image.LocalPath)
                ? AvatarBitmapResizer.DecodeFile(image.LocalPath, avatarSize)
                : AvatarBitmapResizer.DecodeBase64(image.Uri, avatarSize);
        return !string.IsNullOrWhiteSpace(image.LocalPath)
            ? context.HostServices.DecodeImageFileForRender(image.LocalPath)
            : context.HostServices.DecodeBase64ImageForRender(image.Uri);
    }

    private static string Value(ParsedMedia media, string key) => media.Attributes.GetValueOrDefault(key, string.Empty);

    [GeneratedRegex(@"#[^\s#]+")]
    private static partial Regex TopicRegex();
}
