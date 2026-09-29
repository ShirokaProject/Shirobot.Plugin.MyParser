using System.Diagnostics;
using System.Net;
using Shirobot.Plugin.MyParser.Parsing;
using ShiroBot.SDK.Abstractions;

namespace Shirobot.Plugin.MyParser.Services;

internal sealed class Downloader(DownloadProgressLogger progressLogger)
{
    private static readonly HttpClient DownloadHttp = new(SafeHttpTransport.CreateHandler(DecompressionMethods.None))
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

        return await DownloadWithHttpClientAsync(request, probe.ContentLength, cancellationToken);
    }

    public async Task<long> DownloadStreamAsync(HttpRangeDownloadRequest request, CancellationToken cancellationToken = default)
    {
        var probe = await ProbeAsync(request, cancellationToken);
        if (request.MaxBytes != long.MaxValue && probe.ContentLength is > 0 && probe.ContentLength > request.MaxBytes)
        {
            throw request.CreateTooLargeException(probe.ContentLength.Value);
        }

        return await DownloadWithHttpClientAsync(request, probe.ContentLength, cancellationToken);
    }

    private async Task<long> DownloadWithHttpClientAsync(HttpRangeDownloadRequest request, long? contentLength, CancellationToken cancellationToken)
    {
        BotLog.Info($"Downloading {request.Path} url:{request.Url}");
        var stopwatch = Stopwatch.StartNew();
        var nextLogAt = TimeSpan.Zero;
        const string mode = "safe-http";

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(request.Path)) ?? AppContext.BaseDirectory);
        CleanupFailedDownload(request.Path);
        progressLogger.LogStart(request.MediaId, request.Path, contentLength, mode);

        try
        {
            using var httpRequest = request.CreateRequest(HttpMethod.Get, null);
            using var response = await DownloadHttp.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw request.CreateHttpException(response.StatusCode);
            }

            var responseLength = response.Content.Headers.ContentLength;
            if (request.MaxBytes != long.MaxValue && responseLength is > 0 && responseLength > request.MaxBytes)
            {
                throw request.CreateTooLargeException(responseLength.Value);
            }

            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using var output = new FileStream(request.Path, FileMode.Create, FileAccess.Write, FileShare.None,
                128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var buffer = new byte[128 * 1024];
            long totalBytes = 0;
            while (true)
            {
                var read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0) break;
                totalBytes += read;
                if (request.MaxBytes != long.MaxValue && totalBytes > request.MaxBytes)
                {
                    throw request.CreateExceededLimitException();
                }

                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                progressLogger.LogProgress(mode, request.MediaId, totalBytes, responseLength ?? contentLength, stopwatch.Elapsed, ref nextLogAt);
            }

            if (totalBytes <= 0)
            {
                return 0;
            }

            if (request.MaxBytes != long.MaxValue && totalBytes > request.MaxBytes)
            {
                throw request.CreateExceededLimitException();
            }

            progressLogger.LogComplete(request.MediaId, request.Path, totalBytes, stopwatch.Elapsed);
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
