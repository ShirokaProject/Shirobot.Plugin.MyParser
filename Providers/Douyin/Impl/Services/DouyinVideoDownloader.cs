using Shirobot.Plugin.MyParser.Providers.Douyin.Infrastructure;
using Shirobot.Plugin.MyParser.Providers.Douyin.Models;
using System.Text;
using ShiroBot.SDK.Abstractions;
using Shirobot.Plugin.MyParser.Services;
using static Shirobot.Plugin.MyParser.Providers.Douyin.Infrastructure.DouyinRequestHeaders;

namespace Shirobot.Plugin.MyParser.Providers.Douyin.Impl.Services;

internal sealed class DouyinVideoDownloader(PluginConfig config, HttpClient http)
{
    private readonly DownloadProgressLogger _progressLogger = new(config.LogDownloadProgress, 2, "MyParser", "aweme_id");

    public async Task<(string FileUri, string LocalPath)> DownloadVideoAsync(DouyinParseResult result, CancellationToken cancellationToken = default)
    {
        var quality = result.Qualities.FirstOrDefault();
        if (quality is null || string.IsNullOrWhiteSpace(result.VideoUrl))
        {
            throw new DouyinParseException("没有可下载的视频地址。");
        }

        var cacheKey = $"douyin:{result.AwemeId}:{quality.GearName}:{quality.Ratio}:{quality.Codec}";
        var downloaded = await MyParserRuntime.GetOrAddVideoDownloadAsync(cacheKey, async () =>
        {
            var candidates = BuildVideoDownloadCandidates(result, quality).Distinct().ToArray();
            Exception? lastError = null;
            foreach (var url in candidates)
            {
                try
                {
                    return await DownloadVideoCandidateAsync(url, result, cancellationToken);
                }
                catch (Exception ex) when (ex is HttpRequestException or IOException or DouyinParseException)
                {
                    lastError = ex;
                }
            }

            throw new DouyinParseException("视频下载失败：" + (lastError?.Message ?? "无可用地址"));
        });

        result.LocalVideoFileUri = downloaded.FileUri;
        result.LocalVideoPath = downloaded.LocalPath;
        return downloaded;
    }

    private async Task<(string FileUri, string LocalPath)> DownloadVideoCandidateAsync(string url, DouyinParseResult result, CancellationToken cancellationToken)
    {
        var maxBytes = config.MaxVideoDownloadMegabytes <= 0
            ? long.MaxValue
            : config.MaxVideoDownloadMegabytes * 1024L * 1024L;
        var dir = ResolveDownloadDirectory();
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"douyin_{SanitizeFileName(result.AwemeId)}_{DateTimeOffset.UtcNow:yyyyMMddHHmmss}.mp4");

        const long minParallelBytes = 1;
        var segmentCount = Math.Clamp(config.ParallelDownloadThreads, 1, 64);
        var downloader = new MyParser.Services.Downloader(http, _progressLogger);
        var request = new HttpRangeDownloadRequest(
            url,
            path,
            result.AwemeId,
            maxBytes,
            true,
            minParallelBytes,
            segmentCount,
            (method, range) => CreateVideoRequest(method, url, result, range),
            statusCode => new DouyinParseException($"HTTP {(int)statusCode}"),
            bytes => new DouyinParseException($"视频文件过大：{bytes / 1024 / 1024}MB > {config.MaxVideoDownloadMegabytes}MB"),
            () => new DouyinParseException($"视频文件超过限制：{config.MaxVideoDownloadMegabytes}MB"),
            (index, statusCode) => new DouyinParseException($"分片 {index} 不支持 Range：HTTP {(int)statusCode}"),
            (index, contentRange) => new DouyinParseException($"分片 {index} Content-Range 不匹配：{contentRange}"),
            (index, copied, expected) => new DouyinParseException($"分片 {index} 大小不匹配：{copied} != {expected}"),
            (total, expected) => new DouyinParseException($"分片合并大小不一致：{total} != {expected}"),
            ex => BotLog.Warning($"MyParser 下载进度: aweme_id={result.AwemeId}, 并发下载失败，回退普通下载：{ex.Message}"));

        var total = await downloader.DownloadAsync(request, cancellationToken);
        if (total == 0)
        {
            throw new DouyinParseException("下载到空文件");
        }

        await ValidateDownloadedVideoAsync(path, total, cancellationToken);
        return (new Uri(path).AbsoluteUri, path);
    }

    private HttpRequestMessage CreateVideoRequest(HttpMethod method, string url, DouyinParseResult result, string? range)
    {
        var request = new HttpRequestMessage(method, url);
        ApplyDefaultHeaders(request, result.SourceUrl ?? DouyinConstants.HomeUrl);
        if (!string.IsNullOrWhiteSpace(range))
        {
            request.Headers.TryAddWithoutValidation("Range", range);
        }

        if (!string.IsNullOrWhiteSpace(MyParserRuntime.DouyinCookie))
        {
            request.Headers.TryAddWithoutValidation("Cookie", MyParserRuntime.DouyinCookie);
        }

        return request;
    }

    private static IEnumerable<string> BuildVideoDownloadCandidates(DouyinParseResult result, DouyinVideoQuality quality)
    {
        yield return quality.Url;

        if (!string.IsNullOrWhiteSpace(quality.Uri))
        {
            var firstRatio = quality.Ratio switch
            {
                "2k" => quality.Height >= 2160 || quality.Width >= 3840 ? "2160p" : "1440p",
                _ => quality.Ratio,
            };
            var ratios = new[] { firstRatio, "1440p", "1080p", "720p", "540p", "480p" }
                .Where(i => !string.IsNullOrWhiteSpace(i) && i != "默认")
                .Distinct(StringComparer.OrdinalIgnoreCase);
            foreach (var ratio in ratios)
            {
                yield return $"https://aweme.snssdk.com/aweme/v1/play/?video_id={Uri.EscapeDataString(quality.Uri!)}&ratio={Uri.EscapeDataString(ratio)}&line=0";
            }
        }
    }

    private string ResolveDownloadDirectory()
    {
        if (string.IsNullOrWhiteSpace(MyParserRuntime.DownloadDirectory))
        {
            return Path.Combine(Path.GetTempPath(), "Shirobot.Plugin.MyParser", "douyin");
        }

        return Path.IsPathRooted(MyParserRuntime.DownloadDirectory)
            ? MyParserRuntime.DownloadDirectory
            : Path.Combine(AppContext.BaseDirectory, MyParserRuntime.DownloadDirectory);
    }

    private static string SanitizeFileName(string value)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
        {
            value = value.Replace(c, '_');
        }

        return value;
    }

    private static async Task ValidateDownloadedVideoAsync(string path, long totalBytes, CancellationToken cancellationToken)
    {
        if (totalBytes < 1024)
        {
            throw new DouyinParseException($"下载文件过小：{totalBytes} bytes");
        }

        await using var file = File.OpenRead(path);
        var header = new byte[Math.Min(4096, (int)Math.Min(file.Length, 4096))];
        var read = await file.ReadAsync(header, cancellationToken);
        var ascii = Encoding.ASCII.GetString(header, 0, read);

        if (!ascii.Contains("ftyp", StringComparison.Ordinal))
        {
            var sample = ascii.ReplaceLineEndings(" ");
            if (sample.Length > 120)
            {
                sample = sample[..120];
            }

            throw new DouyinParseException($"下载文件不像 MP4，可能是风控页或错误内容：{sample}");
        }
    }
}
