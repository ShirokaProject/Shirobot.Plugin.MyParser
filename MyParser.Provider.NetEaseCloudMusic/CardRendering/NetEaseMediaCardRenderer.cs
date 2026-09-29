using Avalonia.Media.Imaging;
using MyParser.Provider.NetEaseCloudMusic.Views;
using ShiroBot.AvaloniaSdk;
using ShiroBot.SDK.Plugin;
using Shirobot.Plugin.MyParser.Parsing;

namespace MyParser.Provider.NetEaseCloudMusic;

internal sealed class NetEaseMediaCardRenderer(ProviderCardRenderContext context) : IProviderMediaCardRenderer
{
    public async Task<string?> RenderAsync(ParsedMedia media, ProviderCardPurpose purpose, CancellationToken cancellationToken = default)
    {
        if (media.Kind != ParsedMediaKind.Track) return null;

        Bitmap? cover = null;
        try
        {
            if (!string.IsNullOrWhiteSpace(media.CoverUrl))
            {
                var image = await context.HostServices.BuildProviderImageAsync(new ProviderImageBuildRequest(
                    media.ProviderName, media.CoverUrl, media.SourceUrl, $"netease_{media.MediaId}_cover", PersistLocalFile: true), cancellationToken).ConfigureAwait(false);
                cover = !string.IsNullOrWhiteSpace(image.LocalPath)
                    ? context.HostServices.DecodeImageFileForRender(image.LocalPath)
                    : context.HostServices.DecodeBase64ImageForRender(image.Uri);
            }

            byte[] png;
            if (purpose == ProviderCardPurpose.Lyrics)
            {
                var lyrics = media.Attributes.GetValueOrDefault("lyrics", "暂无歌词");
                var translated = media.Attributes.GetValueOrDefault("translated_lyrics");
                if (!string.IsNullOrWhiteSpace(translated)) lyrics += Environment.NewLine + translated;
                var viewModel = new NetEaseLyricCardViewModel
                {
                    Cover = cover,
                    Title = media.Title ?? string.Empty,
                    Artists = media.AuthorName ?? string.Empty,
                    Album = media.Attributes.GetValueOrDefault("album", string.Empty),
                    LyricText = lyrics,
                };
                png = await context.BotContext.RenderControlPngAsync<NetEaseLyricCard>(viewModel, new ControlRenderOptions(RenderTheme.Auto)).ConfigureAwait(false);
            }
            else
            {
                var viewModel = new NetEaseMusicCardViewModel
                {
                    Cover = cover,
                    Title = media.Title ?? string.Empty,
                    Artists = media.AuthorName ?? string.Empty,
                    Album = media.Attributes.GetValueOrDefault("album", string.Empty),
                    QualityText = media.Attributes.GetValueOrDefault("quality", string.Empty),
                    SizeText = FormatSize(media.Attributes.GetValueOrDefault("file_size", string.Empty)),
                    BitrateText = FormatBitrate(media.Attributes.GetValueOrDefault("bitrate", string.Empty)),
                    SongIdText = media.MediaId,
                };
                png = await context.BotContext.RenderControlPngAsync<NetEaseMusicCard>(viewModel, new ControlRenderOptions(RenderTheme.Auto)).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            return "base64://" + Convert.ToBase64String(png);
        }
        finally
        {
            cover?.Dispose();
        }
    }

    private static string FormatSize(string value)
    {
        if (!long.TryParse(value, out var bytes)) return "未知";
        if (bytes >= 1024L * 1024L * 1024L) return $"{bytes / 1024d / 1024d / 1024d:F2}GB";
        if (bytes >= 1024L * 1024L) return $"{bytes / 1024d / 1024d:F2}MB";
        if (bytes >= 1024L) return $"{bytes / 1024d:F1}KB";
        return bytes + "B";
    }
    private static string FormatBitrate(string value) => int.TryParse(value, out var bitrate) ? $"{bitrate} kbps" : "未知";
}
