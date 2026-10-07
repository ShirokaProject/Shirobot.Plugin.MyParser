using System.Reflection;
using ShiroBot.SDK.Models;
using ShiroBot.SDK.Plugin;
using Shirobot.Plugin.MyParser.MessageHandling;
using Shirobot.Plugin.MyParser.Parsing;

namespace Shirobot.Plugin.MyParser;

internal sealed class ProviderCatalog(IBotContext botContext, PluginConfig config, ProviderHostServices hostServices) : IDisposable
{
    private readonly List<IMyParserProviderModule> _modules = [];
    private readonly List<IParseProvider> _providers = [];
    private readonly List<IDisposable> _providerDisposables = [];
    private readonly List<ProviderCookieDescriptor> _cookieDescriptors = [];
    private readonly List<IProviderTextNormalizer> _textNormalizers = [];
    private readonly List<IIncomingProviderTextNormalizer> _incomingTextNormalizers = [];
    private readonly List<IProviderAutoParsePolicy> _autoParsePolicies = [];
    private readonly List<IProviderAvailabilityPolicy> _availabilityPolicies = [];
    private readonly List<IProviderResultMessageClassifier> _resultClassifiers = [];
    private readonly List<IProviderReplyParseTextBuilder> _replyTextBuilders = [];
    private readonly Dictionary<string, string> _moduleIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IProviderMessageHandler> _handlers = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<IProviderRuntimeModule> _runtimeModules = [];
    private ParseProviderRegistry? _registry;
    private IReadOnlyList<ProviderCommandDescriptor>? _commands;
    private bool _disposed;

    public IReadOnlyList<IMyParserProviderModule> Modules => _modules;
    public IReadOnlyList<IParseProvider> Providers => _providers;
    public IReadOnlyList<ProviderCookieDescriptor> CookieDescriptors => _cookieDescriptors;
    public ParseProviderRegistry Registry => _registry ?? throw new InvalidOperationException("Provider catalog is not initialized.");

    public void DiscoverModules()
    {
        _modules.Clear();
        _modules.AddRange(DiscoverProviderModules());
        RegisterModuleCapabilities();
    }

    public void CreateProviders()
    {
        _providers.Clear();
        _providerDisposables.Clear();
        _moduleIds.Clear();

        foreach (var module in _modules)
        {
            foreach (var provider in module.CreateProviders(config))
            {
                _moduleIds[provider.Id] = module.Id;
                _providers.Add(provider);
                if (provider is IDisposable disposable) _providerDisposables.Add(disposable);
            }
        }

        var ordered = _providers.OrderBy(GetProviderOrder)
            .ThenBy(provider => provider.Id, StringComparer.OrdinalIgnoreCase).ToArray();
        _providers.Clear();
        _providers.AddRange(ordered);
        _registry = new ParseProviderRegistry(_providers);
    }

    public void LoadRuntimeModules()
    {
        _runtimeModules.Clear();
        foreach (var module in _modules.OfType<IProviderRuntimeModule>())
        {
            _runtimeModules.Add(module);
            module.LoadRuntime(new ProviderRuntimeContext(botContext, config, hostServices));
        }
    }

    public void CreateMessageHandlers()
    {
        _handlers.Clear();
        var cardRenderers = new Dictionary<string, IProviderMediaCardRenderer?>(StringComparer.OrdinalIgnoreCase);
        foreach (var provider in _providers)
        {
            var moduleId = GetModuleId(provider.Id);
            if (!cardRenderers.TryGetValue(moduleId, out var renderer))
            {
                var factory = _modules.FirstOrDefault(module =>
                    string.Equals(module.Id, moduleId, StringComparison.OrdinalIgnoreCase)) as IProviderMediaCardRendererFactory;
                renderer = factory?.CreateMediaCardRenderer(new ProviderCardRenderContext(botContext, config, hostServices));
                cardRenderers[moduleId] = renderer;
            }

            _handlers[provider.Id] = new UnifiedMediaMessageHandler(
                new ProviderMessageHandlerContext(botContext, config, provider, hostServices, renderer));
        }
    }

    public IReadOnlyList<ProviderCommandDescriptor> CreateCommands()
    {
        if (_commands is not null) return _commands;
        var descriptors = new List<ProviderCommandDescriptor>();
        var commandNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var module in _modules.OfType<IProviderCommandContributor>())
        {
            var providerModule = (IMyParserProviderModule)module;
            var primaryProvider = _providers.FirstOrDefault(provider =>
                string.Equals(GetModuleId(provider.Id), providerModule.Id, StringComparison.OrdinalIgnoreCase));
            var handler = primaryProvider is null ? null : GetHandler(primaryProvider.Id);
            AddCommands(descriptors, commandNames, module.CreateCommands(
                new ProviderCommandContext(botContext, config, hostServices, primaryProvider, handler)));
        }

        foreach (var runtimeModule in _runtimeModules)
        {
            var primaryProvider = _providers.FirstOrDefault(provider => runtimeModule.ProviderIds.Any(id =>
                string.Equals(id, provider.Id, StringComparison.OrdinalIgnoreCase)));
            var handler = primaryProvider is null ? null : GetHandler(primaryProvider.Id);
            AddCommands(descriptors, commandNames, runtimeModule.CreateCommands(
                new ProviderCommandContext(botContext, config, hostServices, primaryProvider, handler)));
        }

        _commands = descriptors;
        return _commands;
    }

    public IProviderMessageHandler? GetHandler(string providerId)
    {
        return _handlers.TryGetValue(providerId, out var handler)
            ? handler
            : _handlers.TryGetValue(GetModuleId(providerId), out handler) ? handler : null;
    }

    public string GetModuleId(string providerId)
    {
        if (_moduleIds.TryGetValue(providerId, out var moduleId)) return moduleId;
        foreach (var runtimeModule in _runtimeModules)
        {
            if (runtimeModule.ProviderIds.Any(id => string.Equals(id, providerId, StringComparison.OrdinalIgnoreCase)))
                return runtimeModule.ProviderIds.FirstOrDefault() ?? providerId;
        }

        return providerId;
    }

    public bool IsAutoParseEnabled(IParseProvider? provider)
    {
        if (provider is null) return false;
        var policy = FindModuleCapability<IProviderAutoParsePolicy>(provider.Id);
        return policy?.IsAutoParseEnabled(config) ?? false;
    }

    public bool IsProviderEnabled(IParseProvider provider)
    {
        var policy = FindModuleCapability<IProviderAvailabilityPolicy>(provider.Id);
        return policy?.IsProviderEnabled(config) ?? true;
    }

    public bool HasAnyAutoParseProviderEnabled() => _autoParsePolicies.Any(policy => policy.IsAutoParseEnabled(config));

    public string? GetStrictAutoParseText(MessageEvent message)
    {
        foreach (var normalizer in _incomingTextNormalizers)
        {
            var normalized = normalizer.NormalizeParseText(message);
            if (!string.IsNullOrWhiteSpace(normalized)) return normalized;
        }

        return null;
    }

    public bool TryBuildReplyParseText(MessageEvent message, out string parseText)
    {
        foreach (var builder in _replyTextBuilders)
        {
            parseText = builder.TryBuildParseText(message) ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(parseText)) return true;
        }

        parseText = string.Empty;
        return false;
    }

    public bool IsDeferredParseText(string text) => _replyTextBuilders.Any(builder => builder.IsDeferredParseText(text));

    public bool HasIncomingProviderNormalizer(string providerId)
    {
        var moduleId = GetModuleId(providerId);
        return _incomingTextNormalizers.Any(normalizer =>
            normalizer is IMyParserProviderModule module
            && string.Equals(module.Id, moduleId, StringComparison.OrdinalIgnoreCase));
    }

    public bool IsPluginResultMessage(string text) =>
        _resultClassifiers.Any(classifier => classifier.IsPluginResultMessage(text))
        || _runtimeModules.Any(module => module.IsPluginResultMessage(text));

    public string NormalizeParseText(string text)
    {
        foreach (var normalizer in _textNormalizers) text = normalizer.NormalizeParseText(text) ?? text;
        return text;
    }

    public IReadOnlyList<string> GetCapabilitySummary(IMyParserProviderModule module)
    {
        var capabilities = new List<string> { "url-parse" };
        if (module is IProviderMediaCardRendererFactory) capabilities.Add("provider-card-renderer");
        if (module is IProviderTextNormalizer) capabilities.Add("text-normalizer");
        if (module is IIncomingProviderTextNormalizer) capabilities.Add("incoming-normalizer");
        if (module is ICookieValidator) capabilities.Add("cookie-validator");
        if (module is IProviderRuntimeModule) capabilities.Add("runtime-module");
        if (module is IProviderReplyParseTextBuilder) capabilities.Add("reply-parse-text-builder");
        return capabilities;
    }

    public IReadOnlyList<string> GetCapabilitySummary(IParseProvider provider)
    {
        var capabilities = new List<string> { "url-recognition", "content-parse", "shared-message-handler", "parsed-media-contract" };
        if (provider is IIncomingMessageParseProvider) capabilities.Add("incoming-message-extract");
        if (provider is IParserHttpClientAccessor) capabilities.Add("http-client");
        if (provider is IParseProviderWithParser { ParserObject: IParserHttpClientAccessor }) capabilities.Add("parser-http-client");
        if (provider is IVideoDownloadGate || provider is IParseProviderWithParser { ParserObject: IVideoDownloadGate }) capabilities.Add("video-download-gate");
        if (provider is IDisposable) capabilities.Add("disposable");
        return capabilities;
    }

    public void LogCapabilities()
    {
        if (_modules.Count == 0)
        {
            BotLog.Warning("MyParser 未发现任何 provider module。请检查 provider 源码是否已通过 MyParserProviderAttribute 注册并编译进主插件。");
            return;
        }

        BotLog.Info($"MyParser provider 能力概览：modules={_modules.Count}, providers={_providers.Count}");
        foreach (var module in _modules.OrderBy(module => module.Id, StringComparer.OrdinalIgnoreCase))
        {
            var moduleProviders = _providers.Where(provider =>
                    string.Equals(GetModuleId(provider.Id), module.Id, StringComparison.OrdinalIgnoreCase))
                .OrderBy(GetProviderOrder).ThenBy(provider => provider.Id, StringComparer.OrdinalIgnoreCase).ToArray();
            BotLog.Info($"MyParser provider module：id={module.Id}, type={module.GetType().FullName}, capabilities=[{string.Join(", ", GetCapabilitySummary(module))}]");
            if (moduleProviders.Length == 0)
            {
                BotLog.Warning($"MyParser provider module 未注册解析器：id={module.Id}");
                continue;
            }

            foreach (var provider in moduleProviders)
            {
                BotLog.Info($"MyParser provider：id={provider.Id}, name={provider.Name}, type={provider.GetType().FullName}, capabilities=[{string.Join(", ", GetCapabilitySummary(provider))}]");
            }
        }
    }

    private T? FindModuleCapability<T>(string providerId) where T : class
    {
        var moduleId = GetModuleId(providerId);
        return _modules.FirstOrDefault(module => string.Equals(module.Id, moduleId, StringComparison.OrdinalIgnoreCase)) as T;
    }

    private void RegisterModuleCapabilities()
    {
        _cookieDescriptors.Clear();
        _textNormalizers.Clear();
        _incomingTextNormalizers.Clear();
        _autoParsePolicies.Clear();
        _availabilityPolicies.Clear();
        _resultClassifiers.Clear();
        _replyTextBuilders.Clear();

        foreach (var module in _modules)
        {
            if (module is IProviderCookieStore cookieStore) _cookieDescriptors.AddRange(cookieStore.CookieDescriptors);
            if (module is IProviderTextNormalizer textNormalizer) _textNormalizers.Add(textNormalizer);
            if (module is IIncomingProviderTextNormalizer incomingNormalizer) _incomingTextNormalizers.Add(incomingNormalizer);
            if (module is IProviderAutoParsePolicy autoParsePolicy) _autoParsePolicies.Add(autoParsePolicy);
            if (module is IProviderAvailabilityPolicy availabilityPolicy) _availabilityPolicies.Add(availabilityPolicy);
            if (module is IProviderResultMessageClassifier resultClassifier) _resultClassifiers.Add(resultClassifier);
            if (module is IProviderReplyParseTextBuilder replyTextBuilder) _replyTextBuilders.Add(replyTextBuilder);
        }
    }

    private static IMyParserProviderModule[] DiscoverProviderModules()
    {
        var assembly = typeof(MyParserPlugin).Assembly;
        return assembly.GetTypes()
            .Where(type => !type.IsAbstract
                           && type.GetCustomAttribute<MyParserProviderAttribute>() is not null
                           && typeof(IMyParserProviderModule).IsAssignableFrom(type))
            .OrderBy(type => type.GetCustomAttribute<MyParserProviderAttribute>()!.Id, StringComparer.OrdinalIgnoreCase)
            .Select(type =>
            {
                var id = type.GetCustomAttribute<MyParserProviderAttribute>()!.Id;
                BotLog.Info($"MyParser 发现 provider module 类型：id={id}, type={type.FullName}");
                return (IMyParserProviderModule)Activator.CreateInstance(type)!;
            })
            .ToArray();
    }

    private static int GetProviderOrder(IParseProvider provider) => provider is IProviderPriority priority ? priority.Priority : 100;

    private static void AddCommands(List<ProviderCommandDescriptor> target, HashSet<string> commandNames,
        IEnumerable<ProviderCommandDescriptor> descriptors)
    {
        foreach (var descriptor in descriptors)
        {
            if (!commandNames.Add(descriptor.Command))
            {
                BotLog.Warning($"MyParser provider command 重复，已忽略：{descriptor.Command}");
                continue;
            }

            target.Add(descriptor);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var handler in _handlers.Values.Distinct()) handler.Dispose();
        var disposedProviders = new HashSet<IDisposable>(ReferenceEqualityComparer.Instance);
        foreach (var disposable in _providerDisposables)
        {
            if (disposedProviders.Add(disposable)) disposable.Dispose();
        }
        _handlers.Clear();
        _providerDisposables.Clear();
        _providers.Clear();
        _modules.Clear();
        _runtimeModules.Clear();
        _commands = null;
        _registry = null;
    }
}
