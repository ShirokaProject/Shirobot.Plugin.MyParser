using System.Diagnostics;
using System.Net;
using LightDl;
using Shirobot.Plugin.MyParser.Parsing;
using Shirobot.Plugin.MyParser.Utility;
using ShiroBot.SDK.Abstractions;

namespace Shirobot.Plugin.MyParser.Services;

internal sealed class Downloader(DownloadProgressLogger progressLogger)
{
    private static readonly HttpClient DownloadHttp = new(new SocketsHttpHandler
    {
        AutomaticDecompression = DecompressionMethods.None,
        MaxConnectionsPerServer = 128,
        PooledConnectionLifetime = TimeSpan.FromMinutes(10),
        PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
    })
    {
        Timeout = Timeout.InfiniteTimeSpan,
    };

    public async Task<long> DownloadAsync(HttpRangeDownloadRequest request, CancellationToken cancellationToken = default)
    {
        var probe = await ProbeAsync(request, cancellationToken);
        if (request.MaxBytes != long.MaxValue && probe.ContentLength is > 0 && probe.ContentLength > request.MaxBytes)
        {
            throw request.CreateTooLargeException(probe.ContentLength.Value);
        }

        return await DownloadWithLightDlAsync(request, probe.ContentLength, cancellationToken);
    }

    public async Task<long> DownloadStreamAsync(HttpRangeDownloadRequest request, CancellationToken cancellationToken = default)
    {
        var probe = await ProbeAsync(request, cancellationToken);
        if (request.MaxBytes != long.MaxValue && probe.ContentLength is > 0 && probe.ContentLength > request.MaxBytes)
        {
            throw request.CreateTooLargeException(probe.ContentLength.Value);
        }

        return await DownloadWithLightDlAsync(request, probe.ContentLength, cancellationToken);
    }

    private async Task<long> DownloadWithLightDlAsync(HttpRangeDownloadRequest request, long? contentLength, CancellationToken cancellationToken)
    {
        BotLog.Info($"Downloading {request.Path} url:{request.Url}");
        var stopwatch = Stopwatch.StartNew();
        var nextLogAt = TimeSpan.Zero;
        var segmentCount = Math.Clamp(request.SegmentCount, 0, 64);
        var initialCount = request.EnableParallel ? segmentCount : 1;
        int[] attempts = !request.RetryForbiddenWithLowerConcurrency || initialCount == 1
            ? [initialCount]
            : initialCount == 0
                ? [0, 8, 1]
                : initialCount > 8
                    ? [initialCount, 8, 1]
                    : [initialCount, 1];

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(request.Path)) ?? AppContext.BaseDirectory);
        CleanupFailedDownload(request.Path);
        for (var attempt = 0; attempt < attempts.Length; attempt++)
        {
            var workers = attempts[attempt];
            var mode = workers == 0 ? "lightdl/default" : $"lightdl/{workers}";
            progressLogger.LogStart(request.MediaId, request.Path, contentLength, mode);
            try
            {
                var lightRequest = LightDownloadRequest.ToFile(request.Url, request.Path, CreateHeaders(request))
                    .OnProgress(progress =>
                    {
                        if (request.MaxBytes != long.MaxValue && progress.DownloadedBytes > request.MaxBytes)
                            throw request.CreateExceededLimitException();

                        progressLogger.LogProgress(mode, request.MediaId, progress.DownloadedBytes, progress.TotalBytes > 0 ? progress.TotalBytes : contentLength, stopwatch.Elapsed, ref nextLogAt);
                    });
                var config = new LightDownloadConfig
                {
                    FileConflictPolicy = LightDownloadFileConflictPolicy.Overwrite,
                    EnableResume = false,
                };
                if (HttpProxySettings.Create(request.HttpProxy) is { } proxy)
                {
                    config.Proxy = proxy;
                    config.UseProxy = true;
                }
                if (workers > 0) config.ChunkCount = workers;

                using var downloader = new LightDownloader(config);
                var result = await downloader.DownloadAsync(lightRequest, cancellationToken);
                var totalBytes = new FileInfo(result.FilePath).Length;
                if (totalBytes <= 0) return 0;

                if (request.MaxBytes != long.MaxValue && totalBytes > request.MaxBytes)
                    throw request.CreateExceededLimitException();

                progressLogger.LogComplete(request.MediaId, result.FilePath, totalBytes, stopwatch.Elapsed);
                return totalBytes;
            }
            catch (Exception ex) when (attempt < attempts.Length - 1
                                       && request.RetryForbiddenWithLowerConcurrency
                                       && !cancellationToken.IsCancellationRequested
                                       && ex is LightDownloadException or HttpRequestException
                                       && (ex.Message.Contains("403", StringComparison.OrdinalIgnoreCase)
                                           || ex.Message.Contains("Forbidden", StringComparison.OrdinalIgnoreCase)))
            {
                CleanupFailedDownload(request.Path);
                BotLog.Warning($"MyParser 媒体流返回 403，降低 LightDl 并发重试: media_id={request.MediaId}, workers={workers}->{attempts[attempt + 1]}");
                await Task.Delay(300, cancellationToken);
            }
            catch
            {
                CleanupFailedDownload(request.Path);
                throw;
            }
        }

        throw new InvalidOperationException("所有下载并发设置均已尝试但未成功。");
    }

    public async Task<(long? ContentLength, bool AcceptRanges)> ProbeAsync(HttpRangeDownloadRequest request, CancellationToken cancellationToken)
    {
        using var proxyHttp = HttpProxySettings.Create(request.HttpProxy) is { } proxy
            ? new HttpClient(new SocketsHttpHandler { Proxy = proxy, UseProxy = true, AutomaticDecompression = DecompressionMethods.None })
            : null;
        for (var attempt = 0; ; attempt++)
        {
            using var httpRequest = request.CreateRequest(HttpMethod.Get, "bytes=0-0");
            using var response = await (proxyHttp ?? DownloadHttp).SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (response.StatusCode == HttpStatusCode.Forbidden
                && request.RetryForbiddenWithLowerConcurrency && attempt < 2)
            {
                await Task.Delay(300 * (attempt + 1), cancellationToken);
                continue;
            }
            if (!response.IsSuccessStatusCode)
                throw request.CreateHttpException(response.StatusCode);

            long? contentLength = response.Content.Headers.ContentRange?.Length ?? response.Content.Headers.ContentLength;
            var acceptRanges = response.StatusCode == HttpStatusCode.PartialContent
                               || response.Headers.AcceptRanges.Any(i => string.Equals(i, "bytes", StringComparison.OrdinalIgnoreCase))
                               || response.Content.Headers.ContentRange is not null;
            return (contentLength, acceptRanges);
        }
    }

    private static Dictionary<string, string> CreateHeaders(HttpRangeDownloadRequest request)
    {
        using var httpRequest = request.CreateRequest(HttpMethod.Get, null);
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var header in httpRequest.Headers)
        {
            headers[header.Key] = string.Join(", ", header.Value);
        }

        if (httpRequest.Content is not null)
        {
            foreach (var header in httpRequest.Content.Headers)
            {
                headers[header.Key] = string.Join(", ", header.Value);
            }
        }

        return headers;
    }

    private static void CleanupFailedDownload(string path)
    {
        TryDelete(path);
        TryDelete(path + ".download");
        TryDelete(path + ".lightdl");
        TryDelete(path + ".lightdl.meta");
        TryDelete(path + ".lightdl.meta.tmp");
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.SetAttributes(path, FileAttributes.Normal);
                File.Delete(path);
            }
        }
        catch
        {
            // best effort
        }
    }
}
