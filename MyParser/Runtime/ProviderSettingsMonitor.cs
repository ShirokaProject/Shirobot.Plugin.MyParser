using System.Text;
using ShiroBot.SDK.Plugin;
using Shirobot.Plugin.MyParser.Parsing;
using Shirobot.Plugin.MyParser.Media;

namespace Shirobot.Plugin.MyParser;

internal sealed class ProviderSettingsMonitor(IBotContext context, PluginConfig config) : IDisposable
{
    private const string CookieDirectoryName = "cookies";
    private readonly Lock _reloadLock = new();
    private FileSystemWatcher? _cookieWatcher;
    private CancellationTokenSource? _cookieReloadDebounce;
    private IReadOnlyList<ProviderCookieDescriptor> _cookieDescriptors = [];
    private bool _disposed;

    public string PluginDirectory => string.IsNullOrWhiteSpace(context.PluginDirectory)
        ? Path.GetDirectoryName(context.Config.ConfigPath) ?? AppContext.BaseDirectory
        : context.PluginDirectory;

    public void InitializeRuntimeDirectories()
    {
        Directory.CreateDirectory(PluginDirectory);
        MyParserRuntime.DouyinCookie = string.Empty;
        MyParserRuntime.BilibiliCookie = string.Empty;
        MyParserRuntime.NetEaseCloudMusicCookie = string.Empty;
        MyParserRuntime.DownloadDirectory = Path.Combine(PluginDirectory, "tmp", "douyin");
        MyParserRuntime.BilibiliDownloadDirectory = Path.Combine(PluginDirectory, "tmp", "bilibili");
        MyParserRuntime.YouTubeDownloadDirectory = Path.Combine(PluginDirectory, "tmp", "youtube");
        MyParserRuntime.WeixinChannelsDownloadDirectory = Path.Combine(PluginDirectory, "tmp", "weixinchannels");

        Directory.CreateDirectory(Path.Combine(PluginDirectory, CookieDirectoryName));
        TemporaryMediaCleanupService.CleanupStartupResidues(config);
        Directory.CreateDirectory(MyParserRuntime.DownloadDirectory);
        Directory.CreateDirectory(MyParserRuntime.BilibiliDownloadDirectory);
        Directory.CreateDirectory(MyParserRuntime.YouTubeDownloadDirectory);
        Directory.CreateDirectory(MyParserRuntime.WeixinChannelsDownloadDirectory);
    }

    public void LoadCookies(IEnumerable<ProviderCookieDescriptor> descriptors)
    {
        _cookieDescriptors = descriptors.ToArray();
        foreach (var descriptor in _cookieDescriptors) LoadCookie(descriptor);
    }

    public string ResolveCookiePath(string fileName)
    {
        var directory = Path.Combine(PluginDirectory, CookieDirectoryName);
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, Path.GetFileName(fileName));
    }

    public void StartWatchers()
    {
        StartCookieWatcher();
    }

    private void StartCookieWatcher()
    {
        var directory = Path.Combine(PluginDirectory, CookieDirectoryName);
        Directory.CreateDirectory(directory);
        _cookieWatcher = new FileSystemWatcher(directory, "*.txt")
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime | NotifyFilters.FileName,
            IncludeSubdirectories = false,
            EnableRaisingEvents = true,
        };
        _cookieWatcher.Changed += (_, _) => ScheduleCookieReload();
        _cookieWatcher.Created += (_, _) => ScheduleCookieReload();
        _cookieWatcher.Deleted += (_, _) => ScheduleCookieReload();
        _cookieWatcher.Renamed += (_, _) => ScheduleCookieReload();
        BotLog.Info($"MyParser Cookie 热重载已启用：{directory}");
    }


    private void ScheduleCookieReload() => ScheduleDebouncedReload(ref _cookieReloadDebounce, ReloadCookies, "Cookie");

    private void ScheduleDebouncedReload(ref CancellationTokenSource? debounce, Action reloadAction, string name)
    {
        lock (_reloadLock)
        {
            if (_disposed) return;
            debounce?.Cancel();
            debounce?.Dispose();
            debounce = new CancellationTokenSource();
            var token = debounce.Token;
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(500, token).ConfigureAwait(false);
                    reloadAction();
                }
                catch (OperationCanceledException)
                {
                    // debounce
                }
                catch (Exception ex)
                {
                    BotLog.Warning($"MyParser {name}热重载失败：{ex.GetType().Name}: {ex.Message}");
                }
            }, CancellationToken.None);
        }
    }

    private void ReloadCookies()
    {
        foreach (var descriptor in _cookieDescriptors) LoadCookie(descriptor);
        BotLog.Info("MyParser Cookie 文件已热重载。");
    }

    private void LoadCookie(ProviderCookieDescriptor descriptor)
    {
        var path = ResolveCookiePath(descriptor.FileName);
        if (!File.Exists(path))
        {
            descriptor.ApplyCookie(string.Empty);
            if (descriptor.CreateIfMissing)
            {
                File.WriteAllText(path, string.Empty, Encoding.UTF8);
                BotLog.Info($"MyParser 已创建 {descriptor.DisplayName} Cookie 文件：{path}");
            }
            return;
        }

        var cookie = File.ReadAllText(path, Encoding.UTF8).Trim().TrimStart('\ufeff');
        if (string.IsNullOrWhiteSpace(cookie))
        {
            descriptor.ApplyCookie(string.Empty);
            BotLog.Info($"MyParser {descriptor.DisplayName}Cookie 为空；{descriptor.EmptyHint ?? $"可编辑文件后重启：{path}"}");
            return;
        }

        if (descriptor.ValidateCookie is not null && !descriptor.ValidateCookie(cookie))
        {
            descriptor.ApplyCookie(string.Empty);
            BotLog.Warning($"MyParser 忽略无效 {descriptor.DisplayName}Cookie 文件：{path}。{descriptor.InvalidHint ?? "请检查 Cookie 内容。"}");
            return;
        }

        descriptor.ApplyCookie(cookie);
        BotLog.Info($"MyParser 已从插件目录读取 {descriptor.DisplayName}Cookie：{path}");
    }

    public void Dispose()
    {
        lock (_reloadLock)
        {
            if (_disposed) return;
            _disposed = true;
            _cookieReloadDebounce?.Cancel();
            _cookieReloadDebounce?.Dispose();
            _cookieReloadDebounce = null;
            _cookieWatcher?.Dispose();
            _cookieWatcher = null;
        }
    }
}
