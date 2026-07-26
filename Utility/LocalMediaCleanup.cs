using ShiroBot.SDK.Abstractions;

namespace Shirobot.Plugin.MyParser.Utility;

internal static class LocalMediaCleanup
{
    public const int HttpVideoSendGraceSeconds = 300;

    public static void CleanupStartupResidues(PluginConfig config)
    {
        try
        {
            var roots = GetAllowedMediaRoots(config).Where(Directory.Exists).Distinct(GetPathComparer()).ToArray();
            foreach (var root in roots)
            {
                CleanupRootResidues(root);
            }
        }
        catch
        {
            // Startup cleanup is best-effort and must never block plugin loading.
        }
    }

    public static void DeleteLocalVideoIfConfigured(PluginConfig config, string? localPath, string provider, int minimumDelaySeconds = 0)
    {
        if (!config.DeleteLocalVideoAfterSend || string.IsNullOrWhiteSpace(localPath))
        {
            return;
        }

        var delaySeconds = Math.Max(Math.Max(0, config.DeleteLocalVideoDelaySeconds), minimumDelaySeconds);
        if (delaySeconds <= 0 && MyParserRuntime.IsCachedVideoPath(localPath))
        {
            // Give concurrent duplicate requests time to reuse/send the same cached file.
            delaySeconds = 30;
        }

        if (delaySeconds <= 0)
        {
            DeleteLocalVideoNow(config, localPath, provider);
            return;
        }

        var cancellationToken = MyParserRuntime.BackgroundCancellationToken;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(delaySeconds), cancellationToken);
                DeleteLocalVideoNow(config, localPath, provider);
            }
            catch (OperationCanceledException)
            {
                // Plugin unload cancels pending delayed cleanup so the assembly can be released.
            }
            catch
            {
                // Cleanup is best-effort and must never affect message sending.
            }
        }, cancellationToken);
    }

    private static void DeleteLocalVideoNow(PluginConfig config, string localPath, string provider)
    {
        try
        {
            var fullPath = Path.GetFullPath(localPath);
            if (!File.Exists(fullPath) || !IsUnderAllowedMediaRoot(config, fullPath))
            {
                return;
            }

            TryDeleteFile(fullPath);
            MyParserRuntime.RemoveCachedVideoPath(fullPath);
            if (string.Equals(provider, "bilibili", StringComparison.OrdinalIgnoreCase))
            {
                var dir = Path.GetDirectoryName(fullPath);
                if (!string.IsNullOrWhiteSpace(dir) && IsUnderAllowedMediaRoot(config, dir))
                {
                    if (IsBilibiliLiveClipDirectory(dir))
                    {
                        TryDeleteDirectoryRecursive(dir);
                        var parentDir = Path.GetDirectoryName(dir);
                        if (!string.IsNullOrWhiteSpace(parentDir))
                        {
                            TryDeleteDirectoryIfEmpty(parentDir);
                        }

                        return;
                    }

                    TryDeleteFile(Path.Combine(dir, "video.m4s"));
                    TryDeleteFile(Path.Combine(dir, "audio.m4s"));
                    TryDeleteDirectoryIfEmpty(dir);
                }
            }
        }
        catch
        {
            // Cleanup is best-effort and must never affect message sending.
        }
    }

    private static bool IsUnderAllowedMediaRoot(PluginConfig config, string path)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var normalizedPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return GetAllowedMediaRoots(config)
            .Where(i => !string.IsNullOrWhiteSpace(i))
            .Select(i => Path.GetFullPath(i).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar)
            .Any(root => normalizedPath.StartsWith(root, comparison));
    }

    private static IEnumerable<string> GetAllowedMediaRoots(PluginConfig config)
    {
        yield return ResolveRoot(MyParserRuntime.DownloadDirectory, Path.Combine("downloads", "MyParser", "douyin"));
        yield return ResolveRoot(MyParserRuntime.BilibiliDownloadDirectory, Path.Combine("downloads", "MyParser", "bilibili"));
        yield return ResolveRoot(MyParserRuntime.XiaohongshuDownloadDirectory, Path.Combine("downloads", "MyParser", "xiaohongshu"));
        yield return Path.Combine(Path.GetTempPath(), "Shirobot.Plugin.MyParser");
    }

    private static void CleanupRootResidues(string root)
    {
        var rootFullPath = Path.GetFullPath(root);
        var entries = new List<FileSystemInfo>();
        var rootInfo = new DirectoryInfo(rootFullPath);
        entries.AddRange(rootInfo.EnumerateFiles("*.download", SearchOption.AllDirectories));
        entries.AddRange(rootInfo.EnumerateFiles("*_video.m4s", SearchOption.TopDirectoryOnly));
        entries.AddRange(rootInfo.EnumerateFiles("*_audio.m4s", SearchOption.TopDirectoryOnly));
        entries.AddRange(rootInfo.EnumerateFiles("video.m4s", SearchOption.AllDirectories));
        entries.AddRange(rootInfo.EnumerateFiles("audio.m4s", SearchOption.AllDirectories));

        var liveClips = Path.Combine(rootFullPath, "live-clips");
        if (Directory.Exists(liveClips))
        {
            entries.AddRange(new DirectoryInfo(liveClips).EnumerateDirectories("*", SearchOption.TopDirectoryOnly));
        }

        foreach (var entry in entries.OrderBy(i => i.LastWriteTimeUtc))
        {
            try
            {
                switch (entry)
                {
                    case FileInfo file when file.Exists:
                        file.Delete();
                        BotLog.Info($"MyParser 启动清理残留文件: {file.FullName}");
                        break;
                    case DirectoryInfo dir when dir.Exists:
                        dir.Delete(true);
                        BotLog.Info($"MyParser 启动清理残留目录: {dir.FullName}");
                        break;
                }
            }
            catch (Exception ex)
            {
                BotLog.Warning($"MyParser 启动清理残留失败: path={entry.FullName}, error={ex.Message}");
            }
        }

        CleanupEmptyDirectories(rootInfo);
    }

    private static void CleanupEmptyDirectories(DirectoryInfo root)
    {
        foreach (var dir in root.EnumerateDirectories("*", SearchOption.AllDirectories).OrderByDescending(i => i.FullName.Length))
        {
            TryDeleteDirectoryIfEmpty(dir.FullName);
        }
    }

    private static StringComparer GetPathComparer()
    {
        return OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    }

    private static bool IsBilibiliLiveClipDirectory(string path)
    {
        var dir = new DirectoryInfo(path);
        return string.Equals(dir.Parent?.Name, "live-clips", StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolveRoot(string? configured, string fallback)
    {
        var value = string.IsNullOrWhiteSpace(configured) ? fallback : configured.Trim();
        return Path.IsPathRooted(value) ? value : Path.Combine(AppContext.BaseDirectory, value);
    }

    private static void TryDeleteFile(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private static void TryDeleteDirectoryIfEmpty(string path)
    {
        if (Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any())
        {
            Directory.Delete(path, false);
        }
    }

    private static void TryDeleteDirectoryRecursive(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, true);
        }
    }
}
