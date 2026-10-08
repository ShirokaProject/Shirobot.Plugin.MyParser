using ShiroBot.SDK.Config;
using Shirobot.Plugin.MyParser.Parsing;
using Shirobot.Plugin.MyParser.Services;
using ShiroBot.Model.QQ;
using ShiroBot.SDK.Models;
using ShiroBot.SDK.Abstractions;
using ShiroBot.SDK.Core;
using ShiroBot.SDK.Plugin;

[assembly: ShiroBotApiCompatibility("0.9.2", "0.9.2")]

namespace Shirobot.Plugin.MyParser;

[BotPlugin(id: "MyParser",
    Name = "MyParser",
    Version = "0.6.1",
    Author = "PVPGood",
    Category = PluginCategory.Utility,
    Description = "面向 Shirobot 的学习型内容消息处理插件。",
    GithubRepo = "ShirokaProject/Shirobot.Plugin.MyParser",
    IsPluginSingleFile = true,
    SharedAssemblies = "ShiroBot.Model.QQ")
]
public sealed class MyParserPlugin : PluginBase, IConfigurableComponent<PluginConfig>
{
    private PluginConfig _config = new();
    private ProviderHostServices? _hostServices;
    private ProviderCatalog? _providerCatalog;
    private ProviderSettingsMonitor? _settingsMonitor;
    private ParseTaskManager? _parseTaskManager;
    private MessageParseCoordinator? _messageCoordinator;

    public override string Name => "MyParser";

    PluginConfig IConfigurableComponent<PluginConfig>.CurrentConfigValue => _config;

    Task IConfigurableComponent<PluginConfig>.OnConfigLoadedAsync(PluginConfig config, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _config = config;
        return Task.CompletedTask;
    }

    Task IConfigurableComponent<PluginConfig>.OnConfigChangedAsync(
        PluginConfig previous, PluginConfig current, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        foreach (var property in typeof(PluginConfig).GetProperties())
            if (property.CanRead && property.CanWrite) property.SetValue(_config, property.GetValue(current));
        BotLog.Info("MyParser 配置已热重载。");
        return Task.CompletedTask;
    }

    protected override void ConfigureRoutes()
    {
        Events.Map<MessageDeletedEvent>(evt =>
            _parseTaskManager?.HandleMessageDeletedAsync(evt) ?? Task.CompletedTask);
        Events.MapPlatform(QEventKinds.GroupMute, evt =>
            _parseTaskManager?.HandleGroupMuteAsync(evt) ?? Task.CompletedTask);
        Events.MapPlatform(QEventKinds.GroupWholeMute, evt =>
            _parseTaskManager?.HandleGroupWholeMuteAsync(evt) ?? Task.CompletedTask);
    }

    protected override Task LoadAsync()
    {
        MyParserRuntime.ResetForLoad();
        ProviderReactionService.ClearCache();

        _hostServices = new ProviderHostServices(Context, _config);
        _settingsMonitor = new ProviderSettingsMonitor(Context, _config);
        _providerCatalog = new ProviderCatalog(Context, _config, _hostServices);

        _providerCatalog.DiscoverModules();
        _settingsMonitor.InitializeRuntimeDirectories();
        _settingsMonitor.LoadCookies(_providerCatalog.CookieDescriptors);
        _providerCatalog.CreateProviders();
        _providerCatalog.LoadRuntimeModules();
        _providerCatalog.CreateMessageHandlers();
        _providerCatalog.LogCapabilities();

        _parseTaskManager = new ParseTaskManager();
        _messageCoordinator = new MessageParseCoordinator(Context, _config, _providerCatalog, _parseTaskManager);
        foreach (var command in _providerCatalog.CreateCommands())
        {
            DirectCommands.MapWhen(
                message => _messageCoordinator.IsProviderCommand(message, command),
                message => _messageCoordinator.HandleProviderCommandAsync(message, command));
            GroupCommands.MapWhen(
                message => _messageCoordinator.IsProviderCommand(message, command),
                message => _messageCoordinator.HandleProviderCommandAsync(message, command));
        }

        DirectCommands.MapWhen(_messageCoordinator.ShouldAutoParse, _messageCoordinator.HandleAutoParseAsync);
        GroupCommands.MapWhen(_messageCoordinator.ShouldAutoParse, _messageCoordinator.HandleAutoParseAsync);

        _settingsMonitor.StartWatchers();
        BotLog.Info("MyParser 已加载：自动检测平台链接，搜索命令：#wyy <歌名/歌手>。");
        return Task.CompletedTask;
    }

    protected override async Task OnUnloadAsync()
    {
        _settingsMonitor?.Dispose();
        _settingsMonitor = null;

        if (_messageCoordinator is not null)
        {
            await _messageCoordinator.StopAsync().ConfigureAwait(false);
            _messageCoordinator = null;
        }
        else
        {
            MyParserRuntime.BeginUnload();
        }

        ProviderReactionService.ClearCache();
        _providerCatalog?.Dispose();
        _providerCatalog = null;
        _hostServices?.Dispose();
        _hostServices = null;
        BotLog.Info("MyParser 已卸载。");
    }
}
