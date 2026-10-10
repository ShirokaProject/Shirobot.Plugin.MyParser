using ShiroBot.SDK.Plugin;
using Shirobot.Plugin.MyParser.Parsing;
using Shirobot.Plugin.MyParser.Media;

namespace Shirobot.Plugin.MyParser;

internal sealed class ProviderSettingsMonitor(IBotContext context, PluginConfig config)
{
    private IReadOnlyList<ProviderCookieDescriptor> _cookieDescriptors = [];

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
        MyParserRuntime.XDownloadDirectory = Path.Combine(PluginDirectory, "tmp", "x");

        TemporaryMediaCleanupService.CleanupStartupResidues(config);
        Directory.CreateDirectory(MyParserRuntime.DownloadDirectory);
        Directory.CreateDirectory(MyParserRuntime.BilibiliDownloadDirectory);
        Directory.CreateDirectory(MyParserRuntime.YouTubeDownloadDirectory);
        Directory.CreateDirectory(MyParserRuntime.WeixinChannelsDownloadDirectory);
        Directory.CreateDirectory(MyParserRuntime.XDownloadDirectory);
    }

    public void LoadCookies(IEnumerable<ProviderCookieDescriptor> descriptors)
    {
        _cookieDescriptors = descriptors.ToArray();
        var imported = CookieConfiguration.ImportLegacyFiles(config, PluginDirectory, () => context.Config.Save(config));
        if (imported > 0) BotLog.Info($"MyParser 已将 {imported} 个旧 Cookie 迁入 config.toml；原文件已保留。");
        ReloadCookies();
    }

    public void ReloadCookies()
    {
        foreach (var descriptor in _cookieDescriptors)
        {
            var cookie = CookieConfiguration.Get(config, descriptor.FileName).Trim().TrimStart('\ufeff');
            if (!string.IsNullOrEmpty(cookie) && descriptor.ValidateCookie is not null && !descriptor.ValidateCookie(cookie))
            {
                descriptor.ApplyCookie(string.Empty);
                BotLog.Warning($"MyParser {descriptor.DisplayName} Cookie 格式无效，请在插件配置的 Cookie 分组检查请求头 Cookie 值。");
                continue;
            }
            descriptor.ApplyCookie(cookie);
        }
    }
}
