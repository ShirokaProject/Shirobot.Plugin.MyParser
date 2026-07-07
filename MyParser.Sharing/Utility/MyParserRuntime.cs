using System.Collections.Concurrent;

namespace Shirobot.Plugin.MyParser;

internal static class MyParserRuntime
{
    private static readonly ConcurrentDictionary<string, Lazy<Task<(string FileUri, string LocalPath)>>> VideoDownloadCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Lock BackgroundLock = new();
    private static CancellationTokenSource BackgroundCancellation = new();

    public static string DouyinCookie { get; set; } = string.Empty;

    public static string BilibiliCookie { get; set; } = string.Empty;

    public static string XiaohongshuCookie { get; set; } = string.Empty;

    public static string NetEaseCloudMusicCookie { get; set; } = string.Empty;

    public static string HeyboxCookie { get; set; } = string.Empty;

    public static string WeixinChannelsYuanbaoCookie { get; set; } = string.Empty;

    public static string WeixinChannelsDownloadDirectory { get; set; } = string.Empty;

    public static string DownloadDirectory { get; set; } = string.Empty;

    public static string BilibiliDownloadDirectory { get; set; } = string.Empty;

    public static string XiaohongshuDownloadDirectory { get; set; } = string.Empty;

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

        VideoDownloadCache.Clear();
        DouyinCookie = string.Empty;
        BilibiliCookie = string.Empty;
        XiaohongshuCookie = string.Empty;
        NetEaseCloudMusicCookie = string.Empty;
        HeyboxCookie = string.Empty;
        WeixinChannelsYuanbaoCookie = string.Empty;
        WeixinChannelsDownloadDirectory = string.Empty;
        DownloadDirectory = string.Empty;
        BilibiliDownloadDirectory = string.Empty;
        XiaohongshuDownloadDirectory = string.Empty;
    }

    public static bool IsCachedVideoPath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        return VideoDownloadCache.Values.Any(lazy =>
            lazy.IsValueCreated
            && lazy.Value.IsCompletedSuccessfully
            && string.Equals(Path.GetFullPath(lazy.Value.Result.LocalPath), fullPath, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal));
    }

    public static void RemoveCachedVideoPath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        foreach (var item in VideoDownloadCache)
        {
            var lazy = item.Value;
            if (lazy.IsValueCreated
                && lazy.Value.IsCompletedSuccessfully
                && string.Equals(Path.GetFullPath(lazy.Value.Result.LocalPath), fullPath, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            {
                VideoDownloadCache.TryRemove(item.Key, out _);
            }
        }
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
