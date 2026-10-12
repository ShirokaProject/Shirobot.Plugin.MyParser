using ShiroBot.SDK.Plugin;
using System.Collections.Concurrent;

namespace Shirobot.Plugin.MyParser;

internal static class MyParserRuntime
{
    private static readonly ConcurrentDictionary<string, DownloadCacheEntry> VideoDownloadCache = new(StringComparer.OrdinalIgnoreCase);
    private static Timer? CacheExpiryTimer;
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

    public static void ForgetTemporaryDirectory(string path) => TemporaryDirectories.TryRemove(Path.GetFullPath(path), out _);

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
            CacheExpiryTimer ??= new Timer(_ => PruneCaches(), null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
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
            CacheExpiryTimer?.Dispose();
            CacheExpiryTimer = null;
            BackgroundCancellation.Cancel();
        }

        DiagnosticLog.Configure(null);
        TemporaryDirectoryFactory = null;
        TemporaryDirectories.Clear();
        VideoDownloadCache.Clear();
        DouyinCookie = string.Empty;
        BilibiliCookie = string.Empty;
        NetEaseCloudMusicCookie = string.Empty;
        HeyboxCookie = string.Empty;
        WeixinChannelsYuanbaoCookie = string.Empty;
    }

    private sealed class DownloadCacheEntry(Func<Task<(string FileUri, string LocalPath)>> factory)
    {
        private readonly Lazy<Task<(string FileUri, string LocalPath)>> _download = new(factory, LazyThreadSafetyMode.ExecutionAndPublication);
        public DateTimeOffset CreatedAt { get; } = DateTimeOffset.UtcNow;
        public bool IsValueCreated => _download.IsValueCreated;
        public Task<(string FileUri, string LocalPath)> Value => _download.Value;
    }

    private static void PruneCaches()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var entry in TemporaryDirectories)
            if (entry.Value <= now) TemporaryDirectories.TryRemove(entry);
        var completed = VideoDownloadCache.Where(entry => entry.Value.IsValueCreated && entry.Value.Value.IsCompleted).ToArray();
        foreach (var entry in completed.OrderBy(entry => entry.Value.CreatedAt))
        {
            var task = entry.Value.Value;
            if (!task.IsCompletedSuccessfully || !File.Exists(task.Result.LocalPath)
                || entry.Value.CreatedAt <= now.AddHours(-6)
                || VideoDownloadCache.Count > 512)
            {
                VideoDownloadCache.TryRemove(entry);
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
                _ => new DownloadCacheEntry(factory));

            try
            {
                var result = await lazy.Value.ConfigureAwait(false);
                if (File.Exists(result.LocalPath))
                {
                    if (VideoDownloadCache.Count > 512) PruneCaches();
                    return result;
                }

                VideoDownloadCache.TryRemove(new KeyValuePair<string, DownloadCacheEntry>(cacheKey, lazy));
            }
            catch
            {
                VideoDownloadCache.TryRemove(new KeyValuePair<string, DownloadCacheEntry>(cacheKey, lazy));
                throw;
            }
        }
    }
}
