using Avalonia.Media.Imaging;
using ShiroBot.AvaloniaSdk;
using ShiroBot.SDK.Abstractions;
using ShiroBot.SDK.Plugin;
using Shirobot.Plugin.MyParser.Parsing;
using Shirobot.Plugin.MyParser.Views;

namespace Shirobot.Plugin.MyParser.MessageHandling;

internal sealed class ProviderMediaCardRenderer(IBotContext context, IProviderHostServices hostServices)
{
    public async Task<string?> RenderAsync(ParsedMedia media, CancellationToken cancellationToken)
    {
        string? fallbackUri = null;
        Bitmap? cover = null;
        try
        {
            if (!string.IsNullOrWhiteSpace(media.CoverUrl))
            {
                var image = await hostServices.BuildProviderImageAsync(new ProviderImageBuildRequest(
                    media.ProviderName, media.CoverUrl, media.SourceUrl, $"{media.ProviderId}_{media.MediaId}_card_cover",
                    PersistLocalFile: true), cancellationToken).ConfigureAwait(false);
                fallbackUri = image.Uri;
                cover = !string.IsNullOrWhiteSpace(image.LocalPath)
                    ? hostServices.DecodeImageFileForRender(image.LocalPath)
                    : hostServices.DecodeBase64ImageForRender(image.Uri);
            }

            var metadata = media.Attributes
                .Where(pair => !string.IsNullOrWhiteSpace(pair.Value)
                    && pair.Key is not ("lyrics" or "translated_lyrics")
                    && !pair.Key.Contains("url", StringComparison.OrdinalIgnoreCase)
                    && !pair.Key.Contains("cookie", StringComparison.OrdinalIgnoreCase)
                    && !pair.Key.Contains("token", StringComparison.OrdinalIgnoreCase))
                .Take(5)
                .Select(pair => $"{pair.Key}: {pair.Value}");
            var viewModel = new MediaCardViewModel
            {
                Cover = cover,
                ProviderName = media.ProviderName,
                KindName = media.Kind.ToString(),
                Title = media.Title ?? media.MediaId,
                Author = media.AuthorName ?? string.Empty,
                Description = media.Description ?? string.Empty,
                Metadata = string.Join("  ·  ", metadata),
            };
            var png = await context.RenderControlPngAsync<MediaCard>(viewModel, new ControlRenderOptions(RenderTheme.Auto)).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return "base64://" + Convert.ToBase64String(png);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            BotLog.Warning($"MyParser 统一媒体卡片渲染失败: provider={media.ProviderId}, media_id={media.MediaId}, error={ex.Message}");
            return fallbackUri;
        }
        finally
        {
            cover?.Dispose();
        }
    }
}
