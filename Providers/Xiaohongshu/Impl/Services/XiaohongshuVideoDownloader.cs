using System.Text;
using Shirobot.Plugin.MyParser.Providers.Xiaohongshu.Infrastructure;
using Shirobot.Plugin.MyParser.Providers.Xiaohongshu.Models;
using Shirobot.Plugin.MyParser.Services;
using ShiroBot.SDK.Abstractions;

namespace Shirobot.Plugin.MyParser.Providers.Xiaohongshu.Impl.Services;

internal sealed class XiaohongshuVideoDownloader(PluginConfig config, HttpClient http)
{
    private readonly DownloadProgressLogger _progressLogger = new(config.LogDownloadProgress, 2, "MyParser", "note_id");

    public async Task<(string FileUri, string LocalPath)> DownloadVideoAsync(XiaohongshuParseResult result, CancellationToken cancellationToken = default)
    {
        var selected = result.SelectedVideo ?? throw new XiaohongshuParseException("没有可下载的小红书视频地址。");
        var cacheKey = $"xiaohongshu:{result.NoteId}:{selected.FormatId}:{selected.Width}x{selected.Height}:{selected.BitrateKbps:0}";
        var downloaded = await MyParserRuntime.GetOrAddVideoDownloadAsync(cacheKey, async () =>
        {
            var candidates = (selected.Urls.Count > 0 ? selected.Urls : [selected.Url]).Where(i => !string.IsNullOrWhiteSpace(i)).Distinct().ToArray();
            Exception? lastError = null;
            foreach (var url in candidates)
            {
                try
                {
                    return await DownloadCandidateAsync(url, result, selected, cancellationToken);
                }
                catch (Exception ex) when (ex is HttpRequestException or IOException or XiaohongshuParseException)
                {
                    lastError = ex;
                }
            }

            throw new XiaohongshuParseException("小红书视频下载失败：" + (lastError?.Message ?? "无可用地址"));
        });

        result.LocalVideoFileUri = downloaded.FileUri;
        result.LocalVideoPath = downloaded.LocalPath;
        return downloaded;
    }

    private async Task<(string FileUri, string LocalPath)> DownloadCandidateAsync(string url, XiaohongshuParseResult result, XiaohongshuVideoFormat format, CancellationToken cancellationToken)
    {
        var maxBytes = config.MaxVideoDownloadMegabytes <= 0 ? long.MaxValue : config.MaxVideoDownloadMegabytes * 1024L * 1024L;
        var dir = ResolveDownloadDirectory();
        Directory.CreateDirectory(dir);
        var ext = string.IsNullOrWhiteSpace(format.Ext) ? "mp4" : format.Ext.Trim('.');
        var path = Path.Combine(dir, $"xhs_{SanitizeFileName(result.NoteId)}_{DateTimeOffset.UtcNow:yyyyMMddHHmmss}.{ext}");
        const long minParallelBytes = 1;
        var segmentCount = Math.Clamp(config.ParallelDownloadThreads, 1, 64);
        var downloader = new MyParser.Services.Downloader(http, _progressLogger);
        var request = new HttpRangeDownloadRequest(
            url,
            path,
            result.NoteId,
            maxBytes,
            true,
            minParallelBytes,
            segmentCount,
            (method, range) => CreateVideoRequest(method, url, result, range),
            statusCode => new XiaohongshuParseException($"HTTP {(int)statusCode}"),
            bytes => new XiaohongshuParseException($"视频文件过大：{bytes / 1024 / 1024}MB > {config.MaxVideoDownloadMegabytes}MB"),
            () => new XiaohongshuParseException($"视频文件超过限制：{config.MaxVideoDownloadMegabytes}MB"),
            (index, statusCode) => new XiaohongshuParseException($"分片 {index} 不支持 Range：HTTP {(int)statusCode}"),
            (index, contentRange) => new XiaohongshuParseException($"分片 {index} Content-Range 不匹配：{contentRange}"),
            (index, copied, expected) => new XiaohongshuParseException($"分片 {index} 大小不匹配：{copied} != {expected}"),
            (total, expected) => new XiaohongshuParseException($"分片合并大小不一致：{total} != {expected}"),
            ex => BotLog.Warning($"MyParser 小红书下载进度: note_id={result.NoteId}, 并发下载失败，回退普通下载：{ex.Message}"));

        var total = await downloader.DownloadAsync(request, cancellationToken);
        if (total == 0)
        {
            throw new XiaohongshuParseException("下载到空文件");
        }

        await ValidateDownloadedVideoAsync(path, total, cancellationToken);
        return (new Uri(path).AbsoluteUri, path);
    }

    private HttpRequestMessage CreateVideoRequest(HttpMethod method, string url, XiaohongshuParseResult result, string? range)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.TryAddWithoutValidation("User-Agent", XiaohongshuConstants.UserAgent);
        request.Headers.TryAddWithoutValidation("Referer", result.SourceUrl ?? XiaohongshuConstants.Origin + "/");
        request.Headers.TryAddWithoutValidation("Accept", "video/webm,video/mp4,video/*;q=0.9,*/*;q=0.8");
        if (!string.IsNullOrWhiteSpace(range))
        {
            request.Headers.TryAddWithoutValidation("Range", range);
        }

        if (!string.IsNullOrWhiteSpace(MyParserRuntime.XiaohongshuCookie))
        {
            request.Headers.TryAddWithoutValidation("Cookie", MyParserRuntime.XiaohongshuCookie);
        }

        return request;
    }

    private string ResolveDownloadDirectory()
    {
        if (string.IsNullOrWhiteSpace(MyParserRuntime.XiaohongshuDownloadDirectory))
        {
            return Path.Combine(Path.GetTempPath(), "Shirobot.Plugin.MyParser", "xiaohongshu");
        }

        return Path.IsPathRooted(MyParserRuntime.XiaohongshuDownloadDirectory)
            ? MyParserRuntime.XiaohongshuDownloadDirectory
            : Path.Combine(AppContext.BaseDirectory, MyParserRuntime.XiaohongshuDownloadDirectory);
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
            throw new XiaohongshuParseException($"下载文件过小：{totalBytes} bytes");
        }

        await using var file = File.OpenRead(path);
        var header = new byte[Math.Min(4096, (int)Math.Min(file.Length, 4096))];
        var read = await file.ReadAsync(header, cancellationToken);
        var ascii = Encoding.ASCII.GetString(header, 0, read);
        if (!ascii.Contains("ftyp", StringComparison.Ordinal) && !ascii.Contains("webm", StringComparison.OrdinalIgnoreCase))
        {
            var sample = ascii.ReplaceLineEndings(" ");
            if (sample.Length > 120)
            {
                sample = sample[..120];
            }

            throw new XiaohongshuParseException($"下载文件不像视频，可能是风控页或错误内容：{sample}");
        }
    }
}
