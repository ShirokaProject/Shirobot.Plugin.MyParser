using ShiroBot.SDK.Abstractions;

namespace Shirobot.Plugin.MyParser.Downloading;

internal sealed class DownloadProgressLogger(bool enabled, int intervalSeconds, string logPrefix = "MyParser", string identifierName = "media_id")
{
    private readonly int _intervalSeconds = Math.Clamp(intervalSeconds, 1, 30);
    public int IntervalMilliseconds => _intervalSeconds * 1000;

    public void LogStart(string mediaId, string path, long? totalBytes, string mode)
    {
        if (!enabled)
        {
            return;
        }

        BotLog.Info($"{logPrefix} 下载开始: {identifierName}={mediaId}, mode={mode}, total={FormatBytes(totalBytes)}, path={path}");
    }

    public void LogComplete(string mediaId, string path, long totalBytes, TimeSpan elapsed)
    {
        if (!enabled)
        {
            return;
        }

        var speed = elapsed.TotalSeconds > 0 ? totalBytes / elapsed.TotalSeconds : 0;
        BotLog.Info($"{logPrefix} 下载完成: {identifierName}={mediaId}, total={FormatBytes(totalBytes)}, elapsed={FormatDuration(elapsed)}, avg_speed={FormatBytes(speed)}/s, path={path}");
    }

    public void LogProgress(string mode, string mediaId, long downloadedBytes, long? totalBytes, TimeSpan elapsed, ref TimeSpan nextLogAt)
    {
        if (!enabled || elapsed < nextLogAt)
        {
            return;
        }

        nextLogAt = elapsed + TimeSpan.FromSeconds(_intervalSeconds);
        LogProgressCore(mode, mediaId, downloadedBytes, totalBytes, elapsed);
    }

    public void LogProgressThreadSafe(string mode, string mediaId, long downloadedBytes, long? totalBytes, TimeSpan elapsed, ref long nextLogAtTicks)
    {
        if (!enabled)
        {
            return;
        }

        var intervalTicks = TimeSpan.FromSeconds(_intervalSeconds).Ticks;
        var nowTicks = elapsed.Ticks;
        var next = Interlocked.Read(ref nextLogAtTicks);
        if (nowTicks < next)
        {
            return;
        }

        if (Interlocked.CompareExchange(ref nextLogAtTicks, nowTicks + intervalTicks, next) == next)
        {
            LogProgressCore(mode, mediaId, downloadedBytes, totalBytes, elapsed);
        }
    }

    private void LogProgressCore(string mode, string mediaId, long downloadedBytes, long? totalBytes, TimeSpan elapsed)
    {
        var speed = elapsed.TotalSeconds > 0 ? downloadedBytes / elapsed.TotalSeconds : 0;
        string percent;
        string eta;
        if (totalBytes is > 0 && speed > 0)
        {
            percent = $"{downloadedBytes * 100d / totalBytes.Value:F1}%";
            eta = FormatDuration(TimeSpan.FromSeconds(Math.Max(0, (totalBytes.Value - downloadedBytes) / speed)));
        }
        else
        {
            percent = "unknown";
            eta = "unknown";
        }

        BotLog.Info($"{logPrefix} 下载进度: {identifierName}={mediaId}, mode={mode}, progress={percent}, downloaded={FormatBytes(downloadedBytes)}/{FormatBytes(totalBytes)}, speed={FormatBytes(speed)}/s, eta={eta}, elapsed={FormatDuration(elapsed)}");
    }

    public static string FormatBytes(double? bytes)
    {
        if (bytes is null || bytes < 0)
        {
            return "unknown";
        }

        string[] units = ["B", "KB", "MB", "GB"];
        var value = bytes.Value;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return $"{value:F2}{units[unit]}";
    }

    public static string FormatDuration(TimeSpan duration)
    {
        return duration.TotalHours >= 1
            ? duration.ToString(@"hh\:mm\:ss")
            : duration.ToString(@"mm\:ss");
    }
}
