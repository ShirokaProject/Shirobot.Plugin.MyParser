using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text;
using Shirobot.Plugin.MyParser.Parsing;
using Shirobot.Plugin.MyParser.Utility;
using ShiroBot.SDK.Abstractions;

namespace Shirobot.Plugin.MyParser.Services;

internal sealed class LiveReplayClipDownloader(PluginConfig config, ProviderDownloadService hostServices)
{
    private const int MaxPlaylistBytes = 2 * 1024 * 1024;

    private static readonly HttpClient PlaylistHttp = new(new HttpClientHandler
    {
        AutomaticDecompression = DecompressionMethods.All,
        AllowAutoRedirect = true,
    });

    public async Task<ProviderLiveReplayClipDownloadResult> DownloadAsync(ProviderLiveReplayClipDownloadRequest request, CancellationToken cancellationToken = default)
    {
        var stream = request.Streams.OrderBy(request.StreamRank).FirstOrDefault()
                     ?? throw new InvalidOperationException($"{request.PlatformDisplayName} 未返回可用于截取的播放流。");
        var dir = ResolveClipDirectory(request);
        Directory.CreateDirectory(dir);
        try
        {
            var outputPath = Path.Combine(dir, $"live_{SanitizeFileName(request.MediaId, 40)}_{DateTimeOffset.UtcNow:yyyyMMddHHmmss}.mp4");
            var durationSeconds = Math.Clamp(config.BilibiliLiveReplayClipSeconds, 3, 3000);
            var playlist = await BuildStaticReplayPlaylistAsync(request, stream.Url, dir, durationSeconds, cancellationToken);
            var timeoutSeconds = Math.Max(30, durationSeconds * 2 + 60);

            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            linkedCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
            if (!await TryBuildFmp4ClipAsync(request, stream, playlist.Path, outputPath, linkedCts.Token))
            {
                var ffmpeg = ResolveFfmpegPath()
                             ?? throw new InvalidOperationException("当前直播流无法由 SharpMP4 直接处理，且未找到 ffmpeg 回退程序。请配置 FfmpegPath 或将 ffmpeg 加入 PATH。");
                await CutStaticLiveClipAsync(ffmpeg, playlist.Path, outputPath, timeoutSeconds, linkedCts.Token);
            }
            await ValidateClipAsync(outputPath, cancellationToken);

            BotLog.Info($"MyParser {request.PlatformDisplayName} 直播回看片段生成完成: {request.IdentifierName}={request.MediaId}, requested_duration={durationSeconds}s, actual_seconds={playlist.ActualSeconds:F1}, segments={playlist.SelectedSegments}/{playlist.TotalSegments}, stream={stream.Protocol}/{stream.Format}/{stream.Codec}, qn={stream.CurrentQn}, file_mb={new FileInfo(outputPath).Length / 1024d / 1024d:F2}, file={outputPath}");
            return new ProviderLiveReplayClipDownloadResult(new Uri(outputPath).AbsoluteUri, outputPath, stream, playlist.SelectedSegments, playlist.TotalSegments, playlist.ActualSeconds, playlist.Path);
        }
        catch
        {
            TryDeleteDirectory(dir);
            throw;
        }
    }

    private async Task<LiveStaticPlaylistResult> BuildStaticReplayPlaylistAsync(ProviderLiveReplayClipDownloadRequest request, string playlistUrl, string dir, int durationSeconds, CancellationToken cancellationToken)
    {
        var segmentDir = Path.Combine(dir, $"segments_{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}");
        Directory.CreateDirectory(segmentDir);
        var maxBytes = config.BilibiliLiveReplayClipMaxMegabytes <= 0
            ? long.MaxValue
            : config.BilibiliLiveReplayClipMaxMegabytes * 1024L * 1024L;
        var (parsed, downloadedSegments, totalSeconds, resolvedPlaylistUrl) = await WaitForEnoughReplaySegmentsAsync(request, playlistUrl, durationSeconds, segmentDir, maxBytes, cancellationToken);
        playlistUrl = resolvedPlaylistUrl;
        var selected = downloadedSegments.Select(i => i.Segment).ToList();

        var firstIndex = parsed.Segments.IndexOf(selected[0]);
        var staticPlaylist = new StringBuilder();
        staticPlaylist.AppendLine("#EXTM3U");
        staticPlaylist.AppendLine($"#EXT-X-VERSION:{Math.Max(3, parsed.Version)}");
        staticPlaylist.AppendLine($"#EXT-X-TARGETDURATION:{Math.Max(1, (int)Math.Ceiling(selected.Max(i => i.DurationSeconds)))}");
        staticPlaylist.AppendLine($"#EXT-X-MEDIA-SEQUENCE:{Math.Max(0, parsed.MediaSequence + firstIndex)}");
        if (parsed.IndependentSegments)
        {
            staticPlaylist.AppendLine("#EXT-X-INDEPENDENT-SEGMENTS");
        }

        if (!string.IsNullOrWhiteSpace(parsed.MapLine))
        {
            staticPlaylist.AppendLine(parsed.MapLine);
        }

        if (!string.IsNullOrWhiteSpace(parsed.KeyLine))
        {
            staticPlaylist.AppendLine(parsed.KeyLine);
        }

        foreach (var (segment, localPath) in downloadedSegments)
        {
            foreach (var tag in segment.Tags)
            {
                staticPlaylist.AppendLine(tag);
            }

            staticPlaylist.AppendLine(FormattableString.Invariant($"#EXTINF:{segment.DurationSeconds:0.###},"));
            staticPlaylist.AppendLine(ToM3U8LocalPath(localPath));
        }

        staticPlaylist.AppendLine("#EXT-X-ENDLIST");
        var path = Path.Combine(dir, $"live_static_{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}.m3u8");
        await File.WriteAllTextAsync(path, staticPlaylist.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), cancellationToken);
        BotLog.Info($"MyParser {request.PlatformDisplayName} 直播回溯 m3u8 已冻结: source={PreviewUrl(playlistUrl)}, segments={selected.Count}/{parsed.Segments.Count}, seconds={totalSeconds:F1}/{durationSeconds}, path={path}");
        return new LiveStaticPlaylistResult(path, selected.Count, parsed.Segments.Count, totalSeconds);
    }

    private async Task<(HlsPlaylist Parsed, List<(HlsSegment Segment, string LocalPath)> Selected, double TotalSeconds, string PlaylistUrl)> WaitForEnoughReplaySegmentsAsync(ProviderLiveReplayClipDownloadRequest request, string playlistUrl, int durationSeconds, string segmentDir, long maxBytes, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(Math.Min(durationSeconds + 15, 90));
        var resolvedPlaylistUrl = playlistUrl;
        var collectedSegments = new List<HlsSegment>();
        var collectedUris = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var downloadedSegments = new Dictionary<string, (HlsSegment Segment, string LocalPath, long Bytes)>(StringComparer.OrdinalIgnoreCase);
        long downloadedBytes = 0;

        while (true)
        {
            var playlistText = await FetchPlaylistTextAsync(request, resolvedPlaylistUrl, cancellationToken);
            if (playlistText.Contains("#EXT-X-STREAM-INF", StringComparison.OrdinalIgnoreCase))
            {
                resolvedPlaylistUrl = ResolveFirstMediaPlaylistUrl(playlistText, resolvedPlaylistUrl)
                                      ?? throw new InvalidOperationException("直播 m3u8 是 master playlist，但未找到 media playlist。");
                playlistText = await FetchPlaylistTextAsync(request, resolvedPlaylistUrl, cancellationToken);
            }

            var parsed = ParseMediaPlaylist(playlistText, resolvedPlaylistUrl);
            if (parsed.Segments.Count == 0)
            {
                throw new InvalidOperationException("当前直播 m3u8 未包含可回溯分片。");
            }

            var newSegments = 0;
            foreach (var segment in parsed.Segments)
            {
                if (collectedUris.Add(segment.Uri))
                {
                    var extension = GuessSegmentExtension(segment.Uri);
                    var localPath = Path.Combine(segmentDir, $"seg_{downloadedSegments.Count:D4}{extension}");
                    var bytes = await DownloadSegmentAsync(request, segment.Uri, localPath, maxBytes - downloadedBytes, cancellationToken);
                    downloadedBytes += bytes;
                    collectedSegments.Add(segment);
                    downloadedSegments.Add(segment.Uri, (segment, localPath, bytes));
                    newSegments++;
                }
            }

            var collectedPlaylist = parsed with
            {
                MediaSequence = 0,
                Segments = [..collectedSegments],
            };
            var selected = SelectRecentSegments(collectedPlaylist, durationSeconds, out var totalSeconds);
            if (totalSeconds >= durationSeconds || DateTimeOffset.UtcNow >= deadline)
            {
                if (totalSeconds < durationSeconds)
                {
                    BotLog.Warning($"MyParser {request.PlatformDisplayName} 直播回溯分片不足配置时长: requested={durationSeconds}s, available={totalSeconds:F1}s, segments={selected.Count}/{collectedSegments.Count}, latest_window={parsed.Segments.Count}");
                }

                return (collectedPlaylist, selected.Select(i =>
                {
                    var downloaded = downloadedSegments[i.Uri];
                    return (downloaded.Segment, downloaded.LocalPath);
                }).ToList(), totalSeconds, resolvedPlaylistUrl);
            }

            var waitSeconds = Math.Clamp(durationSeconds - totalSeconds, 2, 5);
            BotLog.Info($"MyParser {request.PlatformDisplayName} 直播回溯分片不足，等待更多分片: requested={durationSeconds}s, available={totalSeconds:F1}s, collected={collectedSegments.Count}, latest_window={parsed.Segments.Count}, new={newSegments}, wait={waitSeconds:F1}s");
            await Task.Delay(TimeSpan.FromSeconds(waitSeconds), cancellationToken);
        }
    }

    private static List<HlsSegment> SelectRecentSegments(HlsPlaylist parsed, int durationSeconds, out double totalSeconds)
    {
        var selected = new List<HlsSegment>();
        totalSeconds = 0;
        for (var i = parsed.Segments.Count - 1; i >= 0; i--)
        {
            selected.Insert(0, parsed.Segments[i]);
            totalSeconds += parsed.Segments[i].DurationSeconds;
            if (totalSeconds >= durationSeconds)
            {
                break;
            }
        }

        return selected;
    }

    private async Task<string> FetchPlaylistTextAsync(ProviderLiveReplayClipDownloadRequest request, string playlistUrl, CancellationToken cancellationToken)
    {
        using var httpRequest = request.CreatePlaylistRequest(HttpMethod.Get, playlistUrl, null);
        using var response = await PlaylistHttp.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is > MaxPlaylistBytes)
        {
            throw new InvalidOperationException($"直播 m3u8 过大：{response.Content.Headers.ContentLength.Value / 1024}KB。");
        }

        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var output = new MemoryStream();
        var buffer = new byte[16 * 1024];
        while (true)
        {
            var read = await input.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                break;
            }

            output.Write(buffer, 0, read);
            if (output.Length > MaxPlaylistBytes)
            {
                throw new InvalidOperationException($"直播 m3u8 读取超过限制：{MaxPlaylistBytes / 1024}KB。");
            }
        }

        return Encoding.UTF8.GetString(output.ToArray());
    }

    private async Task CutStaticLiveClipAsync(string ffmpeg, string playlistPath, string outputPath, int timeoutSeconds, CancellationToken cancellationToken)
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
        psi.ArgumentList.Add("-nostdin");
        psi.ArgumentList.Add("-hide_banner");
        psi.ArgumentList.Add("-loglevel");
        psi.ArgumentList.Add("warning");
        psi.ArgumentList.Add("-allowed_extensions");
        psi.ArgumentList.Add("ALL");
        psi.ArgumentList.Add("-i");
        psi.ArgumentList.Add(playlistPath);
        psi.ArgumentList.Add("-map");
        psi.ArgumentList.Add("0:v:0?");
        psi.ArgumentList.Add("-map");
        psi.ArgumentList.Add("0:a:0?");
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("copy");
        psi.ArgumentList.Add("-avoid_negative_ts");
        psi.ArgumentList.Add("make_zero");
        psi.ArgumentList.Add("-movflags");
        psi.ArgumentList.Add("+faststart");
        psi.ArgumentList.Add(outputPath);

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("ffmpeg 启动失败。");
        try
        {
            var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            if (process.ExitCode != 0)
            {
                var detail = TrimFfmpegDetail(string.IsNullOrWhiteSpace(stderr) ? stdout : stderr);
                throw new InvalidOperationException("ffmpeg 截取直播片段失败：" + detail);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            throw new InvalidOperationException($"ffmpeg 截取直播片段超时（>{timeoutSeconds}s）。");
        }
    }

    private async Task<bool> TryBuildFmp4ClipAsync(
        ProviderLiveReplayClipDownloadRequest request,
        ProviderLiveReplayStream stream,
        string playlistPath,
        string outputPath,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(stream.Format, "fmp4", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var lines = await File.ReadAllLinesAsync(playlistPath, cancellationToken);
        if (lines.Any(line => line.StartsWith("#EXT-X-KEY:", StringComparison.OrdinalIgnoreCase)
                              && !line.Contains("METHOD=NONE", StringComparison.OrdinalIgnoreCase))
            || lines.Any(line => line.StartsWith("#EXT-X-BYTERANGE", StringComparison.OrdinalIgnoreCase)
                                 || line.StartsWith("#EXT-X-DISCONTINUITY", StringComparison.OrdinalIgnoreCase)
                                 || line.StartsWith("#EXT-X-GAP", StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        var mapLine = lines.FirstOrDefault(line => line.StartsWith("#EXT-X-MAP:", StringComparison.OrdinalIgnoreCase));
        var mapUri = mapLine is null ? null : ExtractUriAttribute(mapLine);
        var segmentPaths = lines
            .Select(line => line.Trim())
            .Where(line => line.Length > 0 && !line.StartsWith('#'))
            .ToArray();
        if (string.IsNullOrWhiteSpace(mapUri) || segmentPaths.Length == 0 || segmentPaths.Any(path => !File.Exists(path)))
        {
            return false;
        }

        var initPath = outputPath + ".init.m4s";
        try
        {
            var maxBytes = config.BilibiliLiveReplayClipMaxMegabytes <= 0
                ? long.MaxValue
                : config.BilibiliLiveReplayClipMaxMegabytes * 1024L * 1024L;
            await DownloadSegmentAsync(request, mapUri, initPath, maxBytes, cancellationToken);
            await using (var output = new BufferedStream(new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.Read)))
            {
                foreach (var path in new[] { initPath }.Concat(segmentPaths))
                {
                    await using var input = new BufferedStream(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read));
                    await input.CopyToAsync(output, cancellationToken);
                }
            }

            await Task.Run(() => SharpMp4MediaService.Validate(outputPath), cancellationToken);
            BotLog.Info($"MyParser {request.PlatformDisplayName} SharpMP4 fMP4 直播片段拼接完成: segments={segmentPaths.Length}, output={outputPath}");
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            TryDelete(outputPath);
            BotLog.Warning($"MyParser {request.PlatformDisplayName} SharpMP4 fMP4 直播片段处理失败，回退 ffmpeg: error={ex.Message}");
            return false;
        }
        finally
        {
            TryDelete(initPath);
        }
    }

    private async Task<long> DownloadSegmentAsync(ProviderLiveReplayClipDownloadRequest request, string url, string localPath, long remainingBytes, CancellationToken cancellationToken)
    {
        if (remainingBytes <= 0)
        {
            throw new InvalidOperationException($"直播片段超过大小限制：{config.BilibiliLiveReplayClipMaxMegabytes}MB。");
        }

        var downloadRequest = new HttpRangeDownloadRequest(
            url,
            localPath,
            Path.GetFileName(localPath),
            remainingBytes,
            true,
            1,
            4,
            (method, range) => request.CreateSegmentRequest(method, url, range),
            statusCode => new InvalidOperationException($"直播分片下载 HTTP {(int)statusCode}"),
            _ => new InvalidOperationException($"直播片段超过大小限制：{config.BilibiliLiveReplayClipMaxMegabytes}MB。"),
            () => new InvalidOperationException($"直播片段超过大小限制：{config.BilibiliLiveReplayClipMaxMegabytes}MB。"),
            (index, statusCode) => new InvalidOperationException($"直播分片 {index} 不支持 Range：HTTP {(int)statusCode}"),
            (index, contentRange) => new InvalidOperationException($"直播分片 {index} Content-Range 不匹配：{contentRange}"),
            (index, copied, expected) => new InvalidOperationException($"直播分片 {index} 大小不匹配：{copied} != {expected}"),
            (actual, expected) => new InvalidOperationException($"直播分片合并大小不匹配：{actual} != {expected}"));

        var total = await hostServices.DownloadAsync(downloadRequest, config.LogDownloadProgress, 2, $"MyParser {request.PlatformDisplayName} 直播", "segment", cancellationToken);
        if (total <= 0)
        {
            throw new InvalidOperationException("直播分片下载到空文件。");
        }

        return total;
    }

    private static string GuessSegmentExtension(string uri)
    {
        var path = Uri.TryCreate(uri, UriKind.Absolute, out var absolute) ? absolute.AbsolutePath : uri;
        var extension = Path.GetExtension(path);
        return string.IsNullOrWhiteSpace(extension) ? ".ts" : extension;
    }

    private static string ToM3U8LocalPath(string localPath)
    {
        return Path.GetFullPath(localPath).Replace('\\', '/');
    }

    private static string? ResolveFirstMediaPlaylistUrl(string masterPlaylistText, string masterUrl)
    {
        var nextUriIsVariant = false;
        foreach (var raw in masterPlaylistText.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = raw.Trim();
            if (line.StartsWith("#EXT-X-STREAM-INF", StringComparison.OrdinalIgnoreCase))
            {
                nextUriIsVariant = true;
                continue;
            }

            if (nextUriIsVariant && !line.StartsWith('#'))
            {
                return ResolveUrl(masterUrl, line);
            }
        }

        return null;
    }

    private static HlsPlaylist ParseMediaPlaylist(string playlistText, string playlistUrl)
    {
        var version = 3;
        var mediaSequence = 0;
        var independentSegments = false;
        string? mapLine = null;
        string? keyLine = null;
        var segments = new List<HlsSegment>();
        var pendingTags = new List<string>();
        double? pendingDuration = null;

        foreach (var raw in playlistText.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.Equals("#EXTM3U", StringComparison.OrdinalIgnoreCase) || line.Equals("#EXT-X-ENDLIST", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (line.StartsWith("#EXT-X-VERSION:", StringComparison.OrdinalIgnoreCase))
            {
                if (int.TryParse(line[15..], out var parsedVersion))
                {
                    version = parsedVersion;
                }
                continue;
            }

            if (line.StartsWith("#EXT-X-MEDIA-SEQUENCE:", StringComparison.OrdinalIgnoreCase))
            {
                if (int.TryParse(line[22..], out var parsedSequence))
                {
                    mediaSequence = parsedSequence;
                }
                continue;
            }

            if (line.StartsWith("#EXT-X-INDEPENDENT-SEGMENTS", StringComparison.OrdinalIgnoreCase))
            {
                independentSegments = true;
                continue;
            }

            if (line.StartsWith("#EXT-X-MAP:", StringComparison.OrdinalIgnoreCase))
            {
                mapLine = RewriteUriAttributeLine(line, playlistUrl);
                continue;
            }

            if (line.StartsWith("#EXT-X-KEY:", StringComparison.OrdinalIgnoreCase))
            {
                keyLine = RewriteUriAttributeLine(line, playlistUrl);
                continue;
            }

            if (line.StartsWith("#EXTINF:", StringComparison.OrdinalIgnoreCase))
            {
                var comma = line.IndexOf(',');
                var number = comma > 8 ? line[8..comma] : line[8..];
                pendingDuration = double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out var duration) ? duration : 0;
                continue;
            }

            if (line.StartsWith('#'))
            {
                if (pendingDuration is not null && IsSegmentScopedTag(line))
                {
                    pendingTags.Add(RewriteUriAttributeLine(line, playlistUrl));
                }
                continue;
            }

            if (pendingDuration is null)
            {
                continue;
            }

            segments.Add(new HlsSegment(pendingDuration.Value, ResolveUrl(playlistUrl, line), [..pendingTags]));
            pendingDuration = null;
            pendingTags.Clear();
        }

        return new HlsPlaylist(version, mediaSequence, independentSegments, mapLine, keyLine, segments);
    }

    private static bool IsSegmentScopedTag(string line)
    {
        return line.StartsWith("#EXT-X-BYTERANGE", StringComparison.OrdinalIgnoreCase)
               || line.StartsWith("#EXT-X-DISCONTINUITY", StringComparison.OrdinalIgnoreCase)
               || line.StartsWith("#EXT-X-PROGRAM-DATE-TIME", StringComparison.OrdinalIgnoreCase)
               || line.StartsWith("#EXT-X-DATERANGE", StringComparison.OrdinalIgnoreCase)
               || line.StartsWith("#EXT-X-GAP", StringComparison.OrdinalIgnoreCase)
               || line.StartsWith("#EXT-X-PART", StringComparison.OrdinalIgnoreCase)
               || line.StartsWith("#EXT-X-PRELOAD-HINT", StringComparison.OrdinalIgnoreCase);
    }

    private static string RewriteUriAttributeLine(string line, string playlistUrl)
    {
        const string marker = "URI=\"";
        var start = line.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (start < 0)
        {
            return line;
        }

        start += marker.Length;
        var end = line.IndexOf('"', start);
        if (end <= start)
        {
            return line;
        }

        var uri = line[start..end];
        var absolute = ResolveUrl(playlistUrl, uri);
        return line[..start] + absolute + line[end..];
    }

    private static string? ExtractUriAttribute(string line)
    {
        const string marker = "URI=\"";
        var start = line.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (start < 0)
        {
            return null;
        }

        start += marker.Length;
        var end = line.IndexOf('"', start);
        return end > start ? line[start..end] : null;
    }

    private static string ResolveUrl(string baseUrl, string value)
    {
        return Uri.TryCreate(value, UriKind.Absolute, out var absolute)
            ? absolute.ToString()
            : new Uri(new Uri(baseUrl), value).ToString();
    }

    private async Task ValidateClipAsync(string path, CancellationToken cancellationToken)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length < 1024)
        {
            throw new InvalidDataException("直播片段输出为空或过小。");
        }

        var maxBytes = config.BilibiliLiveReplayClipMaxMegabytes <= 0
            ? long.MaxValue
            : config.BilibiliLiveReplayClipMaxMegabytes * 1024L * 1024L;
        if (info.Length > maxBytes)
        {
            TryDelete(path);
            throw new InvalidOperationException($"直播片段过大：{info.Length / 1024 / 1024}MB > {config.BilibiliLiveReplayClipMaxMegabytes}MB。");
        }

        await using var file = File.OpenRead(path);
        var header = new byte[Math.Min(4096, (int)Math.Min(file.Length, 4096))];
        var read = await file.ReadAsync(header, cancellationToken);
        var ascii = Encoding.ASCII.GetString(header, 0, read);
        if (!ascii.Contains("ftyp", StringComparison.Ordinal))
        {
            throw new InvalidDataException("直播片段输出不像 MP4，可能处理失败。");
        }
    }

    private string ResolveClipDirectory(ProviderLiveReplayClipDownloadRequest request)
    {
        var root = string.IsNullOrWhiteSpace(request.DownloadDirectory)
            ? Path.Combine(Path.GetTempPath(), "Shirobot.Plugin.MyParser", request.PlatformId)
            : Path.IsPathRooted(request.DownloadDirectory)
                ? request.DownloadDirectory
                : Path.Combine(AppContext.BaseDirectory, request.DownloadDirectory);
        return Path.Combine(root, "live-clips", SanitizeFileName(request.MediaId, 40));
    }

    private string? ResolveFfmpegPath()
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

    private static string SanitizeFileName(string value, int maxLength)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
        {
            value = value.Replace(c, '_');
        }

        value = value.ReplaceLineEndings(" ").Trim(' ', '.');
        if (string.IsNullOrWhiteSpace(value))
        {
            value = "live";
        }

        return value.Length <= maxLength ? value : value[..maxLength];
    }

    private static string TrimFfmpegDetail(string detail)
    {
        if (string.IsNullOrWhiteSpace(detail))
        {
            return "无详细输出";
        }

        return detail.Length > 2000 ? detail[^2000..] : detail;
    }

    private static string PreviewUrl(string url)
    {
        var query = url.IndexOf('?');
        return query >= 0 ? url[..query] + "?..." : url;
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // best-effort cleanup
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, true);
            }
        }
        catch
        {
            // Cleanup is best-effort; the caller should still see the original failure.
        }
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
            // best-effort cleanup
        }
    }

    private sealed record HlsPlaylist(int Version, int MediaSequence, bool IndependentSegments, string? MapLine, string? KeyLine, List<HlsSegment> Segments);

    private sealed record HlsSegment(double DurationSeconds, string Uri, List<string> Tags);

    private sealed record LiveStaticPlaylistResult(string Path, int SelectedSegments, int TotalSegments, double ActualSeconds);
}
