using ShiroBot.SDK.Config;

namespace Shirobot.Plugin.MyParser;

public sealed class PluginConfig
{
    // 通用设置
    [ConfigField("启用开发模式时只解析并发送文本/卡片，不下载或发送视频。", Label = "【通用设置】开发模式")]
    public bool DevelopmentMode { get; set; } = false;

    [ConfigField("是否发送各平台封面图片和封面卡片，不影响正文图片。", Label = "【通用设置】发送封面（总开关）")]
    public bool SendCoverImages { get; set; } = true;

    [ConfigField("可选 HTTP/HTTPS 代理地址，例如 http://127.0.0.1:7890；留空使用系统网络设置。", Label = "HTTP 代理", Placeholder = "http://127.0.0.1:7890")]
    public string HttpProxy { get; set; } = string.Empty;

    [ConfigField("VideoSegment 发送 URI 协议：0 = Base64，1 = File（默认），2 = Http。", Label = "VideoSegment 协议", Min = 0, Max = 2)]
    public int FileProtocol { get; set; } = 1;

    [ConfigField("单个视频最大下载大小，单位 MB。", Label = "最大视频下载 MB", Min = 1, Max = 51200)]
    public int MaxVideoDownloadMegabytes { get; set; } = 1024;

    [ConfigField("静图视频编码及不支持的媒体回退所用 ffmpeg；同时需要同目录或 PATH 中有 ffprobe。留空自动查找。", Label = "ffmpeg 路径", Placeholder = "ffmpeg")]
    public string FfmpegPath { get; set; } = string.Empty;

    [ConfigField("选择视频流时优先更高帧率。", Label = "优先高帧率")]
    public bool PreferHighFps { get; set; } = true;

    [ConfigField("优先视频编码：0 = H265，1 = H264，2 = AV1。", Label = "优先视频编码")]
    public PreferredVideoCodec PreferredVideoCodec { get; set; } = PreferredVideoCodec.H265;

    [ConfigField("网络请求超时时间，单位秒。", Label = "请求超时秒数", Min = 5, Max = 300)]
    public int RequestTimeoutSeconds { get; set; } = 15;

    [ConfigField("回复解析结果时是否引用原消息。", Label = "引用回复")]
    public bool QuoteReply { get; set; } = false;

    [ConfigField("是否将 Provider 的统一错误提示发送到聊天。关闭后只记录错误日志。", Label = "发送解析错误提示")]
    public bool SendProviderFailureMessages { get; set; } = true;

    [ConfigField("通用 Provider 错误提示模板，支持 {provider} 占位符。", Label = "错误提示模板", Placeholder = "{provider}处理失败，请稍后重试。")]
    public string ProviderFailureMessageTemplate { get; set; } = "{provider}处理失败，请稍后重试。";

    [ConfigField("是否下载并发送 VideoSegment。关闭后只发送解析文本或卡片。", Label = "发送 VideoSegment")]
    public bool SendVideoSegment { get; set; } = true;

    public bool IsVideoDeliveryEnabled() => !DevelopmentMode && SendVideoSegment;
    public bool IsVideoFileUploadEnabled() => !DevelopmentMode && UploadVideoAsFile;
    public bool IsBilibiliLiveReplayEnabled() => !DevelopmentMode && SendBilibiliLiveReplayClip;

    [ConfigField("是否上传视频为群/私聊文件。", Label = "上传视频文件")]
    public bool UploadVideoAsFile { get; set; } = false;

    [ConfigField("仅当 VideoSegment 发送失败时才上传视频文件。", Label = "失败时才上传文件")]
    public bool UploadVideoAsFileOnlyOnVideoSendFailure { get; set; } = true;

    [ConfigField("视频发送完成后是否清理插件 tmp 目录中的本地视频。", Label = "发送后删除本地视频")]
    public bool DeleteLocalVideoAfterSend { get; set; } = true;

    [ConfigField("发送完成后延迟多少秒删除本地视频。0 表示立即删除。", Label = "延迟删除秒数", Min = 0, Max = 86400)]
    public int DeleteLocalVideoDelaySeconds { get; set; } = 0;

    [ConfigField("视频超过大小限制时是否自动降级尝试较低画质。", Label = "超限自动降画质")]
    public bool AutoFallbackQualityBySize { get; set; } = true;

    [ConfigField("是否在日志中输出选择到的视频清晰度与编码信息。", Label = "记录画质选择日志")]
    public bool LogSelectedQualityInfo { get; set; } = true;

    [ConfigField("是否记录视频下载进度日志。", Label = "记录下载进度")]
    public bool LogDownloadProgress { get; set; } = true;

    [ConfigField("并行下载线程数：0 使用 LightDl 默认值，1–64 使用指定线程数。", Label = "并行下载线程数", Min = 0, Max = 64)]
    public int ParallelDownloadThreads { get; set; } = 0;

    [ConfigField("是否将抖音图文音乐和网易云音乐编码为 Silk 语音；关闭后直接尝试发送 MP3 语音。", Label = "启用 Silk 编码")]
    public bool EnableSilkEncoding { get; set; } = true;

    // 抖音
    [ConfigField("是否自动解析聊天中的抖音链接。", Label = "【抖音】自动解析抖音链接")]
    public bool AutoParseDouyinLinks { get; set; } = true;

    [ConfigField("是否发送抖音作品封面卡片。", Label = "发送抖音封面")]
    public bool SendDouyinCover { get; set; } = true;

    [ConfigField("解析抖音作品时是否获取并发送热门评论。需要有效的抖音 Cookie，接口失败不会影响作品发送。", Label = "获取抖音评论")]
    public bool DouyinFetchComments { get; set; } = true;

    [ConfigField("抖音作品最多获取的热门评论数量。", Label = "抖音评论数量", Min = 0, Max = 50)]
    public int DouyinCommentCount { get; set; } = 10;

    // Bilibili
    [ConfigField("是否自动解析聊天中的 Bilibili 链接。", Label = "【Bilibili】自动解析 Bilibili 链接")]
    public bool AutoParseBilibiliLinks { get; set; } = true;

    [ConfigField("是否发送 Bilibili 视频、番剧、分 P 和直播封面。", Label = "发送 Bilibili 封面")]
    public bool SendBilibiliVideoCover { get; set; } = true;

    [ConfigField("Bilibili 分 P 总览最多展示封面数量。", Label = "Bilibili 分P封面上限", Min = 0, Max = 200)]
    public int BilibiliMultiPageCoverImageLimit { get; set; } = 50;

    [ConfigField("解析 Bilibili 直播时是否尝试发送短回溯片段。", Label = "发送 Bilibili 直播回溯")]
    public bool SendBilibiliLiveReplayClip { get; set; } = true;

    [ConfigField("Bilibili 直播短回溯片段时长，单位秒。", Label = "直播回溯秒数", Min = 3, Max = 3000)]
    public int BilibiliLiveReplayClipSeconds { get; set; } = 30;

    [ConfigField("Bilibili 直播短回溯片段最大下载大小，单位 MB。", Label = "直播回溯最大 MB", Min = 1, Max = 2048)]
    public int BilibiliLiveReplayClipMaxMegabytes { get; set; } = 256;

    // 网易云音乐
    [ConfigField("是否启用网易云音乐解析。关闭后网易云链接解析和 #wyy 搜索均不可用。", Label = "【网易云音乐】启用网易云音乐解析")]
    public bool EnableNetEaseCloudMusic { get; set; } = true;

    [ConfigField("是否自动解析聊天中的网易云音乐歌曲链接。", Label = "自动解析网易云音乐链接")]
    public bool AutoParseNetEaseCloudMusicLinks { get; set; } = true;

    [ConfigField("解析网易云音乐时是否发送歌曲介绍卡片。", Label = "发送网易云介绍卡片")]
    public bool SendNetEaseCloudMusicIntroCard { get; set; } = true;

    [ConfigField("解析网易云音乐时是否发送歌词卡片。", Label = "发送网易云歌词卡片")]
    public bool SendNetEaseCloudMusicLyricCard { get; set; } = true;

    [ConfigField("发送网易云音乐语音时是否额外发送手机高音质版。关闭时默认只发送电脑兼容版。", Label = "网易云额外发送手机高音质语音")]
    public bool SendNetEaseMobileBestRecord { get; set; } = false;

    // 小黑盒
    [ConfigField("是否启用小黑盒解析。关闭后小黑盒链接解析不可用。", Label = "【小黑盒】启用小黑盒解析")]
    public bool EnableHeybox { get; set; } = true;

    [ConfigField("是否自动解析聊天中的小黑盒链接。", Label = "自动解析小黑盒链接")]
    public bool AutoParseHeyboxLinks { get; set; } = true;

    [ConfigField("是否发送小黑盒封面。", Label = "发送小黑盒封面")]
    public bool SendHeyboxCover { get; set; } = true;

    // 微信视频号
    [ConfigField("是否自动解析聊天中的微信视频号链接。", Label = "【微信视频号】自动解析微信视频号链接")]
    public bool AutoParseWeixinChannelsLinks { get; set; } = true;

    [ConfigField("是否发送微信视频号封面卡片。", Label = "发送视频号封面")]
    public bool SendWeixinChannelsCover { get; set; } = true;

    // YouTube
    [ConfigField("是否自动解析聊天中的 YouTube 视频链接。", Label = "【YouTube】自动解析 YouTube 链接")]
    public bool AutoParseYouTubeLinks { get; set; } = true;

    [ConfigField("开启后仅允许机器人 Owner/Admin 发送的 YouTube 链接触发解析，群聊和私聊均生效。", Label = "YouTube 仅管理员")]
    public bool YouTubeAdminOnly { get; set; } = true;

    [ConfigField("是否发送 YouTube 视频封面。", Label = "发送 YouTube 封面")]
    public bool SendYouTubeCover { get; set; } = true;

    public bool IsCoverEnabled(string platform) => SendCoverImages && (platform.ToLowerInvariant() switch
    {
        var p when p.StartsWith("抖音") || p == "douyin" => SendDouyinCover,
        var p when p.StartsWith("bilibili") => SendBilibiliVideoCover,
        var p when p.StartsWith("小黑盒") || p == "heybox" => SendHeyboxCover,
        var p when p.StartsWith("网易云音乐") || p is "netease" or "neteasecloudmusic" => SendNetEaseCloudMusicIntroCard,
        var p when p.StartsWith("微信视频号") || p == "weixinchannels" => SendWeixinChannelsCover,
        var p when p.StartsWith("youtube") => SendYouTubeCover,
        _ => true,
    });
}

public enum PreferredVideoCodec
{
    H265,
    H264,
    AV1
}
