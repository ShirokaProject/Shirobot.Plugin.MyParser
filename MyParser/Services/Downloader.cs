using System.Diagnostics;
using System.Net;
using LightDl;
using Shirobot.Plugin.MyParser.Parsing;
using ShiroBot.SDK.Abstractions;

namespace Shirobot.Plugin.MyParser.Services;

internal sealed class Downloader(HttpClient http, DownloadProgressLogger progressLogger)
{
    private readonly HttpClient _ = http;
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
        var segmentCount = Math.Clamp(request.SegmentCount, 1, 64);
        var mode = request.EnableParallel && segmentCount > 1 ? $"lightdl/{segmentCount}" : "lightdl";

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(request.Path)) ?? AppContext.BaseDirectory);
        CleanupFailedDownload(request.Path);
        progressLogger.LogStart(request.MediaId, request.Path, contentLength, mode);

        try
        {
            var lightRequest = LightDownloadRequest.ToFile(request.Url, request.Path, CreateHeaders(request))
                .OnProgress(progress =>
                {
                    if (request.MaxBytes != long.MaxValue && progress.DownloadedBytes > request.MaxBytes)
                    {
                        throw request.CreateExceededLimitException();
                    }

                    progressLogger.LogProgress(mode, request.MediaId, progress.DownloadedBytes, progress.TotalBytes > 0 ? progress.TotalBytes : contentLength, stopwatch.Elapsed, ref nextLogAt);
                });
            var config = new LightDownloadConfig
            {
                ChunkCount = request.EnableParallel ? segmentCount : 1,
                FileConflictPolicy = LightDownloadFileConflictPolicy.Overwrite,
                EnableResume = false,
            };

            using var downloader = new LightDownloader(config);
            var result = await downloader.DownloadAsync(lightRequest, cancellationToken);
            var totalBytes = new FileInfo(result.FilePath).Length;
            if (totalBytes <= 0)
            {
                return 0;
            }

            if (request.MaxBytes != long.MaxValue && totalBytes > request.MaxBytes)
            {
                throw request.CreateExceededLimitException();
            }

            progressLogger.LogComplete(request.MediaId, result.FilePath, totalBytes, stopwatch.Elapsed);
            return totalBytes;
        }
        catch
        {
            CleanupFailedDownload(request.Path);
            throw;
        }
    }

    public async Task<(long? ContentLength, bool AcceptRanges)> ProbeAsync(HttpRangeDownloadRequest request, CancellationToken cancellationToken)
    {
        using var httpRequest = request.CreateRequest(HttpMethod.Get, "bytes=0-0");
        using var response = await DownloadHttp.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw request.CreateHttpException(response.StatusCode);
        }

        long? contentLength = response.Content.Headers.ContentRange?.Length ?? response.Content.Headers.ContentLength;
        var acceptRanges = response.StatusCode == HttpStatusCode.PartialContent
                           || response.Headers.AcceptRanges.Any(i => string.Equals(i, "bytes", StringComparison.OrdinalIgnoreCase))
                           || response.Content.Headers.ContentRange is not null;
        return (contentLength, acceptRanges);
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
