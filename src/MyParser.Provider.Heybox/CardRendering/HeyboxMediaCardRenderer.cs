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
            var png = await context.BotContext.RenderControlPngAsync<HeyboxCard>(model, new ControlRenderOptions(RenderTheme.Auto)).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return "base64://" + Convert.ToBase64String(png);
        }
        finally
        {
            cover?.Dispose();
        }
    }

    private static string Value(ParsedMedia media, string key) => media.Attributes.GetValueOrDefault(key, string.Empty);
    private static string Pair(string label, string value) => string.IsNullOrWhiteSpace(value) ? string.Empty : $"{label} {value}";
}
