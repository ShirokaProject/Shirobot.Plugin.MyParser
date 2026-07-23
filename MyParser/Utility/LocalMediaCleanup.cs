using ShiroBot.SDK.Abstractions;

namespace Shirobot.Plugin.MyParser.Utility;

internal static class LocalMediaCleanup
{
    public static void CleanupStartupResidues(PluginConfig config)
    {
        try
        {
            var roots = GetStartupTempRoots(config)
                .Where(Directory.Exists)
                .Distinct(GetPathComparer())
                .OrderBy(path => path.Length)
                .ToArray();
            foreach (var root in roots)
            {
                CleanupRootContents(root);
            }
        }
        catch
        {
            // Startup cleanup is best-effort and must never block plugin loading.
        }
    }

    public static void DeleteLocalVideoIfConfigured(PluginConfig config, string? localPath, string provider)
    {
        if (!config.DeleteLocalVideoAfterSend || string.IsNullOrWhiteSpace(localPath))
        {
            return;
        }

        var delaySeconds = Math.Max(0, config.DeleteLocalVideoDelaySeconds);
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
        yield return ResolveRoot(MyParserRuntime.WeixinChannelsDownloadDirectory, Path.Combine("downloads", "MyParser", "weixinchannels"));
        yield return Path.Combine(Path.GetTempPath(), "Shirobot.Plugin.MyParser");
    }

    private static IEnumerable<string> GetStartupTempRoots(PluginConfig config)
    {
        var mediaRoots = GetAllowedMediaRoots(config).Select(Path.GetFullPath).ToArray();
        var pluginTempRoot = mediaRoots
            .Select(Path.GetDirectoryName)
            .FirstOrDefault(path => string.Equals(Path.GetFileName(path), "tmp", StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(pluginTempRoot))
        {
            yield return pluginTempRoot;
        }

        foreach (var root in mediaRoots)
        {
            yield return root;
        }
    }

    private static void CleanupRootContents(string root)
    {
        var rootInfo = new DirectoryInfo(Path.GetFullPath(root));
        if (!rootInfo.Exists)
        {
            return;
        }

        var deleted = 0;
        var failed = 0;
        foreach (var entry in rootInfo.EnumerateFileSystemInfos("*", SearchOption.TopDirectoryOnly))
        {
            try
            {
                switch (entry)
                {
                    case FileInfo file when file.Exists:
                        file.IsReadOnly = false;
                        file.Delete();
                        break;
                    case DirectoryInfo dir when dir.Exists:
                        dir.Delete(true);
                        break;
                }

                deleted++;
            }
            catch (Exception ex)
            {
                failed++;
                BotLog.Warning($"MyParser 启动清理残留失败: path={entry.FullName}, error={ex.Message}");
            }
        }

        if (deleted > 0 || failed > 0)
        {
            BotLog.Info($"MyParser 启动临时目录清理完成: root={rootInfo.FullName}, deleted={deleted}, failed={failed}");
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
