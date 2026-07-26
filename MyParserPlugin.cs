using Shirobot.Plugin.MyParser.Providers.Bilibili.Facade;
using Shirobot.Plugin.MyParser.Providers.Bilibili.Impl.MessageHandling.Impl;
using Shirobot.Plugin.MyParser.Providers.Bilibili.Impl.Services;
using Shirobot.Plugin.MyParser.Providers.Douyin.Impl.MessageHandling;
using Shirobot.Plugin.MyParser.Providers.Douyin.Facade;
using Shirobot.Plugin.MyParser.Providers.Xiaohongshu.Facade;
using Shirobot.Plugin.MyParser.Providers.Xiaohongshu.Impl.MessageHandling;
using System.Text;
using Shirobot.Plugin.MyParser.Parsing;
using Shirobot.Plugin.MyParser.Providers.Common.MessageHandling;
using Shirobot.Plugin.MyParser.Providers.Bilibili.Utilities;
using Shirobot.Plugin.MyParser.Utility;
using ShiroBot.SDK.Abstractions;
using ShiroBot.SDK.Core;
using ShiroBot.SDK.Models;
using ShiroBot.SDK.Plugin;

namespace Shirobot.Plugin.MyParser;

[BotPlugin(id: "MyParser",
    Name = "MyParser",
    Version = "0.2.0",
    Author = "PVPGood",
    Category = PluginCategory.Utility,
    Description = "面向 Shirobot 的学习型内容消息处理插件。",
    GithubRepo = "PVPGOOD/Shirobot.Plugin.MyParser",
    IsPluginSingleFile = false)
]
public sealed class MyParserPlugin : PluginBase
{
    private const string CookieDirectoryName = "cookies";
    private const string DouyinCookieFileName = "douyin.txt";
    private const string BilibiliCookieFileName = "bilibili.txt";
    private const string XiaohongshuCookieFileName = "xiaohongshu.txt";
    private const string BilibiliLoginCommand = "#bili-login";
    private const string XiaohongshuLoginCommand = "#xhs-login";
    private const string DouyinCookieCheckCommand = "#douyin-cookie-check";
    private const string BilibiliCookieCheckCommand = "#bili-cookie-check";
    private const string XiaohongshuCookieCheckCommand = "#xhs-cookie-check";

    private readonly Lock _reloadLock = new();
    private PluginConfig _config = new();
    private FileSystemWatcher? _configWatcher;
    private FileSystemWatcher? _cookieWatcher;
    private CancellationTokenSource? _configReloadDebounce;
    private CancellationTokenSource? _cookieReloadDebounce;
    private DouyinParseProvider? _douyinProvider;
    private BilibiliParseProvider? _bilibiliProvider;
    private BilibiliArticleParseProvider? _bilibiliArticleProvider;
    private BilibiliBangumiParseProvider? _bilibiliBangumiProvider;
    private BilibiliLiveParseProvider? _bilibiliLiveProvider;
    private XiaohongshuParseProvider? _xiaohongshuProvider;
    private ParseProviderRegistry? _providerRegistry;
    private DouyinMessageHandler? _douyinMessageHandler;
    private BilibiliMessageHandler? _bilibiliMessageHandler;
    private XiaohongshuMessageHandler? _xiaohongshuMessageHandler;

    public override string Name => "MyParser";

    protected override async Task LoadAsync()
    {
        MyParserRuntime.ResetForLoad();
        MessageHandlerCommon.ClearReactionCache();
        _config = Context.Config.Load<PluginConfig>();
        NormalizeRuntimeDirectories();
        LoadDouyinCookieFromPluginDirectory();
        LoadBilibiliCookieFromPluginDirectory();
        LoadXiaohongshuCookieFromPluginDirectory();
        _douyinProvider = new DouyinParseProvider(new DouyinParser(_config));
        var bilibiliParser = new BilibiliParser(_config);
        _bilibiliProvider = new BilibiliParseProvider(bilibiliParser);
        _bilibiliArticleProvider = new BilibiliArticleParseProvider(bilibiliParser);
        _bilibiliBangumiProvider = new BilibiliBangumiParseProvider(new BilibiliBangumiParser(bilibiliParser.HttpClient, _config));
        _bilibiliLiveProvider = new BilibiliLiveParseProvider(new BilibiliLiveParser(bilibiliParser.HttpClient, _config));
        _xiaohongshuProvider = new XiaohongshuParseProvider(new XiaohongshuParser(_config));
        _providerRegistry = new ParseProviderRegistry([_xiaohongshuProvider, _douyinProvider, _bilibiliArticleProvider, _bilibiliBangumiProvider, _bilibiliLiveProvider, _bilibiliProvider]);
        _douyinMessageHandler = new DouyinMessageHandler(Context, _config, _providerRegistry, _douyinProvider);
        _bilibiliMessageHandler = new BilibiliMessageHandler(Context, _config, _providerRegistry, _bilibiliProvider);
        _xiaohongshuMessageHandler = new XiaohongshuMessageHandler(Context, _config, _providerRegistry, _xiaohongshuProvider);
        
        await LogDouyinCookieLoginStatusAsync();
        await LogBilibiliCookieLoginStatusAsync();
        await LogXiaohongshuCookieLoginStatusAsync();

        // GroupCommands.MapExact("b", async message =>
        // {
        //     BotLog.Error("MyParser 错误日志测试");
        //     var text = GetPlainText(message);
        //     var pic = await Context.RenderControlPngAsync<BiliCard>(new BiliCardViewModel(),
        //         new ControlRenderOptions(RenderTheme.Light,192));
        //     await Context.Message.ReplyAsync(message, $"b卡",new ImageOutgoingSegment($"base64://{Convert.ToBase64String(pic)}"));
        // });
        //
        // GroupCommands.MapExact("d", async message =>
        // {
        //     BotLog.Error("MyParser 错误日志测试");
        //     var text = GetPlainText(message);
        //     var pic = await Context.RenderControlPngAsync<DouyinCard>(new DouyinCardViewModel(),
        //         new ControlRenderOptions(RenderTheme.Light));
        //     await Context.Message.ReplyAsync(message, $"dycard",new ImageOutgoingSegment($"base64://{Convert.ToBase64String(pic)}"));
        // });
        //
        DirectCommands.MapExact("#parser", HandleHelpAsync);
        GroupCommands.MapExact("#parser", HandleHelpAsync);
        DirectCommands.MapExact(BilibiliLoginCommand, HandleBilibiliLoginAsync);
        GroupCommands.MapExact(BilibiliLoginCommand, HandleBilibiliLoginAsync);
        DirectCommands.MapExact(XiaohongshuLoginCommand, HandleXiaohongshuLoginAsync);
        GroupCommands.MapExact(XiaohongshuLoginCommand, HandleXiaohongshuLoginAsync);
        DirectCommands.MapExact(DouyinCookieCheckCommand, HandleDouyinCookieCheckAsync);
        GroupCommands.MapExact(DouyinCookieCheckCommand, HandleDouyinCookieCheckAsync);
        DirectCommands.MapExact(BilibiliCookieCheckCommand, HandleBilibiliCookieCheckAsync);
        GroupCommands.MapExact(BilibiliCookieCheckCommand, HandleBilibiliCookieCheckAsync);
        DirectCommands.MapExact(XiaohongshuCookieCheckCommand, HandleXiaohongshuCookieCheckAsync);
        GroupCommands.MapExact(XiaohongshuCookieCheckCommand, HandleXiaohongshuCookieCheckAsync);

        DirectCommands.MapWhen(IsParseCommand, HandleParseCommandAsync);
        GroupCommands.MapWhen(IsParseCommand, HandleParseCommandAsync);

        DirectCommands.MapWhen(ShouldAutoParse, HandleAutoParseAsync);
        GroupCommands.MapWhen(ShouldAutoParse, HandleAutoParseAsync);

        StartHotReloadWatchers();
        BotLog.Info($"MyParser 已加载：抖音/Bilibili/小红书 解析启用。命令：#parser / {_config.ParseCommandPrefix} <链接> / {BilibiliLoginCommand} / {XiaohongshuLoginCommand}");
    }

    private async Task LogDouyinCookieLoginStatusAsync()
    {
        if (_douyinProvider is null)
        {
            return;
        }

        try
        {
            var status = await _douyinProvider.Parser.CheckLoginStatusAsync();
            BotLog.Info($"MyParser DouyinCookie 登录状态：{status}");
        }
        catch (Exception ex)
        {
            BotLog.Warning($"MyParser DouyinCookie 登录状态检查失败：{ex.GetType().Name}: {ex.Message}");
        }
    }

    private async Task LogBilibiliCookieLoginStatusAsync()
    {
        if (_bilibiliProvider is null)
        {
            return;
        }

        try
        {
            var status = await _bilibiliProvider.Parser.CheckLoginStatusAsync();
            BotLog.Info($"MyParser BilibiliCookie 登录状态：{status.Message}");
        }
        catch (Exception ex)
        {
            BotLog.Warning($"MyParser BilibiliCookie 登录状态检查失败：{ex.GetType().Name}: {ex.Message}");
        }
    }

    private async Task LogXiaohongshuCookieLoginStatusAsync()
    {
        if (_xiaohongshuProvider is null)
        {
            return;
        }

        try
        {
            var status = await _xiaohongshuProvider.Parser.CheckLoginStatusAsync();
            BotLog.Info($"MyParser XiaohongshuCookie 登录状态：{status.Message}");
        }
        catch (Exception ex)
        {
            BotLog.Warning($"MyParser XiaohongshuCookie 登录状态检查失败：{ex.GetType().Name}: {ex.Message}");
        }
    }

    private void LoadDouyinCookieFromPluginDirectory()
    {
        var cookiePath = ResolveCookiePath(DouyinCookieFileName);

        if (!File.Exists(cookiePath))
        {
            MyParserRuntime.DouyinCookie = string.Empty;
            File.WriteAllText(cookiePath, string.Empty, Encoding.UTF8);
            BotLog.Info($"MyParser 已创建抖音 Cookie 文件：{cookiePath}");
            return;
        }

        var cookie = File.ReadAllText(cookiePath, Encoding.UTF8).Trim().TrimStart('\ufeff');
        if (string.IsNullOrWhiteSpace(cookie))
        {
            MyParserRuntime.DouyinCookie = string.Empty;
            BotLog.Info($"MyParser DouyinCookie 为空；可编辑文件后重启：{cookiePath}");
            return;
        }

        if (!LooksLikeDouyinCookie(cookie))
        {
            MyParserRuntime.DouyinCookie = string.Empty;
            BotLog.Warning($"MyParser 忽略无效 DouyinCookie 文件：{cookiePath}。请确保文件内容是浏览器 Request Headers 中 Cookie: 后面的完整值。");
            return;
        }

        MyParserRuntime.DouyinCookie = cookie;
        BotLog.Info($"MyParser 已从插件目录读取 DouyinCookie：{cookiePath}");
    }

    private void LoadBilibiliCookieFromPluginDirectory()
    {
        var cookiePath = ResolveCookiePath(BilibiliCookieFileName);

        if (!File.Exists(cookiePath))
        {
            MyParserRuntime.BilibiliCookie = string.Empty;
            File.WriteAllText(cookiePath, string.Empty, Encoding.UTF8);
            BotLog.Info($"MyParser 已创建 Bilibili Cookie 文件：{cookiePath}");
            return;
        }

        var cookie = File.ReadAllText(cookiePath, Encoding.UTF8).Trim().TrimStart('\ufeff');
        if (string.IsNullOrWhiteSpace(cookie))
        {
            MyParserRuntime.BilibiliCookie = string.Empty;
            BotLog.Info($"MyParser BilibiliCookie 为空；可发送 {BilibiliLoginCommand} 扫码登录，或编辑文件后重启：{cookiePath}");
            return;
        }

        if (!BilibiliParser.LooksLikeBilibiliCookie(cookie))
        {
            MyParserRuntime.BilibiliCookie = string.Empty;
            BotLog.Warning($"MyParser 忽略无效 BilibiliCookie 文件：{cookiePath}。请确保文件内容包含 SESSDATA/bili_jct 等 Cookie。");
            return;
        }

        MyParserRuntime.BilibiliCookie = cookie;
        BotLog.Info($"MyParser 已从插件目录读取 BilibiliCookie：{cookiePath}");
    }

    private void LoadXiaohongshuCookieFromPluginDirectory()
    {
        var cookiePath = ResolveCookiePath(XiaohongshuCookieFileName);

        if (!File.Exists(cookiePath))
        {
            MyParserRuntime.XiaohongshuCookie = string.Empty;
            File.WriteAllText(cookiePath, string.Empty, Encoding.UTF8);
            BotLog.Info($"MyParser 已创建小红书 Cookie 文件：{cookiePath}");
            return;
        }

        var cookie = File.ReadAllText(cookiePath, Encoding.UTF8).Trim().TrimStart('\ufeff');
        if (string.IsNullOrWhiteSpace(cookie))
        {
            MyParserRuntime.XiaohongshuCookie = string.Empty;
            BotLog.Info($"MyParser XiaohongshuCookie 为空；可发送 {XiaohongshuLoginCommand} 扫码登录，或编辑文件后重启：{cookiePath}");
            return;
        }

        MyParserRuntime.XiaohongshuCookie = cookie;
        BotLog.Info($"MyParser 已从插件目录读取 XiaohongshuCookie：{cookiePath}");
    }

    private void NormalizeRuntimeDirectories()
    {
        var pluginDir = GetPluginDirectory();
        Directory.CreateDirectory(pluginDir);

        MyParserRuntime.DouyinCookie = string.Empty;
        MyParserRuntime.BilibiliCookie = string.Empty;
        MyParserRuntime.XiaohongshuCookie = string.Empty;

        MyParserRuntime.DownloadDirectory = Path.Combine(pluginDir, "tmp", "douyin");
        MyParserRuntime.BilibiliDownloadDirectory = Path.Combine(pluginDir, "tmp", "bilibili");
        MyParserRuntime.XiaohongshuDownloadDirectory = Path.Combine(pluginDir, "tmp", "xiaohongshu");

        Directory.CreateDirectory(Path.Combine(pluginDir, CookieDirectoryName));
        Directory.CreateDirectory(MyParserRuntime.DownloadDirectory);
        Directory.CreateDirectory(MyParserRuntime.BilibiliDownloadDirectory);
        Directory.CreateDirectory(MyParserRuntime.XiaohongshuDownloadDirectory);
        LocalMediaCleanup.CleanupStartupResidues(_config);
    }

    private string ResolveCookiePath(string fileName)
    {
        return ResolveCookiePath(GetPluginDirectory(), fileName);
    }

    internal static string ResolveCookiePath(string pluginDirectory, string fileName)
    {
        var cookieDir = Path.Combine(pluginDirectory, CookieDirectoryName);
        Directory.CreateDirectory(cookieDir);
        return Path.Combine(cookieDir, Path.GetFileName(fileName));
    }

    private void StartHotReloadWatchers()
    {
        StartConfigHotReloadWatcher();
        StartCookieHotReloadWatcher();
    }

    private void StartConfigHotReloadWatcher()
    {
        var configPath = Context.Config.ConfigPath;
        if (string.IsNullOrWhiteSpace(configPath))
        {
            return;
        }

        var fullPath = Path.GetFullPath(configPath);
        var directory = Path.GetDirectoryName(fullPath);
        var fileName = Path.GetFileName(fullPath);
        if (string.IsNullOrWhiteSpace(directory) || string.IsNullOrWhiteSpace(fileName))
        {
            return;
        }

        Directory.CreateDirectory(directory);
        _configWatcher = new FileSystemWatcher(directory, fileName)
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime | NotifyFilters.FileName,
            IncludeSubdirectories = false,
            EnableRaisingEvents = true,
        };
        _configWatcher.Changed += (_, _) => ScheduleConfigReload();
        _configWatcher.Created += (_, _) => ScheduleConfigReload();
        _configWatcher.Renamed += (_, _) => ScheduleConfigReload();
        BotLog.Info($"MyParser 配置热重载已启用：{fullPath}");
    }

    private void StartCookieHotReloadWatcher()
    {
        var cookieDir = Path.Combine(GetPluginDirectory(), CookieDirectoryName);
        Directory.CreateDirectory(cookieDir);
        _cookieWatcher = new FileSystemWatcher(cookieDir, "*.txt")
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime | NotifyFilters.FileName,
            IncludeSubdirectories = false,
            EnableRaisingEvents = true,
        };
        _cookieWatcher.Changed += (_, _) => ScheduleCookieReload();
        _cookieWatcher.Created += (_, _) => ScheduleCookieReload();
        _cookieWatcher.Deleted += (_, _) => ScheduleCookieReload();
        _cookieWatcher.Renamed += (_, _) => ScheduleCookieReload();
        BotLog.Info($"MyParser Cookie 热重载已启用：{cookieDir}");
    }

    private void ScheduleConfigReload()
    {
        ScheduleDebouncedReload(ref _configReloadDebounce, ReloadConfigNow, "配置");
    }

    private void ScheduleCookieReload()
    {
        ScheduleDebouncedReload(ref _cookieReloadDebounce, ReloadCookiesNow, "Cookie");
    }

    private void ScheduleDebouncedReload(ref CancellationTokenSource? debounce, Action reloadAction, string name)
    {
        lock (_reloadLock)
        {
            debounce?.Cancel();
            debounce?.Dispose();
            debounce = new CancellationTokenSource();
            var token = debounce.Token;
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(500, token);
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

    private void ReloadConfigNow()
    {
        var updated = Context.Config.Load<PluginConfig>();
        ApplyConfigValues(_config, updated);
        BotLog.Info("MyParser 配置已热重载。注意：命令热重载仅支持 #parse 前缀；登录/Cookie 检查命令为固定命令。 ");
    }

    private void ReloadCookiesNow()
    {
        LoadDouyinCookieFromPluginDirectory();
        LoadBilibiliCookieFromPluginDirectory();
        LoadXiaohongshuCookieFromPluginDirectory();
        BotLog.Info("MyParser Cookie 文件已热重载。 ");
    }

    private static void ApplyConfigValues(PluginConfig target, PluginConfig source)
    {
        foreach (var property in typeof(PluginConfig).GetProperties(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public))
        {
            if (!property.CanRead || !property.CanWrite)
            {
                continue;
            }

            property.SetValue(target, property.GetValue(source));
        }
    }

    private string GetPluginDirectory()
    {
        return string.IsNullOrWhiteSpace(Context.PluginDirectory)
            ? Path.GetDirectoryName(Context.Config.ConfigPath) ?? AppContext.BaseDirectory
            : Context.PluginDirectory;
    }

    private static bool LooksLikeDouyinCookie(string cookie)
    {
        return cookie.Contains("sessionid=", StringComparison.OrdinalIgnoreCase)
               && cookie.Contains("ttwid=", StringComparison.OrdinalIgnoreCase)
               && cookie.Contains(';');
    }

    protected override Task OnUnloadAsync()
    {
        MyParserRuntime.BeginUnload();
        MessageHandlerCommon.ClearReactionCache();
        _configReloadDebounce?.Cancel();
        _configReloadDebounce?.Dispose();
        _configReloadDebounce = null;
        _cookieReloadDebounce?.Cancel();
        _cookieReloadDebounce?.Dispose();
        _cookieReloadDebounce = null;
        _configWatcher?.Dispose();
        _configWatcher = null;
        _cookieWatcher?.Dispose();
        _cookieWatcher = null;

        _douyinMessageHandler?.Dispose();
        _douyinMessageHandler = null;
        _bilibiliMessageHandler?.Dispose();
        _bilibiliMessageHandler = null;
        _douyinProvider?.Dispose();
        _douyinProvider = null;
        _bilibiliProvider?.Dispose();
        _bilibiliProvider = null;
        _bilibiliArticleProvider = null;
        _bilibiliBangumiProvider = null;
        _bilibiliLiveProvider = null;
        _xiaohongshuMessageHandler?.Dispose();
        _xiaohongshuMessageHandler = null;
        _xiaohongshuProvider?.Dispose();
        _xiaohongshuProvider = null;
        _providerRegistry = null;
        BotLog.Info("MyParser 已卸载。");
        return Task.CompletedTask;
    }

    private Task HandleHelpAsync(MessageEvent message)
    {
        var help = "MyParser\n"
                   + "当前支持：抖音视频 / 图集 / LivePhoto、Bilibili 视频/专栏/图文、小红书视频/图文/评论卡片\n\n"
                   + "用法：\n"
                   + $"1. {_config.ParseCommandPrefix} <抖音/Bilibili/小红书 分享链接>\n"
                   + "2. 直接发送抖音/Bilibili/小红书链接可自动解析\n"
                   + $"3. {BilibiliLoginCommand}：Bilibili 扫码登录并保存 Cookie\n"
                   + $"4. {XiaohongshuLoginCommand}：小红书扫码登录并保存 Cookie\n"
                   + $"5. {DouyinCookieCheckCommand} / {BilibiliCookieCheckCommand} / {XiaohongshuCookieCheckCommand}：检查 Cookie 有效性\n\n"
                   + "Cookie 文件：插件目录/cookies/douyin.txt、cookies/bilibili.txt、cookies/xiaohongshu.txt\n"
                   + "Bilibili 说明：需要登录态；视频/音频流会分别下载，并用本地 ffmpeg 合并后发送。\n"
                   + "小红书说明：需要自行搭建 xhshow sign 服务，并在运行时配置 sign URL/token。";
        return Context.Message.ReplyAsync(message, help);
    }

    private bool IsParseCommand(MessageEvent message)
    {
        var text = GetPlainText(message).TrimStart();
        return !string.IsNullOrWhiteSpace(_config.ParseCommandPrefix)
               && text.StartsWith(_config.ParseCommandPrefix, StringComparison.OrdinalIgnoreCase);
    }

    private bool ShouldAutoParse(MessageEvent message)
    {
        if (TryBuildBilibiliPageLinkFromReply(message, out _))
        {
            return _config.AutoParseBilibiliLinks;
        }

        var text = GetPlainText(message);
        if (!string.IsNullOrWhiteSpace(text))
        {
            var trimmed = text.TrimStart();
            if (trimmed.StartsWith(_config.ParseCommandPrefix, StringComparison.OrdinalIgnoreCase)
                || trimmed.StartsWith("#parser", StringComparison.OrdinalIgnoreCase)
                || IsPluginResultMessage(trimmed)
                || IsBilibiliPageTemplateLink(trimmed)
                || trimmed.StartsWith(BilibiliLoginCommand, StringComparison.OrdinalIgnoreCase)
                || trimmed.StartsWith(XiaohongshuLoginCommand, StringComparison.OrdinalIgnoreCase)
                || trimmed.StartsWith(DouyinCookieCheckCommand, StringComparison.OrdinalIgnoreCase)
                || trimmed.StartsWith(BilibiliCookieCheckCommand, StringComparison.OrdinalIgnoreCase)
                || trimmed.StartsWith(XiaohongshuCookieCheckCommand, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        var parseText = GetStrictAutoParseText(message);
        var provider = string.IsNullOrWhiteSpace(parseText) ? null : _providerRegistry?.FindProvider(parseText);
        if (provider is null && _providerRegistry?.FindProvider(message, out _) is { } fallbackProvider && !IsBilibiliProvider(fallbackProvider.Id))
        {
            provider = fallbackProvider;
        }
        return provider?.Id switch
        {
            "douyin" => _config.AutoParseDouyinLinks,
            "bilibili" => _config.AutoParseBilibiliLinks,
            "bilibili-article" => _config.AutoParseBilibiliLinks,
            "bilibili-bangumi" => _config.AutoParseBilibiliLinks,
            "bilibili-live" => _config.AutoParseBilibiliLinks,
            "xiaohongshu" => _config.AutoParseXiaohongshuLinks,
            _ => false,
        };
    }

    private Task HandleAutoParseAsync(MessageEvent message)
    {
        if (TryBuildBilibiliPageLinkFromReply(message, out var pageLink))
        {
            return DispatchParseAsync(message, pageLink, silentProviderMismatch: true);
        }

        var parseText = GetStrictAutoParseText(message);
        if (!string.IsNullOrWhiteSpace(parseText) && _providerRegistry?.FindProvider(parseText) is not null)
        {
            return DispatchParseAsync(message, parseText, silentProviderMismatch: true);
        }

        if (_providerRegistry?.FindProvider(message, out parseText) is { } fallbackProvider && !IsBilibiliProvider(fallbackProvider.Id) && !string.IsNullOrWhiteSpace(parseText))
        {
            return DispatchParseAsync(message, parseText, silentProviderMismatch: true);
        }

        return Task.CompletedTask;
    }

    private static bool IsBilibiliProvider(string providerId)
    {
        return providerId.StartsWith("bilibili", StringComparison.OrdinalIgnoreCase);
    }

    private static string? GetStrictAutoParseText(MessageEvent message)
    {
        var text = GetPlainText(message);
        var strict = BilibiliUrlParser.ExtractStrictBilibiliUrl(text);
        if (strict is not null)
        {
            return strict;
        }

        var standalone = BilibiliUrlParser.NormalizeStandaloneBilibiliId(text);
        if (standalone is not null)
        {
            return standalone;
        }

        return BilibiliLightAppUrlExtractor.ExtractParseText(message) is { } extracted
            ? BilibiliUrlParser.ExtractStrictBilibiliUrl(extracted)
            : null;
    }

    private Task HandleParseCommandAsync(MessageEvent message)
    {
        var text = GetPlainText(message);
        var content = text.Length <= _config.ParseCommandPrefix.Length
            ? string.Empty
            : text[_config.ParseCommandPrefix.Length..].Trim();

        return DispatchParseAsync(message, string.IsNullOrWhiteSpace(content) ? text : content);
    }

    private async Task HandleBilibiliLoginAsync(MessageEvent message)
    {
        if (!await EnsurePrivateAdminCommandAsync(message, BilibiliLoginCommand))
        {
            return;
        }

        if (_bilibiliMessageHandler is not null)
        {
            await _bilibiliMessageHandler.HandleLoginAsync(message);
        }
    }

    private async Task HandleXiaohongshuLoginAsync(MessageEvent message)
    {
        if (!await EnsurePrivateAdminCommandAsync(message, XiaohongshuLoginCommand))
        {
            return;
        }

        if (_xiaohongshuMessageHandler is not null)
        {
            await _xiaohongshuMessageHandler.HandleLoginAsync(message);
        }
    }

    private async Task HandleDouyinCookieCheckAsync(MessageEvent message)
    {
        if (!await EnsurePrivateAdminCommandAsync(message, DouyinCookieCheckCommand))
        {
            return;
        }

        if (_douyinProvider is null)
        {
            await Context.Message.ReplyAsync(message, "Douyin 解析器尚未初始化。");
            return;
        }

        try
        {
            var status = await _douyinProvider.Parser.CheckLoginStatusAsync();
            await Context.Message.ReplyAsync(message, "DouyinCookie 状态：" + status);
        }
        catch (Exception ex)
        {
            BotLog.Warning($"MyParser DouyinCookie 手动检查失败：{ex}");
            await Context.Message.ReplyAsync(message, "DouyinCookie 状态检查失败：" + ex.Message);
        }
    }

    private async Task HandleBilibiliCookieCheckAsync(MessageEvent message)
    {
        if (!await EnsurePrivateAdminCommandAsync(message, BilibiliCookieCheckCommand))
        {
            return;
        }

        if (_bilibiliProvider is null)
        {
            await Context.Message.ReplyAsync(message, "Bilibili 解析器尚未初始化。");
            return;
        }

        try
        {
            var status = await _bilibiliProvider.Parser.CheckLoginStatusAsync();
            var detail = status.IsLogin
                ? $"有效 / 已登录：{status.UserName ?? "未知用户"} (mid={status.Mid})"
                : "无效 / 未登录：" + status.Message;
            await Context.Message.ReplyAsync(message, "BilibiliCookie 状态：" + detail);
        }
        catch (Exception ex)
        {
            BotLog.Warning($"MyParser BilibiliCookie 手动检查失败：{ex}");
            await Context.Message.ReplyAsync(message, "BilibiliCookie 状态检查失败：" + ex.Message);
        }
    }

    private async Task HandleXiaohongshuCookieCheckAsync(MessageEvent message)
    {
        if (!await EnsurePrivateAdminCommandAsync(message, XiaohongshuCookieCheckCommand))
        {
            return;
        }

        if (_xiaohongshuProvider is null)
        {
            await Context.Message.ReplyAsync(message, "小红书解析器尚未初始化。");
            return;
        }

        try
        {
            var status = await _xiaohongshuProvider.Parser.CheckLoginStatusAsync();
            var detail = status.IsLogin
                ? $"有效 / 已登录：{status.UserName ?? "未知用户"}" + (string.IsNullOrWhiteSpace(status.UserId) ? string.Empty : $" ({status.UserId})")
                : status.NeedVerify ? "触发安全验证：" + status.Message : "无效 / 未登录：" + status.Message;
            await Context.Message.ReplyAsync(message, "XiaohongshuCookie 状态：" + detail);
        }
        catch (Exception ex)
        {
            BotLog.Warning($"MyParser XiaohongshuCookie 手动检查失败：{ex}");
            await Context.Message.ReplyAsync(message, "XiaohongshuCookie 状态检查失败：" + ex.Message);
        }
    }

    private async Task<bool> EnsurePrivateAdminCommandAsync(MessageEvent message, string command)
    {
        switch (message.Channel.Type)
        {
            case ChannelType.Direct when Context.IsAdmin(message.Sender.Id):
                return true;
            case ChannelType.Direct:
                await Context.Message.ReplyAsync(message, $"{command} 仅允许机器人 Owner/Admin 私信使用。");
                return false;
            case ChannelType.Group:
                await Context.Message.ReplyAsync(message, $"{command} 涉及账号登录凭据，仅允许机器人 Owner/Admin 私信机器人使用，请不要在群内触发。");
                return false;
            case ChannelType.Other:
                await Context.Message.ReplyAsync(message, $"{command} 仅允许机器人 Owner/Admin 私信使用，不支持临时会话。");
                return false;
            default:
                await Context.Message.ReplyAsync(message, $"{command} 仅允许机器人 Owner/Admin 私信使用。");
                return false;
        }
    }

    private static bool TryBuildBilibiliPageLinkFromReply(MessageEvent message, out string pageLink)
    {
        pageLink = string.Empty;
        var text = GetPlainText(message).Trim();
        if (!int.TryParse(text, out var page) || page <= 0)
        {
            return false;
        }

        // 新 SDK 的 QuoteSegment 不携带被回复消息内容；QQ 平台从 Raw 原始消息里的
        // QqReplyIncoming 拿被回复消息的文本段（保持旧 GetReply() 的同步语义）。
        var reply = (message.Raw as ShiroBot.Qq.Model.QqIncomingMessage)?
            .Segments.OfType<ShiroBot.Qq.Model.QqReplyIncoming>().FirstOrDefault();
        if (reply is null)
        {
            return false;
        }

        var repliedText = string.Concat(reply.Segments.OfType<ShiroBot.Qq.Model.QqTextIncoming>().Select(i => i.Text)).Trim();
        if (!IsBilibiliPageTemplateLink(repliedText))
        {
            return false;
        }

        pageLink = repliedText + page;
        return true;
    }

    private static bool IsBilibiliPageTemplateLink(string text)
    {
        return System.Text.RegularExpressions.Regex.IsMatch(
            text.Trim(),
            @"^https?://www\.bilibili\.com/video/BV[0-9A-Za-z]{10}/?\?p=\s*$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }

    private static bool IsPluginResultMessage(string text)
    {
        return text.StartsWith("Bilibili 视频解析", StringComparison.OrdinalIgnoreCase)
               || text.StartsWith("Bilibili 直播解析", StringComparison.OrdinalIgnoreCase)
               || text.StartsWith("Bilibili 图文解析", StringComparison.OrdinalIgnoreCase)
               || text.StartsWith("Bilibili 专栏解析", StringComparison.OrdinalIgnoreCase)
               || text.StartsWith("Douyin 解析", StringComparison.OrdinalIgnoreCase)
               || text.StartsWith("抖音解析", StringComparison.OrdinalIgnoreCase)
               || text.StartsWith("小红书解析", StringComparison.OrdinalIgnoreCase)
               || text.StartsWith("Xiaohongshu", StringComparison.OrdinalIgnoreCase);
    }

    private Task DispatchParseAsync(MessageEvent message, string text, bool silentProviderMismatch = false)
    {
        text = BilibiliUrlParser.NormalizeStandaloneBilibiliId(text) ?? text;
        if (IsBilibiliPageTemplateLink(text))
        {
            return Task.CompletedTask;
        }

        var provider = _providerRegistry?.FindProvider(text);
        return provider?.Id switch
        {
            "douyin" => _douyinMessageHandler?.ParseAndReplyAsync(message, text) ?? Task.CompletedTask,
            "bilibili" => _bilibiliMessageHandler?.ParseAndReplyAsync(message, text, silentProviderMismatch) ?? Task.CompletedTask,
            "bilibili-article" => _bilibiliMessageHandler?.ParseAndReplyAsync(message, text, silentProviderMismatch) ?? Task.CompletedTask,
            "bilibili-bangumi" => _bilibiliMessageHandler?.ParseAndReplyAsync(message, text, silentProviderMismatch) ?? Task.CompletedTask,
            "bilibili-live" => _bilibiliMessageHandler?.ParseAndReplyAsync(message, text, silentProviderMismatch) ?? Task.CompletedTask,
            "xiaohongshu" => _xiaohongshuMessageHandler?.ParseAndReplyAsync(message, text) ?? Task.CompletedTask,
            _ => Context.Message.ReplyAsync(message, "未找到可处理该链接的解析提供商。"),
        };
    }

    private static string GetPlainText(MessageEvent message) => message.GetPlainText();
}

