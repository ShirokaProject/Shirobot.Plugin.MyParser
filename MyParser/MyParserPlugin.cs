using System.Reflection;
using System.Text;
using Shirobot.Plugin.MyParser.MessageHandling;
using Shirobot.Plugin.MyParser.Parsing;
using Shirobot.Plugin.MyParser.Utility;
using ShiroBot.Model.Common;
using ShiroBot.SDK.Abstractions;
using ShiroBot.SDK.Core;
using ShiroBot.SDK.Plugin;

namespace Shirobot.Plugin.MyParser;

[BotPlugin(id: "MyParser2",
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

    private readonly Lock _reloadLock = new();
    private PluginConfig _config = new();
    private FileSystemWatcher? _configWatcher;
    private FileSystemWatcher? _cookieWatcher;
    private CancellationTokenSource? _configReloadDebounce;
    private CancellationTokenSource? _cookieReloadDebounce;
    private readonly List<IParseProvider> _providers = [];
    private readonly List<IDisposable> _providerDisposables = [];
    private readonly List<ProviderCookieDescriptor> _providerCookieDescriptors = [];
    private readonly List<IProviderTextNormalizer> _providerTextNormalizers = [];
    private readonly List<IIncomingProviderTextNormalizer> _incomingProviderTextNormalizers = [];
    private readonly List<IProviderAutoParsePolicy> _providerAutoParsePolicies = [];
    private readonly List<IProviderResultMessageClassifier> _providerResultMessageClassifiers = [];
    private readonly List<IProviderReplyParseTextBuilder> _providerReplyParseTextBuilders = [];
    private readonly List<IProviderCommandContributor> _providerCommandContributors = [];
    private readonly Dictionary<string, string> _providerModuleIds = new(StringComparer.OrdinalIgnoreCase);
    private ParseProviderRegistry? _providerRegistry;
    private ProviderHostServices? _hostServices;
    private readonly Dictionary<string, IProviderMessageHandler> _providerMessageHandlers = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<IProviderRuntimeModule> _providerRuntimeModules = [];
    private readonly List<ProviderCommandDescriptor> _providerCommandDescriptors = [];

    public override string Name => "MyParser";

    protected override async Task LoadAsync()
    {
        MyParserRuntime.ResetForLoad();
        ProviderMessageUtilities.ClearReactionCache();
        _config = Context.Config.Load<PluginConfig>();
        _hostServices = new ProviderHostServices(Context);
        var modules = DiscoverProviderModules();
        RegisterProviderModuleCapabilities(modules);
        NormalizeRuntimeDirectories();
        LoadProviderCookiesFromPluginDirectory();

        foreach (var module in modules)
        {
            foreach (var provider in module.CreateProviders(_config))
            {
                _providerModuleIds[provider.Id] = module.Id;
                _providers.Add(provider);
                if (provider is IDisposable disposable)
                {
                    _providerDisposables.Add(disposable);
                }
            }
        }

        var orderedProviders = _providers
            .OrderBy(GetProviderOrder)
            .ThenBy(provider => provider.Id, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        foreach (var runtimeModule in modules.OfType<IProviderRuntimeModule>())
        {
            _providerRuntimeModules.Add(runtimeModule);
            runtimeModule.LoadRuntime(new ProviderRuntimeContext(Context, _config, _hostServices));
        }

        _providerRegistry = new ParseProviderRegistry(orderedProviders);
        CreateProviderMessageHandlers(modules, orderedProviders);

        LogLoadedProviderCapabilities(modules, orderedProviders);

        await LogProviderCookieLoginStatusesAsync(orderedProviders);

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
        FriendCommands.MapExact("#parser", HandleHelpAsync);
        GroupCommands.MapExact("#parser", HandleHelpAsync);
        RegisterProviderCommands(modules, orderedProviders);

        FriendCommands.MapWhen(IsParseCommand, HandleParseCommandAsync);
        GroupCommands.MapWhen(IsParseCommand, HandleParseCommandAsync);

        FriendCommands.MapWhen(ShouldAutoParse, HandleAutoParseAsync);
        GroupCommands.MapWhen(ShouldAutoParse, HandleAutoParseAsync);

        StartHotReloadWatchers();
        BotLog.Info($"MyParser 已加载：provider 自动注册完成。命令：#parser / {_config.ParseCommandPrefix} <链接>");
    }

    private static IMyParserProviderModule[] DiscoverProviderModules()
    {
        var modules = new List<IMyParserProviderModule>();
        var assembly = typeof(MyParserPlugin).Assembly;
        foreach (var type in assembly.GetTypes()
                     .Where(type => !type.IsAbstract
                                    && type.GetCustomAttribute<MyParserProviderAttribute>() is not null
                                    && typeof(IMyParserProviderModule).IsAssignableFrom(type))
                     .OrderBy(type => type.GetCustomAttribute<MyParserProviderAttribute>()!.Id, StringComparer.OrdinalIgnoreCase))
        {
            var attribute = type.GetCustomAttribute<MyParserProviderAttribute>()!;
            BotLog.Info($"MyParser 发现 provider module 类型：id={attribute.Id}, type={type.FullName}");
            modules.Add((IMyParserProviderModule)Activator.CreateInstance(type)!);
        }

        return modules.ToArray();
    }

    private void RegisterProviderModuleCapabilities(IEnumerable<IMyParserProviderModule> modules)
    {
        _providerCookieDescriptors.Clear();
        _providerModuleIds.Clear();
        _providerTextNormalizers.Clear();
        _incomingProviderTextNormalizers.Clear();
        _providerAutoParsePolicies.Clear();
        _providerResultMessageClassifiers.Clear();
        _providerReplyParseTextBuilders.Clear();
        _providerCommandContributors.Clear();

        foreach (var module in modules)
        {
            if (module is IProviderCookieStore cookieStore)
            {
                _providerCookieDescriptors.AddRange(cookieStore.CookieDescriptors);
            }

            if (module is IProviderTextNormalizer textNormalizer)
            {
                _providerTextNormalizers.Add(textNormalizer);
            }

            if (module is IIncomingProviderTextNormalizer incomingNormalizer)
            {
                _incomingProviderTextNormalizers.Add(incomingNormalizer);
            }

            if (module is IProviderAutoParsePolicy autoParsePolicy)
            {
                _providerAutoParsePolicies.Add(autoParsePolicy);
            }

            if (module is IProviderResultMessageClassifier resultClassifier)
            {
                _providerResultMessageClassifiers.Add(resultClassifier);
            }

            if (module is IProviderReplyParseTextBuilder replyParseTextBuilder)
            {
                _providerReplyParseTextBuilders.Add(replyParseTextBuilder);
            }

            if (module is IProviderCommandContributor commandContributor)
            {
                _providerCommandContributors.Add(commandContributor);
            }
        }
    }

    private void LogLoadedProviderCapabilities(IReadOnlyCollection<IMyParserProviderModule> modules, IReadOnlyCollection<IParseProvider> providers)
    {
        if (modules.Count == 0)
        {
            BotLog.Warning("MyParser 未发现任何 provider module。请检查 provider 源码是否已通过 MyParserProviderAttribute 注册并编译进主插件。");
            return;
        }

        BotLog.Info($"MyParser provider 能力概览：modules={modules.Count}, providers={providers.Count}");

        foreach (var module in modules.OrderBy(module => module.Id, StringComparer.OrdinalIgnoreCase))
        {
            var moduleProviders = providers
                .Where(provider => string.Equals(GetModuleId(provider.Id), module.Id, StringComparison.OrdinalIgnoreCase))
                .OrderBy(GetProviderOrder)
                .ThenBy(provider => provider.Id, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            var moduleCapabilities = GetModuleCapabilities(module);
            BotLog.Info($"MyParser provider module：id={module.Id}, type={module.GetType().FullName}, capabilities=[{string.Join(", ", moduleCapabilities)}]");

            if (moduleProviders.Length == 0)
            {
                BotLog.Warning($"MyParser provider module 未注册解析器：id={module.Id}");
                continue;
            }

            foreach (var provider in moduleProviders)
            {
                var providerCapabilities = GetProviderCapabilities(provider);
                BotLog.Info($"MyParser provider：id={provider.Id}, name={provider.Name}, type={provider.GetType().FullName}, capabilities=[{string.Join(", ", providerCapabilities)}]");
            }
        }
    }

    private static IReadOnlyList<string> GetModuleCapabilities(IMyParserProviderModule module)
    {
        var capabilities = new List<string> { "url-parse" };

        if (module is IProviderMessageHandlerFactory)
        {
            capabilities.Add("message-handler");
        }

        if (module is IProviderTextNormalizer)
        {
            capabilities.Add("text-normalizer");
        }

        if (module is IIncomingProviderTextNormalizer)
        {
            capabilities.Add("incoming-normalizer");
        }

        if (module is ICookieValidator)
        {
            capabilities.Add("cookie-validator");
        }

        if (module is IProviderRuntimeModule)
        {
            capabilities.Add("runtime-module");
        }

        if (module is IProviderReplyParseTextBuilder)
        {
            capabilities.Add("reply-parse-text-builder");
        }

        return capabilities;
    }

    private static IReadOnlyList<string> GetProviderCapabilities(IParseProvider provider)
    {
        var capabilities = new List<string> { "url-recognition", "content-parse" };

        if (provider is IIncomingMessageParseProvider)
        {
            capabilities.Add("incoming-message-extract");
        }

        if (provider is IProviderLoginStatusProvider)
        {
            capabilities.Add("login-status");
        }

        if (provider is IQrLoginProvider)
        {
            capabilities.Add("qr-login");
        }

        if (provider is IParserHttpClientAccessor)
        {
            capabilities.Add("http-client");
        }

        if (provider is IParseProviderWithParser { ParserObject: IParserHttpClientAccessor })
        {
            capabilities.Add("parser-http-client");
        }

        if (provider is IVideoDownloadGate || provider is IParseProviderWithParser { ParserObject: IVideoDownloadGate })
        {
            capabilities.Add("video-download-gate");
        }

        if (provider is IDisposable)
        {
            capabilities.Add("disposable");
        }

        return capabilities;
    }

    private void CreateProviderMessageHandlers(IEnumerable<IMyParserProviderModule> modules, IEnumerable<IParseProvider> providers)
    {
        if (_providerRegistry is null || _hostServices is null)
        {
            return;
        }

        foreach (var provider in providers)
        {
            var module = modules
                .OfType<IProviderMessageHandlerFactory>()
                .FirstOrDefault(i => string.Equals(((IMyParserProviderModule)i).Id, GetModuleId(provider.Id), StringComparison.OrdinalIgnoreCase));
            if (module is null)
            {
                continue;
            }

            var handler = module.CreateMessageHandler(new ProviderMessageHandlerContext(Context, _config, _providerRegistry, provider, _hostServices));
            if (handler is null)
            {
                continue;
            }

            _providerMessageHandlers[provider.Id] = handler;
        }
    }

    private IProviderMessageHandler? TryGetProviderMessageHandler(string providerId)
    {
        return _providerMessageHandlers.TryGetValue(providerId, out var handler)
            ? handler
            : _providerMessageHandlers.TryGetValue(GetModuleId(providerId), out handler)
                ? handler
                : null;
    }

    private string GetModuleId(string providerId)
    {
        if (_providerModuleIds.TryGetValue(providerId, out var moduleId))
        {
            return moduleId;
        }

        foreach (var runtimeModule in _providerRuntimeModules)
        {
            if (runtimeModule.ProviderIds.Any(id => string.Equals(id, providerId, StringComparison.OrdinalIgnoreCase)))
            {
                return runtimeModule.ProviderIds.FirstOrDefault() ?? providerId;
            }
        }

        return providerId;
    }

    private static int GetProviderOrder(IParseProvider provider)
    {
        return provider is IProviderPriority priorityProvider ? priorityProvider.Priority : 100;
    }

    private async Task LogProviderCookieLoginStatusesAsync(IEnumerable<IParseProvider> providers)
    {
        foreach (var provider in providers.OfType<IProviderLoginStatusProvider>())
        {
            var parseProvider = (IParseProvider)provider;
            await LogProviderCookieLoginStatusAsync(parseProvider, parseProvider.Name);
        }
    }

    private static async Task LogProviderCookieLoginStatusAsync(IParseProvider? provider, string platformName)
    {
        if (provider is not IProviderLoginStatusProvider loginStatusProvider)
        {
            return;
        }

        try
        {
            var status = await loginStatusProvider.CheckLoginStatusAsync();
            BotLog.Info($"MyParser {platformName}Cookie 登录状态：{status.Message}");
        }
        catch (Exception ex)
        {
            BotLog.Warning($"MyParser {platformName}Cookie 登录状态检查失败：{ex.GetType().Name}: {ex.Message}");
        }
    }

    private void LoadProviderCookiesFromPluginDirectory()
    {
        foreach (var descriptor in _providerCookieDescriptors)
        {
            LoadProviderCookieFromPluginDirectory(descriptor);
        }
    }

    private void LoadProviderCookieFromPluginDirectory(ProviderCookieDescriptor descriptor)
    {
        var cookiePath = ResolveCookiePath(descriptor.FileName);

        if (!File.Exists(cookiePath))
        {
            descriptor.ApplyCookie(string.Empty);
            if (descriptor.CreateIfMissing)
            {
                File.WriteAllText(cookiePath, string.Empty, Encoding.UTF8);
                BotLog.Info($"MyParser 已创建 {descriptor.DisplayName} Cookie 文件：{cookiePath}");
            }
            return;
        }

        var cookie = File.ReadAllText(cookiePath, Encoding.UTF8).Trim().TrimStart('\ufeff');
        if (string.IsNullOrWhiteSpace(cookie))
        {
            descriptor.ApplyCookie(string.Empty);
            BotLog.Info($"MyParser {descriptor.DisplayName}Cookie 为空；{descriptor.EmptyHint ?? $"可编辑文件后重启：{cookiePath}"}");
            return;
        }

        if (descriptor.ValidateCookie is not null && !descriptor.ValidateCookie(cookie))
        {
            descriptor.ApplyCookie(string.Empty);
            BotLog.Warning($"MyParser 忽略无效 {descriptor.DisplayName}Cookie 文件：{cookiePath}。{descriptor.InvalidHint ?? "请检查 Cookie 内容。"}");
            return;
        }

        descriptor.ApplyCookie(cookie);
        BotLog.Info($"MyParser 已从插件目录读取 {descriptor.DisplayName}Cookie：{cookiePath}");
    }

    private void NormalizeRuntimeDirectories()
    {
        var pluginDir = GetPluginDirectory();
        Directory.CreateDirectory(pluginDir);

        MyParserRuntime.DouyinCookie = string.Empty;
        MyParserRuntime.BilibiliCookie = string.Empty;
        MyParserRuntime.XiaohongshuCookie = string.Empty;
        MyParserRuntime.NetEaseCloudMusicCookie = string.Empty;

        MyParserRuntime.DownloadDirectory = Path.Combine(pluginDir, "tmp", "douyin");
        MyParserRuntime.BilibiliDownloadDirectory = Path.Combine(pluginDir, "tmp", "bilibili");
        MyParserRuntime.XiaohongshuDownloadDirectory = Path.Combine(pluginDir, "tmp", "xiaohongshu");
        MyParserRuntime.WeixinChannelsDownloadDirectory = Path.Combine(pluginDir, "tmp", "weixinchannels");

        Directory.CreateDirectory(Path.Combine(pluginDir, CookieDirectoryName));
        Directory.CreateDirectory(MyParserRuntime.DownloadDirectory);
        Directory.CreateDirectory(MyParserRuntime.BilibiliDownloadDirectory);
        Directory.CreateDirectory(MyParserRuntime.XiaohongshuDownloadDirectory);
        Directory.CreateDirectory(MyParserRuntime.WeixinChannelsDownloadDirectory);
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
        LoadProviderCookiesFromPluginDirectory();
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

    protected override Task OnUnloadAsync()
    {
        MyParserRuntime.BeginUnload();
        ProviderMessageUtilities.ClearReactionCache();
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

        _hostServices?.Dispose();
        _hostServices = null;

        foreach (var handler in _providerMessageHandlers.Values.Distinct())
        {
            handler.Dispose();
        }

        _providerMessageHandlers.Clear();
        foreach (var disposable in _providerDisposables)
        {
            disposable.Dispose();
        }

        _providerDisposables.Clear();
        _providers.Clear();
        _providerRegistry = null;
        _providerRuntimeModules.Clear();
        _providerCommandDescriptors.Clear();
        _providerCookieDescriptors.Clear();
        _providerTextNormalizers.Clear();
        _incomingProviderTextNormalizers.Clear();
        _providerAutoParsePolicies.Clear();
        _providerResultMessageClassifiers.Clear();
        _providerReplyParseTextBuilders.Clear();
        _providerCommandContributors.Clear();
        _providerModuleIds.Clear();
        BotLog.Info("MyParser 已卸载。");
        return Task.CompletedTask;
    }

    private Task HandleHelpAsync(IncomingMessage message)
    {
        var modules = _providerModuleIds.Values.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        var commands = _providerCommandDescriptors.Select(i => i.Command).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        var cookies = _providerCookieDescriptors.Select(i => $"cookies/{i.FileName}").Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        var runtimeHelp = _providerRuntimeModules
            .Select(module => module.GetHelpText(_config))
            .Where(i => !string.IsNullOrWhiteSpace(i))
            .ToArray();
        var help = new StringBuilder();
        help.AppendLine("MyParser");
        help.AppendLine($"已加载 provider：{(modules.Length == 0 ? "无" : string.Join(" / ", modules))}");
        help.AppendLine();
        help.AppendLine("用法：");
        help.AppendLine($"1. {_config.ParseCommandPrefix} <分享链接>");
        help.AppendLine("2. 直接发送 provider 支持的链接可自动解析");
        if (commands.Length > 0)
        {
            help.AppendLine($"3. provider 命令：{string.Join(" / ", commands)}");
        }

        if (cookies.Length > 0)
        {
            help.AppendLine();
            help.AppendLine("Cookie 文件：" + string.Join("、", cookies));
        }

        foreach (var item in runtimeHelp)
        {
            help.AppendLine();
            help.AppendLine(item);
        }

        return Context.Message.ReplyAsync(message, help.ToString().TrimEnd());
    }

    private bool IsParseCommand(IncomingMessage message)
    {
        return TryGetParseCommandContent(GetPlainText(message), out _);
    }

    private bool ShouldAutoParse(IncomingMessage message)
    {
        if (TryBuildProviderReplyParseText(message, out var replyParseText))
        {
            return IsAutoParseEnabledForProvider(_providerRegistry?.FindProvider(replyParseText, isAutoParse: false, out _));
        }

        var text = GetPlainText(message);
        if (!string.IsNullOrWhiteSpace(text))
        {
            var trimmed = text.TrimStart();
            if (TryGetParseCommandContent(trimmed, out _)
                || IsPluginResultMessage(trimmed)
                || IsDeferredProviderParseText(trimmed)
                || _providerCommandDescriptors.Any(i => IsProviderCommand(trimmed, i)))
            {
                return false;
            }
        }

        var parseText = GetStrictAutoParseText(message);
        var provider = string.IsNullOrWhiteSpace(parseText) ? null : _providerRegistry?.FindProvider(parseText, isAutoParse: true, out _);
        if (provider is null && _providerRegistry?.FindProvider(message, out _) is { } fallbackProvider && !HasIncomingProviderNormalizer(fallbackProvider.Id))
        {
            provider = fallbackProvider;
        }

        return IsAutoParseEnabledForProvider(provider);
    }

    private Task HandleAutoParseAsync(IncomingMessage message)
    {
        if (TryBuildProviderReplyParseText(message, out var replyParseText))
        {
            return DispatchParseAsync(message, replyParseText, silentProviderMismatch: true, isAutoParse: false);
        }

        var parseText = GetStrictAutoParseText(message);
        if (!string.IsNullOrWhiteSpace(parseText) && _providerRegistry?.FindProvider(parseText, isAutoParse: true, out parseText) is not null)
        {
            return DispatchParseAsync(message, parseText, silentProviderMismatch: true, isAutoParse: true);
        }

        if (_providerRegistry?.FindProvider(message, out parseText) is { } fallbackProvider && !HasIncomingProviderNormalizer(fallbackProvider.Id) && !string.IsNullOrWhiteSpace(parseText))
        {
            return DispatchParseAsync(message, parseText, silentProviderMismatch: true, isAutoParse: true);
        }

        return Task.CompletedTask;
    }

    private string? GetStrictAutoParseText(IncomingMessage message)
    {
        foreach (var normalizer in _incomingProviderTextNormalizers)
        {
            var normalized = normalizer.NormalizeParseText(message);
            if (!string.IsNullOrWhiteSpace(normalized))
            {
                return normalized;
            }
        }

        return null;
    }

    private bool TryBuildProviderReplyParseText(IncomingMessage message, out string parseText)
    {
        foreach (var builder in _providerReplyParseTextBuilders)
        {
            parseText = builder.TryBuildParseText(message) ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(parseText))
            {
                return true;
            }
        }

        parseText = string.Empty;
        return false;
    }

    private bool IsDeferredProviderParseText(string text)
    {
        return _providerReplyParseTextBuilders.Any(builder => builder.IsDeferredParseText(text));
    }

    private bool HasIncomingProviderNormalizer(string providerId)
    {
        var moduleId = GetModuleId(providerId);
        return _incomingProviderTextNormalizers.Any(normalizer =>
            normalizer is IMyParserProviderModule module
            && string.Equals(module.Id, moduleId, StringComparison.OrdinalIgnoreCase));
    }

    private bool IsAutoParseEnabledForProvider(IParseProvider? provider)
    {
        if (provider is null)
        {
            return false;
        }

        var moduleId = GetModuleId(provider.Id);
        var policy = _providerAutoParsePolicies.FirstOrDefault(item =>
            item is IMyParserProviderModule module
            && string.Equals(module.Id, moduleId, StringComparison.OrdinalIgnoreCase));
        return policy?.IsAutoParseEnabled(_config) ?? false;
    }

    private Task HandleParseCommandAsync(IncomingMessage message)
    {
        var text = GetPlainText(message);
        var content = TryGetParseCommandContent(text, out var parsedContent) ? parsedContent : string.Empty;

        return DispatchParseAsync(message, string.IsNullOrWhiteSpace(content) ? text : content);
    }

    private bool TryGetParseCommandContent(string text, out string content)
    {
        var trimmed = text.TrimStart();
        return TryStripParseCommandPrefix(trimmed, _config.ParseCommandPrefix, requireContent: false, out content)
               || TryStripParseCommandPrefix(trimmed, "#parser", requireContent: true, out content);
    }

    private static bool TryStripParseCommandPrefix(string text, string prefix, bool requireContent, out string content)
    {
        content = string.Empty;
        if (string.IsNullOrWhiteSpace(prefix) || !text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (text.Length == prefix.Length)
        {
            return !requireContent;
        }

        if (!char.IsWhiteSpace(text[prefix.Length]))
        {
            return false;
        }

        content = text[prefix.Length..].Trim();
        return !requireContent || !string.IsNullOrWhiteSpace(content);
    }

    private void RegisterProviderCommands(IReadOnlyCollection<IMyParserProviderModule> modules, IReadOnlyList<IParseProvider> orderedProviders)
    {
        if (_hostServices is null)
        {
            return;
        }

        foreach (var module in modules)
        {
            if (module is not IProviderCommandContributor commandContributor)
            {
                continue;
            }

            var primaryProvider = orderedProviders.FirstOrDefault(provider => string.Equals(GetModuleId(provider.Id), module.Id, StringComparison.OrdinalIgnoreCase));
            var primaryHandler = primaryProvider is null ? null : TryGetProviderMessageHandler(primaryProvider.Id);
            AddProviderCommands(commandContributor.CreateCommands(new ProviderCommandContext(Context, _config, _hostServices, primaryProvider, primaryHandler)));
        }

        foreach (var runtimeModule in _providerRuntimeModules)
        {
            var primaryProvider = orderedProviders.FirstOrDefault(provider => runtimeModule.ProviderIds.Any(id => string.Equals(id, provider.Id, StringComparison.OrdinalIgnoreCase)));
            var primaryHandler = primaryProvider is null ? null : TryGetProviderMessageHandler(primaryProvider.Id);
            AddProviderCommands(runtimeModule.CreateCommands(new ProviderCommandContext(Context, _config, _hostServices, primaryProvider, primaryHandler)));
        }
    }

    private void AddProviderCommands(IEnumerable<ProviderCommandDescriptor> descriptors)
    {
        foreach (var descriptor in descriptors)
        {
            if (_providerCommandDescriptors.Any(i => string.Equals(i.Command, descriptor.Command, StringComparison.OrdinalIgnoreCase)))
            {
                BotLog.Warning($"MyParser provider command 重复，已忽略：{descriptor.Command}");
                continue;
            }

            _providerCommandDescriptors.Add(descriptor);
            FriendCommands.MapWhen(message => IsProviderCommand(message, descriptor), message => HandleProviderCommandAsync(message, descriptor));
            GroupCommands.MapWhen(message => IsProviderCommand(message, descriptor), message => HandleProviderCommandAsync(message, descriptor));
        }
    }

    private bool IsProviderCommand(IncomingMessage message, ProviderCommandDescriptor descriptor)
    {
        return IsProviderCommand(GetPlainText(message).TrimStart(), descriptor);
    }

    private static bool IsProviderCommand(string text, ProviderCommandDescriptor descriptor)
    {
        if (!text.StartsWith(descriptor.Command, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return text.Length == descriptor.Command.Length || char.IsWhiteSpace(text[descriptor.Command.Length]);
    }

    private async Task HandleProviderCommandAsync(IncomingMessage message, ProviderCommandDescriptor descriptor)
    {
        if (descriptor.AdminOnly && !await EnsurePrivateAdminCommandAsync(message, descriptor.Command))
        {
            return;
        }

        await descriptor.HandleAsync(message);
    }

    private async Task<bool> EnsurePrivateAdminCommandAsync(IncomingMessage message, string command)
    {
        switch (message)
        {
            case FriendIncomingMessage friend when Context.IsAdmin(friend.SenderId):
                return true;
            case FriendIncomingMessage:
                await Context.Message.ReplyAsync(message, $"{command} 仅允许机器人 Owner/Admin 私信使用。");
                return false;
            case GroupIncomingMessage:
                await Context.Message.ReplyAsync(message, $"{command} 涉及账号登录凭据，仅允许机器人 Owner/Admin 私信机器人使用，请不要在群内触发。");
                return false;
            case TempIncomingMessage:
                await Context.Message.ReplyAsync(message, $"{command} 仅允许机器人 Owner/Admin 私信使用，不支持临时会话。");
                return false;
            default:
                await Context.Message.ReplyAsync(message, $"{command} 仅允许机器人 Owner/Admin 私信使用。");
                return false;
        }
    }

    private bool IsPluginResultMessage(string text)
    {
        return _providerResultMessageClassifiers.Any(classifier => classifier.IsPluginResultMessage(text))
               || _providerRuntimeModules.Any(module => module.IsPluginResultMessage(text));
    }

    private Task DispatchParseAsync(IncomingMessage message, string text, bool silentProviderMismatch = false, bool isAutoParse = false)
    {
        text = NormalizeParseText(text);
        if (IsDeferredProviderParseText(text))
        {
            return Task.CompletedTask;
        }

        var parseText = text;
        var provider = _providerRegistry?.FindProvider(text, isAutoParse, out parseText);
        if (provider is null)
        {
            return Context.Message.ReplyAsync(message, "未找到可处理该链接的解析提供商。");
        }

        return TryGetProviderMessageHandler(provider.Id)?.ParseAndReplyAsync(message, parseText, silentProviderMismatch)
               ?? Context.Message.ReplyAsync(message, $"{provider.Name} 已识别，但该 provider 未接入消息发送流程。");
    }

    private string NormalizeParseText(string text)
    {
        foreach (var normalizer in _providerTextNormalizers)
        {
            text = normalizer.NormalizeParseText(text) ?? text;
        }

        return text;
    }

    private static string GetPlainText(IncomingMessage message) => message switch
    {
        FriendIncomingMessage friend => friend.GetPlainText(),
        GroupIncomingMessage group => group.GetPlainText(),
        TempIncomingMessage temp => string.Concat(temp.Segments.OfType<TextIncomingSegment>().Select(i => i.Text)),
        _ => string.Empty,
    };
}
