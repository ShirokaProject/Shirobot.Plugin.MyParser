using System.Diagnostics;
using System.Text;
using SilkCodec.NET;
using Shirobot.Plugin.MyParser.Parsing;
using Shirobot.Plugin.MyParser.Utility;
using ShiroBot.SDK.Abstractions;

namespace Shirobot.Plugin.MyParser.Services;

internal sealed class ProviderDownloadService
{
    public Task<(string FileUri, string LocalPath)> DownloadProviderVideoAsync(
        PluginConfig config,
        ProviderVideoDownloadRequest request,
        CancellationToken cancellationToken = default)
    {
        return MyParserRuntime.GetOrAddVideoDownloadAsync(request.CacheKey, async () =>
        {
            var candidates = request.CandidateUrls.Where(i => !string.IsNullOrWhiteSpace(i)).Distinct().ToArray();
            if (candidates.Length == 0)
            {
                throw new InvalidOperationException($"{request.PlatformDisplayName} 没有可下载的视频地址。");
            }

            Exception? lastError = null;
            foreach (var url in candidates)
            {
                try
                {
                    return await DownloadProviderVideoCandidateAsync(config, request, url, cancellationToken);
                }
                catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or InvalidOperationException)
                {
                    lastError = ex;
                }
            }

            throw new InvalidOperationException($"{request.PlatformDisplayName} 视频下载失败：{lastError?.Message ?? "无可用地址"}");
        });
    }

    public Task<ProviderLiveReplayClipDownloadResult> DownloadLiveReplayClipAsync(
        PluginConfig config,
        ProviderLiveReplayClipDownloadRequest request,
        CancellationToken cancellationToken = default)
    {
        var downloader = new LiveReplayClipDownloader(config, this);
        return downloader.DownloadAsync(request, cancellationToken);
    }

    public Task<(string FileUri, string LocalPath)> DownloadProviderAudioAsync(
        PluginConfig config,
        ProviderAudioDownloadRequest request,
        CancellationToken cancellationToken = default)
    {
        return MyParserRuntime.GetOrAddVideoDownloadAsync(request.CacheKey, () => DownloadProviderAudioCoreAsync(config, request, cancellationToken));
    }

    public async Task<IReadOnlyList<ProviderRecordVariant>> BuildSilkRecordVariantsAsync(
        PluginConfig config,
        ProviderRecordBuildRequest request,
        CancellationToken cancellationToken = default)
    {
        var directory = Path.Combine(Path.GetTempPath(), "Shirobot.Plugin.MyParser", request.PlatformId, "silk");
        Directory.CreateDirectory(directory);
        var safeBaseName = SanitizeFileName($"{request.FileNamePrefix}_{request.MediaId}", 120);

        var mobile = new ProviderRecordVariant(
            "mobile-best",
            "手机最优",
            "pcm_rate=48000 silk_rate=100000 max=24000 packet=20 tencent=true decoder=NLayer resampler=managed encoder=SilkCodec.NET/1.0.1",
            Path.Combine(directory, safeBaseName + "_mobile_48k_100k_silkcodecnet_101.silk"),
            100000);
        var pc = new ProviderRecordVariant(
            "pc-best",
            "电脑最优",
            "pcm_rate=48000 silk_rate=35000 max=24000 packet=20 tencent=true decoder=NLayer resampler=managed encoder=SilkCodec.NET",
            Path.Combine(directory, safeBaseName + "_pc_48k_35000_silkcodecnet_full.silk"),
            35000);

        var variants = request.IncludeMobileBest
            ? new[] { pc, mobile }
            : new[] { pc };

        foreach (var variant in variants)
        {
            if (File.Exists(variant.Path) && new FileInfo(variant.Path).Length > 0)
            {
                continue;
            }

            await EncodeSilkAsync(request, variant.Path, variant.SilkRate, cancellationToken).ConfigureAwait(false);
        }

        return variants;
    }

    public static async Task<string> BuildRecordUriAsync(string localPath, CancellationToken cancellationToken = default)
    {
        var bytes = await File.ReadAllBytesAsync(localPath, cancellationToken).ConfigureAwait(false);
        return "base64://" + Convert.ToBase64String(bytes);
    }

    public async Task<(string FileUri, string LocalPath)> DownloadMuxedProviderVideoAsync(
        PluginConfig config,
        ProviderMuxedVideoDownloadRequest request,
        CancellationToken cancellationToken = default)
    {
        var (video, audio, estimatedBytes) = await SelectMuxedDownloadStreamsAsync(config, request, cancellationToken);
        if (estimatedBytes is not null)
        {
            BotLog.Info($"MyParser {request.PlatformDisplayName} 下载前大小预估: {request.IdentifierName}={request.MediaId}, video={FormatSize(video.EstimatedBytes)}, audio={FormatSize(audio.EstimatedBytes)}, total={FormatSize(estimatedBytes.Value)}, limit={config.MaxVideoDownloadMegabytes}MB");
        }

        var cacheKey = $"{request.CacheKeyPrefix}:v{video.Stream.QualityId}:{video.Stream.CodecName}:a{audio.Stream.StreamId}";
        return await MyParserRuntime.GetOrAddVideoDownloadAsync(cacheKey, () => DownloadMuxedProviderVideoCoreAsync(config, request, video.Stream with { Url = video.Url, BackupUrls = [] }, audio.Stream with { Url = audio.Url, BackupUrls = [] }, cancellationToken));
    }

    public Task<long> DownloadAsync(
        HttpRangeDownloadRequest request,
        bool logProgress,
        int intervalSeconds,
        string logPrefix,
        string identifierName,
        CancellationToken cancellationToken = default)
    {
        var downloader = new Downloader(new HttpClient(), new DownloadProgressLogger(logProgress, intervalSeconds, logPrefix, identifierName));
        return downloader.DownloadAsync(request, cancellationToken);
    }

    public Task<(long? ContentLength, bool AcceptRanges)> ProbeDownloadAsync(
        HttpRangeDownloadRequest request,
        bool logProgress,
        int intervalSeconds,
        string logPrefix,
        string identifierName,
        CancellationToken cancellationToken = default)
    {
        var downloader = new Downloader(new HttpClient(), new DownloadProgressLogger(logProgress, intervalSeconds, logPrefix, identifierName));
        return downloader.ProbeAsync(request, cancellationToken);
    }

    private async Task<(MuxedStreamProbe Video, MuxedStreamProbe Audio, long? EstimatedBytes)> SelectMuxedDownloadStreamsAsync(
        PluginConfig config,
        ProviderMuxedVideoDownloadRequest request,
        CancellationToken cancellationToken)
    {
        var videos = request.VideoStreams.Count > 0 ? request.VideoStreams : throw new InvalidOperationException($"{request.PlatformDisplayName} 没有可下载的视频流。");
        var audios = request.AudioStreams.Count > 0 ? request.AudioStreams : throw new InvalidOperationException($"{request.PlatformDisplayName} 没有可下载的音频流。");
        var maxBytes = GetMaxBytes(config);
        MuxedStreamProbe? firstVideoProbe = null;
        var audioProbe = await SelectMuxedAudioProbeAsync(config, request, audios, maxBytes, cancellationToken);
        foreach (var video in config.AutoFallbackQualityBySize ? videos : videos.Take(1))
        {
            var videoProbe = await ProbeMuxedStreamAsync(config, request, video, "视频流", maxBytes, cancellationToken);
            firstVideoProbe ??= videoProbe;
            var estimated = EstimateTotalBytes(videoProbe.EstimatedBytes, audioProbe.EstimatedBytes);
            if (IsWithinLimit(videoProbe.EstimatedBytes, maxBytes)
                && IsWithinLimit(audioProbe.EstimatedBytes, maxBytes)
                && IsWithinLimit(estimated, maxBytes))
            {
                if (!ReferenceEquals(videoProbe.Stream, videos[0]) || !ReferenceEquals(audioProbe.Stream, audios[0]))
                {
                    BotLog.Info($"MyParser {request.PlatformDisplayName} 因文件大小自动降级: {request.IdentifierName}={request.MediaId}, quality={videoProbe.Stream.QualityName}, fps={videoProbe.Stream.Fps}, size={videoProbe.Stream.Width}x{videoProbe.Stream.Height}, codec={videoProbe.Stream.CodecName}, estimated_total={FormatSize(estimated)}");
                }

                return (videoProbe, audioProbe, estimated);
            }

            if (!config.AutoFallbackQualityBySize)
            {
                ThrowMuxedTooLarge(request, videoProbe, audioProbe, estimated, maxBytes);
            }
        }

        var fallbackVideo = firstVideoProbe ?? throw new InvalidOperationException($"{request.PlatformDisplayName} 没有可下载的视频流。");
        ThrowMuxedTooLarge(request, fallbackVideo, audioProbe, EstimateTotalBytes(fallbackVideo.EstimatedBytes, audioProbe.EstimatedBytes), maxBytes);
        throw new UnreachableException();
    }

    private async Task<MuxedStreamProbe> SelectMuxedAudioProbeAsync(
        PluginConfig config,
        ProviderMuxedVideoDownloadRequest request,
        IReadOnlyList<ProviderMuxedMediaStream> audios,
        long maxBytes,
        CancellationToken cancellationToken)
    {
        MuxedStreamProbe? firstProbe = null;
        foreach (var audio in config.AutoFallbackQualityBySize ? audios : audios.Take(1))
        {
            var probe = await ProbeMuxedStreamAsync(config, request, audio, "音频流", maxBytes, cancellationToken);
            firstProbe ??= probe;
            if (IsWithinLimit(probe.EstimatedBytes, maxBytes))
            {
                return probe;
            }

            if (!config.AutoFallbackQualityBySize)
            {
                ThrowMuxedTooLarge(request, probe, probe, probe.EstimatedBytes, maxBytes);
            }
        }

        return firstProbe ?? throw new InvalidOperationException($"{request.PlatformDisplayName} 没有可下载的音频流。");
    }

    private async Task<MuxedStreamProbe> ProbeMuxedStreamAsync(
        PluginConfig config,
        ProviderMuxedVideoDownloadRequest request,
        ProviderMuxedMediaStream stream,
        string label,
        long maxBytes,
        CancellationToken cancellationToken)
    {
        Exception? lastError = null;
        foreach (var url in stream.UrlCandidates.Where(i => !string.IsNullOrWhiteSpace(i)).Distinct())
        {
            try
            {
                using var probeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                probeCts.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(config.RequestTimeoutSeconds, 5, 300)));
                var downloadRequest = CreateMuxedRangeRequest(config, request, stream, url, string.Empty, label, maxBytes);
                var probe = await ProbeDownloadAsync(downloadRequest, config.LogDownloadProgress, 2, "MyParser", request.IdentifierName, probeCts.Token);
                return new MuxedStreamProbe(stream, url, probe.ContentLength);
            }
            catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                lastError = ex;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException)
            {
                lastError = ex;
            }
        }

        throw new InvalidOperationException($"{request.PlatformDisplayName} {label}大小探测失败：{lastError?.Message ?? "无可用地址"}");
    }

    private async Task<(string FileUri, string LocalPath)> DownloadMuxedProviderVideoCoreAsync(
        PluginConfig config,
        ProviderMuxedVideoDownloadRequest request,
        ProviderMuxedMediaStream video,
        ProviderMuxedMediaStream audio,
        CancellationToken cancellationToken)
    {
        var dir = ResolveDownloadDirectory(request.DownloadDirectory, request.PlatformId);
        Directory.CreateDirectory(dir);
        var title = SanitizeFileName(string.IsNullOrWhiteSpace(request.Title) ? request.MediaId : request.Title!, 80);
        var unique = $"{SanitizeFileName(request.MediaId, 32)}_{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}";
        var videoPath = Path.Combine(dir, $"{unique}_video.m4s");
        var audioPath = Path.Combine(dir, $"{unique}_audio.m4s");
        var outputPath = GetAvailableOutputPath(dir, $"{title}.mp4");

        BotLog.Info($"MyParser {request.PlatformDisplayName} 音视频流并发下载开始: {request.IdentifierName}={request.MediaId}, video={Path.GetFileName(videoPath)}, audio={Path.GetFileName(audioPath)}, output={Path.GetFileName(outputPath)}");
        try
        {
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var videoTask = DownloadMuxedStreamAsync(config, request, video, videoPath, "视频流", linkedCts.Token);
            var audioTask = DownloadMuxedStreamAsync(config, request, audio, audioPath, "音频流", linkedCts.Token);
            try
            {
                await Task.WhenAll(videoTask, audioTask);
            }
            catch
            {
                await linkedCts.CancelAsync();
                try
                {
                    await Task.WhenAll(videoTask, audioTask);
                }
                catch
                {
                    // Preserve the original download failure below.
                }

                throw;
            }

            BotLog.Info($"MyParser {request.PlatformDisplayName} 音视频流并发下载完成，开始 SharpMP4 合并: {request.IdentifierName}={request.MediaId}");
            await MuxAsync(config, videoPath, audioPath, outputPath, cancellationToken);
            await ValidateMuxedVideoAsync(outputPath, cancellationToken);
        }
        finally
        {
            TryDelete(videoPath);
            TryDelete(audioPath);
        }

        return (new Uri(outputPath).AbsoluteUri, outputPath);
    }

    private async Task DownloadMuxedStreamAsync(
        PluginConfig config,
        ProviderMuxedVideoDownloadRequest request,
        ProviderMuxedMediaStream stream,
        string path,
        string label,
        CancellationToken cancellationToken)
    {
        var urls = stream.UrlCandidates.Where(i => !string.IsNullOrWhiteSpace(i)).Distinct().ToArray();
        if (urls.Length == 0)
        {
            throw new InvalidOperationException($"{request.PlatformDisplayName} {label}没有可用下载地址。");
        }

        var maxBytes = GetMaxBytes(config);
        Exception? lastError = null;
        foreach (var url in urls)
        {
            try
            {
                var downloadRequest = CreateMuxedRangeRequest(config, request, stream, url, path, label, maxBytes);
                var total = await DownloadAsync(downloadRequest, config.LogDownloadProgress, 2, "MyParser", request.IdentifierName, cancellationToken);
                if (total <= 0)
                {
                    throw new InvalidOperationException($"{request.PlatformDisplayName} {label}下载到空文件。");
                }

                return;
            }
            catch (IOException ex)
            {
                CleanupDownloadFiles(path);
                throw new InvalidOperationException($"{request.PlatformDisplayName} {label}下载失败：{ex.Message}", ex);
            }
            catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException)
            {
                if (IsNonRetriableDownloadError(ex))
                {
                    throw;
                }

                lastError = ex;
                CleanupDownloadFiles(path);
            }
        }

        throw new InvalidOperationException($"{request.PlatformDisplayName} {label}下载失败：{lastError?.Message ?? "无可用地址"}");
    }

    private HttpRangeDownloadRequest CreateMuxedRangeRequest(
        PluginConfig config,
        ProviderMuxedVideoDownloadRequest request,
        ProviderMuxedMediaStream stream,
        string url,
        string path,
        string label,
        long maxBytes)
    {
        return new HttpRangeDownloadRequest(
            url,
            path,
            request.MediaId,
            maxBytes,
            true,
            1,
            Math.Clamp(config.ParallelDownloadThreads, 1, 64),
            (method, range) => request.CreateRequest(method, url, range),
            statusCode => new InvalidOperationException($"{request.PlatformDisplayName} {label}下载 HTTP {(int)statusCode}"),
            bytes => new InvalidOperationException($"{request.PlatformDisplayName} {label}文件过大：{bytes / 1024 / 1024}MB > {config.MaxVideoDownloadMegabytes}MB"),
            () => new InvalidOperationException($"{request.PlatformDisplayName} {label}文件超过限制：{config.MaxVideoDownloadMegabytes}MB"),
            (index, statusCode) => new InvalidOperationException($"{request.PlatformDisplayName} {label}分片 {index} 不支持 Range：HTTP {(int)statusCode}"),
            (index, contentRange) => new InvalidOperationException($"{request.PlatformDisplayName} {label}分片 {index} Content-Range 不匹配：{contentRange}"),
            (index, copied, expected) => new InvalidOperationException($"{request.PlatformDisplayName} {label}分片 {index} 大小不匹配：{copied} != {expected}"),
            (total, expected) => new InvalidOperationException($"{request.PlatformDisplayName} {label}分片合并大小不一致：{total} != {expected}"),
            ex => BotLog.Warning($"MyParser {request.PlatformDisplayName} {label}并发下载失败，回退普通下载: {request.IdentifierName}={request.MediaId}, quality={stream.QualityName}, error={ex.Message}"));
    }

    private async Task<(string FileUri, string LocalPath)> DownloadProviderAudioCoreAsync(
        PluginConfig config,
        ProviderAudioDownloadRequest request,
        CancellationToken cancellationToken)
    {
        var dir = ResolveDownloadDirectory(request.DownloadDirectory, request.PlatformId);
        Directory.CreateDirectory(dir);
        var extension = string.IsNullOrWhiteSpace(request.FileExtension) ? "mp3" : request.FileExtension.Trim('.');
        var path = Path.Combine(dir, $"{SanitizeFileName(request.FileNamePrefix, 160)}.{extension}");
        if (File.Exists(path) && new FileInfo(path).Length > 0)
        {
            return (new Uri(path).AbsoluteUri, path);
        }

        var tempPath = path + ".download";
        TryDelete(tempPath);
        var maxBytes = GetMaxBytes(config);
        var downloadRequest = new HttpRangeDownloadRequest(
            request.Url,
            path,
            request.MediaId,
            maxBytes,
            false,
            long.MaxValue,
            1,
            (method, range) => request.CreateRequest(method, request.Url, range),
            statusCode => new InvalidOperationException($"{request.PlatformDisplayName} 音频下载 HTTP {(int)statusCode}"),
            bytes => new InvalidOperationException($"{request.PlatformDisplayName} 音频文件过大：{bytes / 1024 / 1024}MB > {config.MaxVideoDownloadMegabytes}MB"),
            () => new InvalidOperationException($"{request.PlatformDisplayName} 音频文件超过限制：{config.MaxVideoDownloadMegabytes}MB"),
            (index, statusCode) => new InvalidOperationException($"{request.PlatformDisplayName} 音频分片 {index} 不支持 Range：HTTP {(int)statusCode}"),
            (index, contentRange) => new InvalidOperationException($"{request.PlatformDisplayName} 音频分片 {index} Content-Range 不匹配：{contentRange}"),
            (index, copied, expected) => new InvalidOperationException($"{request.PlatformDisplayName} 音频分片 {index} 大小不匹配：{copied} != {expected}"),
            (total, expected) => new InvalidOperationException($"{request.PlatformDisplayName} 音频分片合并大小不一致：{total} != {expected}"));

        var total = await DownloadAsync(downloadRequest, config.LogDownloadProgress, 2, $"MyParser {request.PlatformDisplayName}", request.IdentifierName, cancellationToken).ConfigureAwait(false);
        if (total <= 0)
        {
            throw new InvalidDataException($"{request.PlatformDisplayName} 音频下载到空文件。");
        }

        return (new Uri(path).AbsoluteUri, path);
    }

    private async Task EncodeSilkAsync(ProviderRecordBuildRequest request, string outputSilkPath, int silkRate, CancellationToken cancellationToken)
    {
        var workDirectory = Path.Combine(Path.GetTempPath(), "Shirobot.Plugin.MyParser", request.PlatformId, "silk-work");
        Directory.CreateDirectory(workDirectory);
        var tempBaseName = request.PlatformId + "_" + Guid.NewGuid().ToString("N");
        var tempPcmPath = Path.Combine(workDirectory, tempBaseName + ".pcm");
        var tempSilkPath = Path.Combine(workDirectory, tempBaseName + ".silk");

        try
        {
            await Task.Run(() => ManagedMp3PcmConverter.ConvertToMonoS16Le(request.LocalAudioPath, tempPcmPath, cancellationToken), cancellationToken).ConfigureAwait(false);
            var encoder = new SilkEncoder(new SilkEncoderOptions
            {
                SampleRate = 48000,
                BitRate = silkRate,
                MaxInternalSampleRate = 24000,
                PacketLengthMilliseconds = 20,
                Tencent = true,
                Complexity = 2,
                PacketLossPercentage = 0,
                UseDtx = false,
                UseInBandFec = false,
            });
            var pcm = await File.ReadAllBytesAsync(tempPcmPath, cancellationToken).ConfigureAwait(false);
            var silk = await Task.Run(() => encoder.EncodePcm16LittleEndian(pcm), cancellationToken).ConfigureAwait(false);
            await File.WriteAllBytesAsync(tempSilkPath, silk, cancellationToken).ConfigureAwait(false);

            if (!File.Exists(tempSilkPath) || new FileInfo(tempSilkPath).Length == 0)
            {
                throw new InvalidDataException("SILK 编码输出为空。");
            }

            if (File.Exists(outputSilkPath)) File.Delete(outputSilkPath);
            File.Move(tempSilkPath, outputSilkPath);
            BotLog.Info($"MyParser {request.PlatformDisplayName} SILK 编码完成: input={request.LocalAudioPath}, output={outputSilkPath}, silk_rate={silkRate}, size_kb={new FileInfo(outputSilkPath).Length / 1024d:F1}, encoder=SilkCodec.NET");
        }
        finally
        {
            TryDelete(tempPcmPath);
            TryDelete(tempSilkPath);
        }
    }

    private async Task<(string FileUri, string LocalPath)> DownloadProviderVideoCandidateAsync(
        PluginConfig config,
        ProviderVideoDownloadRequest request,
        string url,
        CancellationToken cancellationToken)
    {
        var maxBytes = config.MaxVideoDownloadMegabytes <= 0
            ? long.MaxValue
            : config.MaxVideoDownloadMegabytes * 1024L * 1024L;
        var dir = ResolveDownloadDirectory(request.DownloadDirectory, request.PlatformId);
        Directory.CreateDirectory(dir);
        var extension = string.IsNullOrWhiteSpace(request.FileExtension) ? "mp4" : request.FileExtension.Trim('.');
        var path = Path.Combine(dir, $"{request.FileNamePrefix}_{SanitizeFileName(request.MediaId)}_{DateTimeOffset.UtcNow:yyyyMMddHHmmss}.{extension}");
        var segmentCount = Math.Clamp(config.ParallelDownloadThreads, 1, 64);
        var downloadRequest = new HttpRangeDownloadRequest(
            url,
            path,
            request.MediaId,
            maxBytes,
            true,
            1,
            segmentCount,
            (method, range) => request.CreateRequest(method, url, range),
            statusCode => new InvalidOperationException($"HTTP {(int)statusCode}"),
            bytes => new InvalidOperationException($"视频文件过大：{bytes / 1024 / 1024}MB > {config.MaxVideoDownloadMegabytes}MB"),
            () => new InvalidOperationException($"视频文件超过限制：{config.MaxVideoDownloadMegabytes}MB"),
            (index, statusCode) => new InvalidOperationException($"分片 {index} 不支持 Range：HTTP {(int)statusCode}"),
            (index, contentRange) => new InvalidOperationException($"分片 {index} Content-Range 不匹配：{contentRange}"),
            (index, copied, expected) => new InvalidOperationException($"分片 {index} 大小不匹配：{copied} != {expected}"),
            (total, expected) => new InvalidOperationException($"分片合并大小不一致：{total} != {expected}"),
            ex => BotLog.Warning($"MyParser {request.PlatformDisplayName} 下载进度: {request.IdentifierName}={request.MediaId}, 并发下载失败，回退普通下载：{ex.Message}"));

        var total = await DownloadAsync(downloadRequest, config.LogDownloadProgress, 2, "MyParser", request.IdentifierName, cancellationToken);
        if (total == 0)
        {
            throw new InvalidDataException("下载到空文件");
        }

        await ValidateDownloadedVideoAsync(path, total, request.ValidationKind, cancellationToken);
        return (new Uri(path).AbsoluteUri, path);
    }

    private static string ResolveDownloadDirectory(string configuredDirectory, string platformId)
    {
        if (string.IsNullOrWhiteSpace(configuredDirectory))
        {
            return Path.Combine(Path.GetTempPath(), "Shirobot.Plugin.MyParser", platformId);
        }

        return Path.IsPathRooted(configuredDirectory)
            ? configuredDirectory
            : Path.Combine(AppContext.BaseDirectory, configuredDirectory);
    }

    private static string SanitizeFileName(string value)
    {
        return SanitizeFileName(value, null);
    }

    private static string SanitizeFileName(string value, int? maxLength)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
        {
            value = value.Replace(c, '_');
        }

        value = value.ReplaceLineEndings(" ").Trim(' ', '.');
        if (string.IsNullOrWhiteSpace(value))
        {
            value = "media";
        }

        return maxLength is > 0 && value.Length > maxLength.Value ? value[..maxLength.Value] : value;
    }

    private static long GetMaxBytes(PluginConfig config)
    {
        return config.MaxVideoDownloadMegabytes <= 0
            ? long.MaxValue
            : config.MaxVideoDownloadMegabytes * 1024L * 1024L;
    }

    private static long? EstimateTotalBytes(long? videoBytes, long? audioBytes)
    {
        return videoBytes is null || audioBytes is null ? null : videoBytes.Value + audioBytes.Value;
    }

    private static bool IsWithinLimit(long? bytes, long maxBytes)
    {
        return maxBytes == long.MaxValue || bytes is null || bytes <= maxBytes;
    }

    private static void ThrowMuxedTooLarge(ProviderMuxedVideoDownloadRequest request, MuxedStreamProbe video, MuxedStreamProbe audio, long? estimated, long maxBytes)
    {
        var limitText = maxBytes == long.MaxValue ? "无限制" : FormatSize(maxBytes);
        throw new InvalidOperationException($"{request.PlatformDisplayName} 视频文件过大：视频流={FormatSize(video.EstimatedBytes)}, 音频流={FormatSize(audio.EstimatedBytes)}, 预估合计={FormatSize(estimated)}, 限制={limitText}");
    }

    private static string FormatSize(long? bytes)
    {
        return bytes is null ? "未知" : $"{bytes.Value / 1024d / 1024d:F2}MB";
    }

    private static bool IsNonRetriableDownloadError(Exception ex)
    {
        for (var current = ex; current is not null; current = current.InnerException)
        {
            if (current is OutOfMemoryException)
            {
                return true;
            }

            if (current is IOException && current.Message.Contains("being used by another process", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (current is InvalidOperationException
                && (current.Message.Contains("文件过大", StringComparison.OrdinalIgnoreCase)
                    || current.Message.Contains("文件超过限制", StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        return false;
    }

    private static void CleanupDownloadFiles(string path)
    {
        TryDelete(path);
        TryDelete(path + ".download");
    }

    private static async Task MuxAsync(PluginConfig config, string videoPath, string audioPath, string outputPath, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Run(() => SharpMp4MediaService.Mux(videoPath, audioPath, outputPath), cancellationToken).ConfigureAwait(false);
            BotLog.Info($"MyParser SharpMP4 音视频合并完成: output={outputPath}");
            return;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            TryDelete(outputPath);
            BotLog.Warning($"MyParser SharpMP4 音视频合并失败，回退 ffmpeg: error={ex.Message}");
        }

        var ffmpeg = ResolveFfmpegPath(config)
                     ?? throw new InvalidOperationException("SharpMP4 无法合并当前音视频流，且未找到 ffmpeg 回退程序。请配置 FfmpegPath 或将 ffmpeg 加入 PATH。");
        await MuxWithFfmpegAsync(ffmpeg, videoPath, audioPath, outputPath, cancellationToken).ConfigureAwait(false);
    }

    private static async Task MuxWithFfmpegAsync(string ffmpeg, string videoPath, string audioPath, string outputPath, CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo
        {
            FileName = ffmpeg,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-y");
        psi.ArgumentList.Add("-i");
        psi.ArgumentList.Add(videoPath);
        psi.ArgumentList.Add("-i");
        psi.ArgumentList.Add(audioPath);
        psi.ArgumentList.Add("-map");
        psi.ArgumentList.Add("0:v:0");
        psi.ArgumentList.Add("-map");
        psi.ArgumentList.Add("1:a:0");
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("copy");
        psi.ArgumentList.Add("-movflags");
        psi.ArgumentList.Add("+faststart");
        psi.ArgumentList.Add(outputPath);

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("ffmpeg 启动失败。");
        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        if (process.ExitCode != 0)
        {
            var detail = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
            if (detail.Length > 2000)
            {
                detail = detail[^2000..];
            }

            throw new InvalidOperationException("ffmpeg 合并失败：" + detail);
        }
    }

    private static async Task ValidateMuxedVideoAsync(string path, CancellationToken cancellationToken)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length < 1024)
        {
            throw new InvalidDataException("音视频合并输出文件为空或过小。");
        }

        await using var file = File.OpenRead(path);
        var header = new byte[Math.Min(4096, (int)Math.Min(file.Length, 4096))];
        var read = await file.ReadAsync(header, cancellationToken);
        var ascii = Encoding.ASCII.GetString(header, 0, read);
        if (!ascii.Contains("ftyp", StringComparison.Ordinal))
        {
            throw new InvalidDataException("音视频合并输出文件不像 MP4，可能合并失败。");
        }
    }

    private static string? ResolveFfmpegPath(PluginConfig config)
    {
        if (!string.IsNullOrWhiteSpace(config.FfmpegPath) && File.Exists(config.FfmpegPath))
        {
            return config.FfmpegPath;
        }

        var executableName = OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, executableName),
            Path.Combine(AppContext.BaseDirectory, "vendor", "ffmpeg", "bin", executableName),
        };
        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return OperatingSystem.IsWindows()
            ? FindOnPath("ffmpeg.exe") ?? FindOnPath("ffmpeg")
            : FindOnPath("ffmpeg") ?? FindOnPath("ffmpeg.exe");
    }

    private static string? FindOnPath(string fileName)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        foreach (var dir in path.Split(Path.PathSeparator))
        {
            try
            {
                var candidate = Path.Combine(dir.Trim(), fileName);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch
            {
                // ignore invalid PATH entries
            }
        }

        return null;
    }

    private static string GetAvailableOutputPath(string directory, string fileName)
    {
        var path = Path.Combine(directory, fileName);
        if (!File.Exists(path))
        {
            return path;
        }

        var name = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        return Path.Combine(directory, $"{name}_{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}{extension}");
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // ignore cleanup errors
        }
    }

    private static async Task ValidateDownloadedVideoAsync(
        string path,
        long totalBytes,
        ProviderVideoValidationKind validationKind,
        CancellationToken cancellationToken)
    {
        if (validationKind == ProviderVideoValidationKind.None)
        {
            return;
        }

        if (totalBytes < 1024)
        {
            throw new InvalidDataException($"下载文件过小：{totalBytes} bytes");
        }

        await using var file = File.OpenRead(path);
        var header = new byte[Math.Min(4096, (int)Math.Min(file.Length, 4096))];
        var read = await file.ReadAsync(header, cancellationToken);
        var ascii = Encoding.ASCII.GetString(header, 0, read);
        var isMp4 = ascii.Contains("ftyp", StringComparison.Ordinal);
        var isWebM = ascii.Contains("webm", StringComparison.OrdinalIgnoreCase);
        var valid = validationKind switch
        {
            ProviderVideoValidationKind.Mp4 => isMp4,
            ProviderVideoValidationKind.Mp4OrWebM => isMp4 || isWebM,
            _ => true,
        };

        if (valid)
        {
            return;
        }

        var sample = ascii.ReplaceLineEndings(" ");
        if (sample.Length > 120)
        {
            sample = sample[..120];
        }

        throw new InvalidDataException($"下载文件不像视频，可能是风控页或错误内容：{sample}");
    }


    private sealed record MuxedStreamProbe(ProviderMuxedMediaStream Stream, string Url, long? EstimatedBytes);
}
