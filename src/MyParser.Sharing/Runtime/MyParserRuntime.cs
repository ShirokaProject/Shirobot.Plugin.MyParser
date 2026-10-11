using ShiroBot.SDK.Plugin;
using System.Collections.Concurrent;

namespace Shirobot.Plugin.MyParser;

internal static class MyParserRuntime
{
    private static readonly ConcurrentDictionary<string, Lazy<Task<(string FileUri, string LocalPath)>>> VideoDownloadCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Lock BackgroundLock = new();
    private static CancellationTokenSource BackgroundCancellation = new();

    public static string DouyinCookie { get; set; } = string.Empty;

    public static string BilibiliCookie { get; set; } = string.Empty;

    public static string NetEaseCloudMusicCookie { get; set; } = string.Empty;

    public static string HeyboxCookie { get; set; } = string.Empty;

    public static string WeixinChannelsYuanbaoCookie { get; set; } = string.Empty;

    // Every operation gets a fresh host-owned directory; retention starts at creation.
    public static Func<TemporaryDirectory>? TemporaryDirectoryFactory { get; set; }
    private static readonly ConcurrentDictionary<string, DateTimeOffset> TemporaryDirectories = new();

    public static string CreateTempDirectory()
    {
        var temporary = TemporaryDirectoryFactory?.Invoke()
            ?? throw new InvalidOperationException("宿主临时目录服务尚未初始化。");
        foreach (var entry in TemporaryDirectories.Where(entry => entry.Value <= DateTimeOffset.UtcNow))
            TemporaryDirectories.TryRemove(entry.Key, out _);
        var path = Path.GetFullPath(temporary.Path);
        TemporaryDirectories[path] = temporary.ExpiresAt;
        return path;
    }

    public static bool IsTemporaryFile(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return TemporaryDirectories.Any(entry => entry.Value > DateTimeOffset.UtcNow
            && fullPath.StartsWith(entry.Key.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar, comparison));
    }

    public static CancellationToken BackgroundCancellationToken
    {
        get
        {
            lock (BackgroundLock)
            {
                return BackgroundCancellation.Token;
            }
        }
    }

    public static void ResetForLoad()
    {
        lock (BackgroundLock)
        {
            if (BackgroundCancellation.IsCancellationRequested)
            {
                BackgroundCancellation.Dispose();
                BackgroundCancellation = new CancellationTokenSource();
            }
        }
    }

    public static void BeginUnload()
    {
        lock (BackgroundLock)
        {
            BackgroundCancellation.Cancel();
        }

        TemporaryDirectoryFactory = null;
        TemporaryDirectories.Clear();
        VideoDownloadCache.Clear();
        DouyinCookie = string.Empty;
        BilibiliCookie = string.Empty;
        NetEaseCloudMusicCookie = string.Empty;
        HeyboxCookie = string.Empty;
        WeixinChannelsYuanbaoCookie = string.Empty;
    }

    public static async Task<(string FileUri, string LocalPath)> GetOrAddVideoDownloadAsync(
        string cacheKey,
        Func<Task<(string FileUri, string LocalPath)>> factory)
    {
        while (true)
        {
            var lazy = VideoDownloadCache.GetOrAdd(
                cacheKey,
                _ => new Lazy<Task<(string FileUri, string LocalPath)>>(factory, LazyThreadSafetyMode.ExecutionAndPublication));

            try
            {
                var result = await lazy.Value.ConfigureAwait(false);
                if (File.Exists(result.LocalPath))
                {
                    return result;
                }

                VideoDownloadCache.TryRemove(new KeyValuePair<string, Lazy<Task<(string FileUri, string LocalPath)>>>(cacheKey, lazy));
            }
            catch
            {
                VideoDownloadCache.TryRemove(new KeyValuePair<string, Lazy<Task<(string FileUri, string LocalPath)>>>(cacheKey, lazy));
                throw;
            }
        }
    }
}
