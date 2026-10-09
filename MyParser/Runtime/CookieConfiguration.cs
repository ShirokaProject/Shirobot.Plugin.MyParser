namespace Shirobot.Plugin.MyParser;

/// <summary>Config is authoritative after a one-time, non-destructive import of legacy Cookie files.</summary>
internal static class CookieConfiguration
{
    private static readonly string[] LegacyFiles =
        ["bilibili.txt", "douyin.txt", "netease.txt", "heybox.txt", "weixinchannels-yuanbao.txt"];

    public static string Get(PluginConfig config, string fileName) => fileName switch
    {
        "bilibili.txt" => config.BilibiliCookie,
        "douyin.txt" => config.DouyinCookie,
        "netease.txt" => config.NetEaseCloudMusicCookie,
        "heybox.txt" => config.HeyboxCookie,
        "weixinchannels-yuanbao.txt" => config.WeixinChannelsYuanbaoCookie,
        _ => throw new InvalidOperationException("Unsupported Cookie configuration."),
    };

    private static void Set(PluginConfig config, string fileName, string value)
    {
        switch (fileName)
        {
            case "bilibili.txt": config.BilibiliCookie = value; break;
            case "douyin.txt": config.DouyinCookie = value; break;
            case "netease.txt": config.NetEaseCloudMusicCookie = value; break;
            case "heybox.txt": config.HeyboxCookie = value; break;
            case "weixinchannels-yuanbao.txt": config.WeixinChannelsYuanbaoCookie = value; break;
        }
    }

    public static int ImportLegacyFiles(PluginConfig config, string pluginDirectory, Action saveConfig)
    {
        // This persistent marker contains no credentials. Do not put it in an evictable cache:
        // clearing a configured Cookie must remain cleared on subsequent loads.
        var marker = Path.Combine(pluginDirectory, "data", ".cookie-toml-imported-v1");
        if (File.Exists(marker)) return 0;
        var imported = 0;
        foreach (var file in LegacyFiles)
        {
            if (!string.IsNullOrWhiteSpace(Get(config, file))) continue;
            var source = Path.Combine(pluginDirectory, "cookies", file);
            if (!File.Exists(source)) continue;
            var cookie = File.ReadAllText(source).Trim().TrimStart('\ufeff');
            if (string.IsNullOrWhiteSpace(cookie)) continue;
            Set(config, file, cookie);
            imported++;
        }
        // Record completion only after the imported values have been saved successfully.
        saveConfig();
        Directory.CreateDirectory(Path.GetDirectoryName(marker)!);
        File.WriteAllText(marker, "1");
        return imported;
    }
}
