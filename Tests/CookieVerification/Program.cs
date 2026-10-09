using Shirobot.Plugin.MyParser;
using ShiroBot.SDK.Config;
using System.Reflection;

var root = Path.Combine(Path.GetTempPath(), "myparser-cookie-verification-" + Guid.NewGuid());
Directory.CreateDirectory(Path.Combine(root, "cookies"));
try
{
    var config = new PluginConfig { DouyinCookie = "configured-douyin" };
    File.WriteAllText(Path.Combine(root, "cookies", "bilibili.txt"), "\ufeff  legacy-bilibili  ");
    File.WriteAllText(Path.Combine(root, "cookies", "douyin.txt"), "legacy-douyin");
    File.WriteAllText(Path.Combine(root, "cookies", "netease.txt"), "");
    File.WriteAllText(Path.Combine(root, "cookies", "heybox.txt"), "legacy-heybox");
    File.WriteAllText(Path.Combine(root, "cookies", "weixinchannels-yuanbao.txt"), "legacy-yuanbao");
    var saves = 0;
    var count = CookieConfiguration.ImportLegacyFiles(config, root, () => saves++);
    Check(count == 3 && saves == 1, "Expected one save and three imported credentials.");
    Check(config.BilibiliCookie == "legacy-bilibili" && config.DouyinCookie == "configured-douyin" &&
        config.NetEaseCloudMusicCookie == "" && config.HeyboxCookie == "legacy-heybox" &&
        config.WeixinChannelsYuanbaoCookie == "legacy-yuanbao", "Import must keep configured values and ignore empty files.");
    Check(File.ReadAllText(Path.Combine(root, "cookies", "douyin.txt")) == "legacy-douyin", "Import must not modify original files.");
    config.BilibiliCookie = "";
    Check(CookieConfiguration.ImportLegacyFiles(config, root, () => saves++) == 0 && saves == 1 &&
        config.BilibiliCookie == "", "Cleared credentials must stay cleared after another load.");
    Check(CookieConfiguration.Get(config, "douyin.txt") == "configured-douyin", "Runtime must read configured values.");
    config.DouyinCookie = "edited-douyin";
    Check(CookieConfiguration.Get(config, "douyin.txt") == "edited-douyin", "Runtime must observe config edits.");
    foreach (var name in new[] { "BilibiliCookie", "DouyinCookie", "NetEaseCloudMusicCookie", "HeyboxCookie", "WeixinChannelsYuanbaoCookie" })
    {
        var field = (ConfigFieldAttribute)Attribute.GetCustomAttribute(typeof(PluginConfig).GetProperty(name)!, typeof(ConfigFieldAttribute))!;
        Check(field.Type == "password" && field.Group == "cookies", "Dashboard must group and mask every credential.");
    }
    var retry = Path.Combine(root, "retry");
    Directory.CreateDirectory(Path.Combine(retry, "cookies"));
    File.WriteAllText(Path.Combine(retry, "cookies", "bilibili.txt"), "retry-cookie");
    try { CookieConfiguration.ImportLegacyFiles(new(), retry, () => throw new IOException("save failed")); }
    catch (IOException) { }
    Check(!File.Exists(Path.Combine(retry, "data", ".cookie-toml-imported-v1")), "Failed config saves must not mark migration complete.");
    var retryConfig = new PluginConfig();
    Check(CookieConfiguration.ImportLegacyFiles(retryConfig, retry, () => { }) == 1 && retryConfig.BilibiliCookie == "retry-cookie", "Failed migration must be retryable.");
    if (args.Length > 0)
    {
        var assembly = Assembly.LoadFrom(Path.GetFullPath(args[0]));
        const BindingFlags allInstance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        var configType = assembly.GetType("Shirobot.Plugin.MyParser.PluginConfig", throwOnError: true)!;
        var runningConfig = Activator.CreateInstance(configType)!;
        var editedConfig = Activator.CreateInstance(configType)!;
        configType.GetProperty("BilibiliCookie")!.SetValue(editedConfig, "SESSDATA=edited-cookie; bili_jct=test-token");
        var moduleType = assembly.GetType("MyParser.Provider.BiliBili.BilibiliProviderModule", throwOnError: true)!;
        var descriptors = moduleType.GetProperty("CookieDescriptors")!.GetValue(Activator.CreateInstance(moduleType));
        var monitorType = assembly.GetType("Shirobot.Plugin.MyParser.ProviderSettingsMonitor", throwOnError: true)!;
        var monitor = Activator.CreateInstance(monitorType, allInstance, null, [null, runningConfig], null)!;
        monitorType.GetField("_cookieDescriptors", allInstance)!.SetValue(monitor, descriptors);
        var pluginType = assembly.GetType("Shirobot.Plugin.MyParser.MyParserPlugin", throwOnError: true)!;
        var plugin = Activator.CreateInstance(pluginType)!;
        pluginType.GetField("_config", allInstance)!.SetValue(plugin, runningConfig);
        pluginType.GetField("_settingsMonitor", allInstance)!.SetValue(plugin, monitor);
        var changed = pluginType.GetMethods(allInstance).Single(x => x.Name.EndsWith(".OnConfigChangedAsync"));
        var runtime = assembly.GetType("Shirobot.Plugin.MyParser.MyParserRuntime", throwOnError: true)!;
        void Apply() => ((Task)changed.Invoke(plugin, [runningConfig, editedConfig, CancellationToken.None])!).GetAwaiter().GetResult();
        Apply();
        Check((string)runtime.GetProperty("BilibiliCookie")!.GetValue(null)! == "SESSDATA=edited-cookie; bili_jct=test-token", "Real config callback did not refresh the runtime Cookie.");
        configType.GetProperty("BilibiliCookie")!.SetValue(editedConfig, "invalid-cookie");
        Apply();
        Check((string)runtime.GetProperty("BilibiliCookie")!.GetValue(null)! == "", "Invalid Cookie must be excluded from runtime.");
        configType.GetProperty("BilibiliCookie")!.SetValue(editedConfig, "");
        Apply();
        Check((string)runtime.GetProperty("BilibiliCookie")!.GetValue(null)! == "", "Clearing config must clear the runtime Cookie.");

        var douyinType = assembly.GetType("MyParser.Provider.Douyin.Services.DouyinParseService", throwOnError: true)!;
        var workParserType = assembly.GetType("MyParser.Provider.Douyin.Abstractions.IDouyinWorkParser", throwOnError: true)!;
        using var http = new HttpClient();
        runtime.GetProperty("DouyinCookie")!.SetValue(null, "sessionid=initial");
        var douyin = Activator.CreateInstance(douyinType, [http, Array.CreateInstance(workParserType, 0), runningConfig])!;
        var sessionProperty = douyinType.GetProperty("_guestSession", allInstance)!;
        string Header()
        {
            var session = sessionProperty.GetValue(douyin)!;
            return (string)session.GetType().GetMethod("BuildCookieHeader")!.Invoke(session, null)!;
        }
        Check(Header().Contains("sessionid=initial"), "Initial Douyin Cookie was not applied.");
        runtime.GetProperty("DouyinCookie")!.SetValue(null, "sessionid=edited");
        Check(Header().Contains("sessionid=edited") && !Header().Contains("initial"), "Douyin retained stale configured credentials.");
        runtime.GetProperty("DouyinCookie")!.SetValue(null, "");
        Check(Header() == "", "Clearing Douyin config retained a logged-in session.");
        Console.WriteLine("Merged plugin config callback, validation, clearing and Douyin session refresh verification passed.");
    }
    Console.WriteLine("Cookie migration, clearing, runtime configuration and Dashboard schema verification passed.");
}
finally { Directory.Delete(root, recursive: true); }

static void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}
