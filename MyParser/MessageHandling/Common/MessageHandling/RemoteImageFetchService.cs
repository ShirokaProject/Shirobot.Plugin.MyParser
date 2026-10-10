using System.Net;
using Shirobot.Plugin.MyParser.Parsing;
using Shirobot.Plugin.MyParser.Utility;
using ShiroBot.SDK.Abstractions;

namespace Shirobot.Plugin.MyParser.MessageHandling;

internal static class RemoteImageFetchService
{
    private const long DefaultMaxImageBytes = 10 * 1024L * 1024L;

    public static async Task<(string Uri, string? LocalPath)> BuildRemoteImageAsync(
        string platformName,
        string? imageUrl,
        string? referer,
        string filePrefix,
        Action<HttpRequestMessage>? configureRequest = null,
        long maxBytes = DefaultMaxImageBytes,
        bool persistLocalFile = false,
        string? httpProxy = null,
        CancellationToken cancellationToken = default,
        bool preferFileUri = false)
    {
        if (string.IsNullOrWhiteSpace(imageUrl))
        {
            return (string.Empty, null);
        }

        try
        {
            BotLog.Info($"MyParser {platformName} 图片下载开始: prefix={filePrefix}, source_url={imageUrl}, referer={referer}");
            using var http = CreateImageHttpClient(httpProxy);
            using var request = new HttpRequestMessage(HttpMethod.Get, imageUrl);
            configureRequest?.Invoke(request);

            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var contentLength = response.Content.Headers.ContentLength;
            if (contentLength is > 0 && contentLength > maxBytes)
            {
                BotLog.Warning($"MyParser {platformName} 图片过大，已跳过远程图片: url={imageUrl}, image_mb={contentLength.Value / 1024d / 1024d:F2}, limit_mb={maxBytes / 1024d / 1024d:F0}");
                return (string.Empty, null);
            }

            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var output = new MemoryStream();
            var buffer = new byte[64 * 1024];
            long total = 0;
            while (true)
            {
                var read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                total += read;
                if (total > maxBytes)
                {
                    BotLog.Warning($"MyParser {platformName} 图片下载超过限制，已跳过远程图片: url={imageUrl}, limit_mb={maxBytes / 1024d / 1024d:F0}");
                    return (string.Empty, null);
                }

                output.Write(buffer, 0, read);
            }

            // Encode/write directly from the stream's backing buffer instead of copying the image.
            var bytes = output.GetBuffer();
            var byteCount = checked((int)output.Length);
            var contentType = response.Content.Headers.ContentType?.MediaType;
            string? localPath = null;
            if (persistLocalFile)
            {
                try
                {
                    var cacheDirectory = Path.Combine(Path.GetTempPath(), "Shirobot.Plugin.MyParser", "images");
                    Directory.CreateDirectory(cacheDirectory);
                    var extension = ResolveImageExtension(contentType);
                    var safePrefix = SanitizeLocalFileName(filePrefix);
                    localPath = Path.Combine(cacheDirectory, $"{safePrefix}_{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}_{Guid.NewGuid():N}.{extension}");
                    await using var file = new FileStream(localPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
                    await file.WriteAsync(bytes.AsMemory(0, byteCount), cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    BotLog.Warning($"MyParser {platformName} 图片缓存落盘失败，继续使用 base64: prefix={filePrefix}, error={ex.Message}");
                    localPath = null;
                }
            }

            var uri = preferFileUri && localPath is not null
                ? new Uri(Path.GetFullPath(localPath)).AbsoluteUri
                : "base64://" + Convert.ToBase64String(bytes, 0, byteCount);
            var uriMode = preferFileUri && localPath is not null ? "file" : "base64";
            BotLog.Info($"MyParser {platformName} 图片下载完成: source_url={imageUrl}, content_type={contentType}, bytes={total}, mode={uriMode}, physical_path={localPath ?? "<none>"}");
            return (uri, localPath);
        }
        catch (Exception ex)
        {
            BotLog.Warning($"MyParser {platformName} 图片转 base64/本地文件失败，已跳过远程图片: url={imageUrl}, error={ex.Message}");
            return (string.Empty, null);
        }
    }

    public static string SanitizeLocalFileName(string value)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
        {
            value = value.Replace(c, '_');
        }

        return value;
    }

    private static string ResolveImageExtension(string? contentType)
    {
        return contentType?.ToLowerInvariant() switch
        {
            "image/png" => "png",
            "image/webp" => "webp",
            "image/gif" => "gif",
            "image/bmp" => "bmp",
            "image/avif" => "avif",
            _ => "jpg",
        };
    }

    public static HttpClient CreateImageHttpClient(string? httpProxy = null)
    {
        var proxy = HttpProxySettings.Create(httpProxy);
        if (proxy is not null)
        {
            return new HttpClient(new HttpClientHandler
            {
                Proxy = proxy,
                UseProxy = true,
                AutomaticDecompression = DecompressionMethods.All,
                AllowAutoRedirect = true,
            });
        }

        return new HttpClient(SafeHttpTransport.CreateHandler());
    }
}
