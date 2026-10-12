using Shirobot.Plugin.MyParser.CardRendering;
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
                    ? AvatarBitmapResizer.DecodeFile(image.LocalPath, 52)
                    : AvatarBitmapResizer.DecodeBase64(image.Uri, 52);
            }

            var viewModel = new NetEaseMusicCardViewModel
            {
                Cover = cover,
                Avatar = avatar,
                Title = media.Title ?? string.Empty,
                Artists = media.AuthorName ?? string.Empty,
                Album = media.Attributes.GetValueOrDefault("album", string.Empty),
                LyricText = string.Join(Environment.NewLine, BuildLyricPreview(media.Attributes.GetValueOrDefault("lyrics", string.Empty))),
                QualityText = media.Attributes.GetValueOrDefault("quality", string.Empty),
                SizeText = FormatSize(media.Attributes.GetValueOrDefault("file_size", string.Empty)),
                BitrateText = FormatBitrate(media.Attributes.GetValueOrDefault("bitrate", string.Empty)),
                SongIdText = media.MediaId,
            };
            return await context.HostServices.RenderCardUriAsync<NetEaseMusicCard>(viewModel, media.ProviderId, media.MediaId, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            cover?.Dispose();
            avatar?.Dispose();
        }
    }

    internal static IReadOnlyList<string> BuildLyricPreview(string lyrics)
    {
        var lines = lyrics.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => System.Text.RegularExpressions.Regex.Replace(line, @"^(?:\[[^\]]*\])+", string.Empty).Trim())
            .Where(line => !string.IsNullOrWhiteSpace(line)).Take(15).ToArray();
        return lines.Length > 0 ? lines : ["无歌词"];
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
