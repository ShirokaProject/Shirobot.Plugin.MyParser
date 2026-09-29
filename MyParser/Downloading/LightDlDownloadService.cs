using System.Diagnostics;
using System.Net;
using LightDl;
using Shirobot.Plugin.MyParser.Parsing;
using ShiroBot.SDK.Abstractions;

namespace Shirobot.Plugin.MyParser.Downloading;

internal sealed class LightDlDownloadService(DownloadProgressLogger progressLogger)
{
    public async Task<long> DownloadAsync(
        HttpRangeDownloadRequest request,
        (long? ContentLength, bool AcceptRanges) probe,
        CancellationToken cancellationToken)
    {
        using var sourceRequest = request.CreateRequest(HttpMethod.Get, null);
        if (sourceRequest.Method != HttpMethod.Get)
            throw new NotSupportedException("LightDl downloads HTTP GET resources only.");

        var url = sourceRequest.RequestUri ?? new Uri(request.Url, UriKind.Absolute);
        var headers = GetDownloadHeaders(sourceRequest);
        var parallel = request.EnableParallel
                       && probe.AcceptRanges
                       && request.SegmentCount > 1
                       && (probe.ContentLength is null or <= 0 || probe.ContentLength >= request.MinParallelBytes);
        var chunkCount = parallel ? Math.Clamp(request.SegmentCount, 1, 64) : 1;
        long? responseLimit = request.MaxBytes == long.MaxValue ? null : request.MaxBytes;
        var config = new LightDownloadConfig
        {
            ChunkCount = chunkCount,
            MaxChunkCount = chunkCount,
            EnableDynamicConcurrency = false,
            EnableDynamicSegmentSize = true,
            EnableResume = true,
            DetectChallengePages = true,
            FileConflictPolicy = LightDownloadFileConflictPolicy.Overwrite,
            ProgressIntervalMs = Math.Clamp(progressLogger.IntervalMilliseconds, 100, 30_000),
            UserAgent = headers.TryGetValue("User-Agent", out var userAgent) ? userAgent : "Shirobot.Plugin.MyParser",
            HttpMessageHandlerFactory = () => new DownloadSizeLimitHandler(
                SafeHttpTransport.CreateHandler(DecompressionMethods.None), responseLimit),
        };

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(request.Path)) ?? AppContext.BaseDirectory);
        progressLogger.LogStart(request.MediaId, request.Path, probe.ContentLength, "LightDl");
        var stopwatch = Stopwatch.StartNew();
        long nextLogAtTicks = 0;
        long reportedTooLargeSize = -1;
        var exceededUnknownLengthLimit = 0;
        using var limitCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var progress = new InlineProgress<LightDownloadProgress>(value =>
        {
            if (request.MaxBytes != long.MaxValue && value.DownloadedBytes > request.MaxBytes)
            {
                Interlocked.Exchange(ref exceededUnknownLengthLimit, 1);
                limitCts.Cancel();
                return;
            }

            progressLogger.LogProgressThreadSafe(
                "LightDl", request.MediaId, value.DownloadedBytes,
                value.TotalBytes > 0 ? value.TotalBytes : null,
                stopwatch.Elapsed, ref nextLogAtTicks);
        });

        try
        {
            using var downloader = new LightDownloader(config);
            var lightRequest = LightDownloadRequest.ToFile(url, request.Path, headers)
                .OnFileInfo(info =>
                {
                    if (request.MaxBytes != long.MaxValue && info.Size > request.MaxBytes)
                    {
                        Interlocked.Exchange(ref reportedTooLargeSize, info.Size);
                        limitCts.Cancel();
                    }
                });
            var result = await downloader.DownloadAsync(lightRequest, progress, cancellationToken: limitCts.Token)
                .ConfigureAwait(false);

            if (request.MaxBytes != long.MaxValue && result.Size > request.MaxBytes)
            {
                CleanupFailedDownload(request.Path);
                throw request.CreateTooLargeException(result.Size);
            }

            progressLogger.LogComplete(request.MediaId, result.FilePath, result.Size, stopwatch.Elapsed);
            return result.Size;
        }
        catch (DownloadSizeLimitExceededException)
        {
            CleanupFailedDownload(request.Path);
            throw request.CreateExceededLimitException();
        }
        catch (LightDownloadException ex)
        {
            throw new HttpRequestException(ex.Message, ex.InnerException ?? ex);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested
                                                  && Interlocked.Read(ref reportedTooLargeSize) >= 0)
        {
            CleanupFailedDownload(request.Path);
            throw request.CreateTooLargeException(Interlocked.Read(ref reportedTooLargeSize));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested
                                                  && Interlocked.CompareExchange(ref exceededUnknownLengthLimit, 0, 0) == 1)
        {
            CleanupFailedDownload(request.Path);
            throw request.CreateExceededLimitException();
        }
    }

    private static IReadOnlyDictionary<string, string> GetDownloadHeaders(HttpRequestMessage request)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, values) in request.Headers)
        {
            if (name is "Range" or "Host" or "Connection" or "Accept-Encoding") continue;
            headers[name] = string.Equals(name, "Cookie", StringComparison.OrdinalIgnoreCase)
                ? string.Join("; ", values)
                : string.Join(", ", values);
        }

        return headers;
    }

    private static void CleanupFailedDownload(string path)
    {
        var directory = Path.GetDirectoryName(path) ?? string.Empty;
        var metadataPath = Path.Combine(directory, "." + Path.GetFileName(path) + ".lightdl.meta");
        foreach (var candidate in new[] { path, path + ".download", path + ".lightdl", path + ".lightdl.meta", path + ".lightdl.meta.tmp", metadataPath })
        {
            try
            {
                if (!File.Exists(candidate)) continue;
                File.SetAttributes(candidate, FileAttributes.Normal);
                File.Delete(candidate);
            }
            catch
            {
                // Best effort cleanup after a rejected oversized resource.
            }
        }
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    private sealed class DownloadSizeLimitHandler(HttpMessageHandler innerHandler, long? maxBytes)
        : DelegatingHandler(innerHandler)
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (maxBytes is > 0 && response.Content is { } content)
            {
                long responseBytes = 0;
                response.Content = new LimitedContent(content, count =>
                {
                    var total = Interlocked.Add(ref responseBytes, count);
                    if (total > maxBytes.Value)
                        throw new DownloadSizeLimitExceededException(total);
                });
            }
            return response;
        }
    }

    private sealed class LimitedContent : HttpContent
    {
        private readonly HttpContent _inner;
        private readonly Action<int> _countBytes;

        public LimitedContent(HttpContent inner, Action<int> countBytes)
        {
            _inner = inner;
            _countBytes = countBytes;
            foreach (var header in inner.Headers)
                Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            await using var source = await CreateCountedStreamAsync(CancellationToken.None).ConfigureAwait(false);
            await source.CopyToAsync(stream).ConfigureAwait(false);
        }

        protected override async Task SerializeToStreamAsync(
            Stream stream, TransportContext? context, CancellationToken cancellationToken)
        {
            await using var source = await CreateCountedStreamAsync(cancellationToken).ConfigureAwait(false);
            await source.CopyToAsync(stream, cancellationToken).ConfigureAwait(false);
        }

        protected override Task<Stream> CreateContentReadStreamAsync() => CreateCountedStreamAsync(CancellationToken.None);

        protected override Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken) =>
            CreateCountedStreamAsync(cancellationToken);

        protected override bool TryComputeLength(out long length)
        {
            length = _inner.Headers.ContentLength ?? 0;
            return _inner.Headers.ContentLength.HasValue;
        }

        private async Task<Stream> CreateCountedStreamAsync(CancellationToken cancellationToken) =>
            new CountingStream(await _inner.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false), _countBytes);

        protected override void Dispose(bool disposing)
        {
            if (disposing) _inner.Dispose();
            base.Dispose(disposing);
        }
    }

    private sealed class CountingStream(Stream inner, Action<int> countBytes) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = inner.Read(buffer, offset, count);
            if (read > 0) countBytes(read);
            return read;
        }

        public override int Read(Span<byte> buffer)
        {
            var read = inner.Read(buffer);
            if (read > 0) countBytes(read);
            return read;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read > 0) countBytes(read);
            return read;
        }

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            var read = await inner.ReadAsync(buffer.AsMemory(offset, count), cancellationToken).ConfigureAwait(false);
            if (read > 0) countBytes(read);
            return read;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            await inner.DisposeAsync().ConfigureAwait(false);
            GC.SuppressFinalize(this);
        }
    }

    private sealed class DownloadSizeLimitExceededException(long actualBytes)
        : Exception($"The download exceeded the configured maximum size ({actualBytes} bytes received).")
    {
        public long ActualBytes { get; } = actualBytes;
    }
}
