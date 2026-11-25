using System.Diagnostics;

namespace Shirobot.Plugin.MyParser.Downloading;

/// <summary>
/// 从本地视频截取首帧作为 VideoSegment 缩略图。
/// QQ 协议端会按缩略图尺寸决定视频气泡比例，平台封面常被裁成 1:1 / 3:4，
/// 与视频真实比例不一致；使用首帧可以保证比例与视频一致。
/// </summary>
internal static class VideoThumbnailExtractor
{
    private const int MaxEdge = 720;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    public static async Task<string?> TryExtractFirstFrameAsync(
        PluginConfig config,
        string videoPath,
        CancellationToken cancellationToken)
    {
        try
        {
            var thumbPath = videoPath + ".thumb.jpg";
            if (File.Exists(thumbPath) && new FileInfo(thumbPath).Length > 0)
            {
                return thumbPath;
            }

            var ffmpeg = ProviderDownloadService.ResolveFfmpegPath(config);
            if (ffmpeg is null)
            {
                BotLog.Warning("MyParser 未找到 ffmpeg，无法截取视频首帧作为缩略图，回退平台封面。");
                return null;
            }

            var tempPath = thumbPath + ".tmp.jpg";
            var psi = new ProcessStartInfo
            {
                FileName = ffmpeg,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            foreach (var arg in new[]
                     {
                         "-hide_banner", "-loglevel", "error", "-y",
                         "-i", videoPath,
                         "-frames:v", "1",
                         // 等比缩放到长边不超过 MaxEdge，保持视频原始宽高比（含旋转元数据，ffmpeg 默认 autorotate）。
                         "-vf", $"scale='if(gte(iw,ih),min({MaxEdge},iw),-2)':'if(gte(iw,ih),-2,min({MaxEdge},ih))'",
                         "-q:v", "3",
                         tempPath,
                     })
            {
                psi.ArgumentList.Add(arg);
            }

            using var process = Process.Start(psi) ?? throw new InvalidOperationException("ffmpeg 启动失败。");
            var stderrTask = process.StandardError.ReadToEndAsync(CancellationToken.None);
            var stdoutTask = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(Timeout);
            try
            {
                await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { /* ignore */ }
                TryDelete(tempPath);
                cancellationToken.ThrowIfCancellationRequested();
                BotLog.Warning($"MyParser 截取视频首帧超时（>{Timeout.TotalSeconds:F0}s），回退平台封面。");
                return null;
            }

            var stderr = await stderrTask.ConfigureAwait(false);
            await stdoutTask.ConfigureAwait(false);
            if (process.ExitCode != 0 || !File.Exists(tempPath) || new FileInfo(tempPath).Length == 0)
            {
                TryDelete(tempPath);
                var detail = stderr.Length > 500 ? stderr[^500..] : stderr;
                BotLog.Warning($"MyParser 截取视频首帧失败，回退平台封面: exit={process.ExitCode}, error={detail.Trim()}");
                return null;
            }

            File.Move(tempPath, thumbPath, overwrite: true);
            global::Shirobot.Plugin.MyParser.DiagnosticLog.Info($"MyParser 已截取视频首帧作为缩略图: file={thumbPath}, bytes={new FileInfo(thumbPath).Length}");
            return thumbPath;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            BotLog.Warning($"MyParser 截取视频首帧异常，回退平台封面: error={ex.Message}");
            return null;
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* ignore */ }
    }
}
