using System.Collections.Concurrent;
using System.Net;
using Shirobot.Plugin.MyParser.Parsing;
using Shirobot.Plugin.MyParser.Utility;
using ShiroBot.SDK.Abstractions;

namespace Shirobot.Plugin.MyParser.MessageHandling;

internal static class RemoteImageFetchService
{
    private const long DefaultMaxImageBytes = 10 * 1024L * 1024L;
    private const int ImageCacheCapacity = 256;
    private static readonly TimeSpan ImageCacheTtl = TimeSpan.FromSeconds(120);

    private static readonly HttpClient DirectImageHttp = CreateImageHttpClient(null);
    private static readonly ConcurrentDictionary<string, HttpClient> ProxiedImageHttp = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, CachedImage> RecentImages = new(StringComparer.Ordinal);

    private readonly record struct CachedImage(string Uri, string? LocalPath, DateTimeOffset CreatedAt);

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
            var cacheKey = BuildImageCacheKey(imageUrl, referer) + (preferFileUri ? "|file" : "|base64");
            if (RecentImages.TryGetValue(cacheKey, out var cached)
                && DateTimeOffset.UtcNow - cached.CreatedAt <= ImageCacheTtl
                && (!persistLocalFile || cached.LocalPath is not null && File.Exists(cached.LocalPath))
                && (!preferFileUri || cached.LocalPath is not null && File.Exists(cached.LocalPath)))
            {
                global::Shirobot.Plugin.MyParser.DiagnosticLog.Info($"MyParser {platformName} 图片下载缓存命中: prefix={filePrefix}, source_url={imageUrl}");
                var cachedUri = preferFileUri && cached.LocalPath is not null
                    ? new Uri(Path.GetFullPath(cached.LocalPath)).AbsoluteUri
                    : cached.Uri;
                return (cachedUri, cached.LocalPath);
            }

            global::Shirobot.Plugin.MyParser.DiagnosticLog.Info($"MyParser {platformName} 图片下载开始: prefix={filePrefix}, source_url={imageUrl}, referer={referer}");
            var http = GetImageHttpClient(httpProxy);
            using var request = new HttpRequestMessage(HttpMethod.Get, imageUrl);
            configureRequest?.Invoke(request);

            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var contentLength = response.Content.Headers.ContentLength;
            if (contentLength is > 0 && contentLength > maxBytes)
            {
                global::Shirobot.Plugin.MyParser.DiagnosticLog.Warning($"MyParser {platformName} 图片过大，已跳过远程图片: url={imageUrl}, image_mb={contentLength.Value / 1024d / 1024d:F2}, limit_mb={maxBytes / 1024d / 1024d:F0}");
                return (string.Empty, null);
            }

            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var contentType = response.Content.Headers.ContentType?.MediaType;
            string? localPath = null;
            if (persistLocalFile || preferFileUri)
            {
                try
                {
                    var cacheDirectory = MyParserRuntime.CreateTempDirectory();
                    Directory.CreateDirectory(cacheDirectory);
                    var extension = ResolveImageExtension(contentType);
                    var safePrefix = SanitizeLocalFileName(filePrefix);
                    localPath = Path.Combine(cacheDirectory, $"{safePrefix}_{Guid.NewGuid():N}.{extension}");
                }
                catch (Exception ex) when (!preferFileUri)
                {
                    global::Shirobot.Plugin.MyParser.DiagnosticLog.Warning($"MyParser 图片缓存目录创建失败，继续使用 base64: {ex.Message}");
                }
            }

            // File/HTTP transport never needs a payload-sized managed buffer.
            using var memory = preferFileUri ? null : new MemoryStream(
                contentLength is > 0 && contentLength <= maxBytes && contentLength <= int.MaxValue
                    ? (int)contentLength.Value : 0);
            long total = 0;
            try
            {
                await using var file = !preferFileUri || localPath is null ? null : new FileStream(localPath,
                    FileMode.CreateNew, FileAccess.Write, FileShare.Read, 65536, FileOptions.Asynchronous);
                var buffer = new byte[65536];
                while (true)
                {
                    var read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                    if (read == 0) break;
                    total += read;
                    if (total > maxBytes) throw new InvalidDataException("图片下载超过大小限制。");
                    if (file is not null) await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    memory?.Write(buffer, 0, read);
                }
            }
            catch
            {
                if (localPath is not null) File.Delete(localPath);
                throw;
            }
            var bytes = memory?.GetBuffer();
            var byteCount = checked((int)total);
            if (!preferFileUri && localPath is not null)
            {
                try
                {
                    await using var file = new FileStream(localPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
                    await file.WriteAsync(bytes!.AsMemory(0, byteCount), cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    File.Delete(localPath);
                    throw;
                }
                catch (Exception ex)
                {
                    File.Delete(localPath);
                    localPath = null;
                    global::Shirobot.Plugin.MyParser.DiagnosticLog.Warning($"MyParser 图片缓存落盘失败，继续使用 base64: {ex.Message}");
                }
            }
            var uri = preferFileUri && localPath is not null
                ? new Uri(Path.GetFullPath(localPath)).AbsoluteUri
                : "base64://" + Convert.ToBase64String(bytes!, 0, byteCount);
            var uriMode = preferFileUri && localPath is not null ? "file" : "base64";
            global::Shirobot.Plugin.MyParser.DiagnosticLog.Info($"MyParser {platformName} 图片下载完成: source_url={imageUrl}, content_type={contentType}, bytes={total}, mode={uriMode}, physical_path={localPath ?? "<none>"}");
            TryCacheImage(cacheKey, uri, localPath);
            return (uri, localPath);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex) when (NetworkRetry.IsTransient(ex)) { throw; }
        catch (Exception ex)
        {
            global::Shirobot.Plugin.MyParser.DiagnosticLog.Warning($"MyParser {platformName} 图片转 base64/本地文件失败，已跳过远程图片: url={imageUrl}, error={ex.Message}");
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
        return new HttpClient(HttpProxySettings.CreateHandler(httpProxy));
    }

    private static string BuildImageCacheKey(string imageUrl, string? referer) =>
        $"{referer?.Trim() ?? string.Empty}\u0000{imageUrl.Trim()}";

    private static void TryCacheImage(string key, string uri, string? localPath)
    {
        var now = DateTimeOffset.UtcNow;
        RecentImages[key] = new CachedImage(uri, localPath, now);
        if (RecentImages.Count <= ImageCacheCapacity)
        {
            return;
        }

        foreach (var pair in RecentImages.Where(pair => now - pair.Value.CreatedAt > ImageCacheTtl))
        {
            RecentImages.TryRemove(pair.Key, out _);
        }

        var overflow = RecentImages.Count - ImageCacheCapacity;
        if (overflow <= 0)
        {
            return;
        }

        foreach (var pair in RecentImages.OrderBy(pair => pair.Value.CreatedAt).Take(overflow))
        {
            RecentImages.TryRemove(pair.Key, out _);
        }
    }

    private static HttpClient GetImageHttpClient(string? httpProxy)
    {
        return string.IsNullOrWhiteSpace(httpProxy)
            ? DirectImageHttp
            : ProxiedImageHttp.GetOrAdd(httpProxy, address => CreateImageHttpClient(address));
    }
}
