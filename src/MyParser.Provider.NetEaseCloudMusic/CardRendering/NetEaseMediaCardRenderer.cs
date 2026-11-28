using Shirobot.Plugin.MyParser.CardRendering;
using Avalonia.Media.Imaging;
using MyParser.Provider.NetEaseCloudMusic.Views;
using ShiroBot.AvaloniaSdk;
using ShiroBot.SDK.Plugin;
using Shirobot.Plugin.MyParser.Parsing;

namespace MyParser.Provider.NetEaseCloudMusic;

internal sealed class NetEaseMediaCardRenderer(ProviderCardRenderContext context) : IProviderMediaCardRenderer
{
    private const int MaxLyricLines = 10;

    public async Task<string?> RenderAsync(ParsedMedia media, ProviderCardPurpose purpose, CancellationToken cancellationToken = default)
    {
        if (media.Kind != ParsedMediaKind.Track || purpose != ProviderCardPurpose.Main) return null;

        Bitmap? cover = null;
        Bitmap? avatar = null;
        try
        {
            if (!string.IsNullOrWhiteSpace(media.CoverUrl))
            {
                var image = await context.HostServices.BuildProviderImageAsync(new ProviderImageBuildRequest(
                    media.ProviderName, media.CoverUrl, media.SourceUrl, $"netease_{media.MediaId}_cover", PersistLocalFile: true), cancellationToken).ConfigureAwait(false);
                cover = !string.IsNullOrWhiteSpace(image.LocalPath)
                    ? context.HostServices.DecodeImageFileForRender(image.LocalPath)
                    : context.HostServices.DecodeBase64ImageForRender(image.Uri);
                avatar = !string.IsNullOrWhiteSpace(image.LocalPath)
                    ? AvatarBitmapResizer.DecodeFile(image.LocalPath, 92)
                    : AvatarBitmapResizer.DecodeBase64(image.Uri, 92);
            }

            var viewModel = CreateViewModel(media, cover, avatar);
            return await context.HostServices.RenderCardUriAsync<NetEaseMusicCard>(viewModel, media.ProviderId, media.MediaId, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            cover?.Dispose();
            avatar?.Dispose();
        }
    }

    internal static NetEaseMusicCardViewModel CreateViewModel(ParsedMedia media, Bitmap? cover, Bitmap? avatar)
    {
        var (lyrics, credits) = NetEaseMusicCardViewModel.ParseLyrics(
            Value(media, "lyrics"), Value(media, "translated_lyrics"), MaxLyricLines);
        return new NetEaseMusicCardViewModel
        {
            Cover = cover,
            Avatar = avatar,
            Title = media.Title ?? string.Empty,
            Artists = media.AuthorName ?? string.Empty,
            Album = Value(media, "album"),
            DurationText = NetEaseMusicCardViewModel.FormatDuration(Value(media, "duration_ms")),
            Credits = credits,
            Lyrics = lyrics,
        };
    }

    private static string Value(ParsedMedia media, string key) => media.Attributes.GetValueOrDefault(key, string.Empty);
}
