using ShiroBot.SDK.Config;

namespace Shirobot.Plugin.MyParser;

public sealed class PluginConfig
{
    [ConfigField("VideoSegment 发送 URI 协议。", Label = "VideoSegment 协议")]
    public VideoSegmentFileProtocol FileProtocol { get; set; } = VideoSegmentFileProtocol.File;
    [ConfigField("单个视频最大下载大小，单位 MB。", Label = "最大视频下载 MB", Min = 1, Max = 51200)]
    public int MaxVideoDownloadMegabytes { get; set; } = 1024;
    [ConfigField("SharpMP4 不支持当前媒体时使用的 ffmpeg 回退路径。留空时自动从 PATH 或程序目录查找。", Label = "ffmpeg 回退路径", Placeholder = "ffmpeg")]
    public string FfmpegPath { get; set; } = string.Empty;
    
    [ConfigField("是否自动解析聊天中的抖音链接。", Label = "自动解析抖音链接")]
    public bool AutoParseDouyinLinks { get; set; } = true;

    [ConfigField("是否自动解析聊天中的 Bilibili 链接。", Label = "自动解析 Bilibili 链接")]
    public bool AutoParseBilibiliLinks { get; set; } = true;

    [ConfigField("是否自动解析聊天中的小红书链接。", Label = "自动解析小红书链接")]
    public bool AutoParseXiaohongshuLinks { get; set; } = false;

    [ConfigField("是否自动解析聊天中的网易云音乐歌曲链接。", Label = "自动解析网易云音乐链接")]
    public bool AutoParseNetEaseCloudMusicLinks { get; set; } = true;

    [ConfigField("是否启用网易云音乐解析。关闭后网易云链接解析、搜索和登录命令均不可用。", Label = "启用网易云音乐解析")]
    public bool EnableNetEaseCloudMusic { get; set; } = true;

    [ConfigField("解析网易云音乐时是否发送歌曲介绍卡片。", Label = "发送网易云介绍卡片")]
    public bool SendNetEaseCloudMusicIntroCard { get; set; } = true;

    [ConfigField("解析网易云音乐时是否发送歌词卡片。", Label = "发送网易云歌词卡片")]
    public bool SendNetEaseCloudMusicLyricCard { get; set; } = true;

    [ConfigField("是否自动解析聊天中的微信视频号链接。", Label = "自动解析微信视频号链接")]
    public bool AutoParseWeixinChannelsLinks { get; set; } = true;

    [ConfigField("是否启用小黑盒解析。关闭后小黑盒链接解析不可用。", Label = "启用小黑盒解析")]
    public bool EnableHeybox { get; set; } = true;

    [ConfigField("是否自动解析聊天中的小黑盒链接。", Label = "自动解析小黑盒链接")]
    public bool AutoParseHeyboxLinks { get; set; } = true;

    [ConfigField("发送网易云音乐语音时是否额外发送手机高音质版。关闭时默认只发送电脑兼容版。", Label = "网易云额外发送手机高音质语音")]
    public bool SendNetEaseMobileBestRecord { get; set; } = false;

    [ConfigField("手动解析命令前缀，例如 #parse <链接>。", Label = "解析命令前缀", Placeholder = "#parse")]
    public string ParseCommandPrefix { get; set; } = "#parse";

    [ConfigField("小红书 xhshow sign 服务地址。留空时小红书部分接口不可用。", Label = "小红书 Sign 服务地址", Placeholder = "https://127.0.0.1:xxxx")]
    public string XiaohongshuSignServerUrl { get; set; } = string.Empty;

    [ConfigField("小红书 xhshow sign 服务 Token。请只在可信环境中配置。", Label = "小红书 Sign Token", Type = "password")]
    public string XiaohongshuSignServerToken { get; set; } = string.Empty;

    [ConfigField("解析小红书图文时是否尝试获取评论。需要 Cookie、xsec_token 和 sign 服务。", Label = "获取小红书评论")]
    public bool XiaohongshuFetchComments { get; set; } = true;

    [ConfigField("小红书评论最多获取条数。", Label = "小红书评论数量", Min = 0, Max = 50)]
    public int XiaohongshuCommentCount { get; set; } = 10;

    [ConfigField("选择视频流时优先更高帧率。", Label = "优先高帧率")]
    public bool PreferHighFps { get; set; } = true;

    [ConfigField("选择视频流时优先的编码格式。", Label = "优先视频编码")]
    public PreferredVideoCodec PreferredVideoCodec { get; set; } = PreferredVideoCodec.H265;

    [ConfigField("是否在日志中输出选择到的视频清晰度与编码信息。", Label = "记录画质选择日志")]
    public bool LogSelectedQualityInfo { get; set; } = true;

    [ConfigField("网络请求超时时间，单位秒。", Label = "请求超时秒数", Min = 5, Max = 300)]
    public int RequestTimeoutSeconds { get; set; } = 15;

    [ConfigField("回复解析结果时是否引用原消息。", Label = "引用回复")]
    public bool QuoteReply { get; set; } = false;

    [ConfigField("是否下载并发送 VideoSegment。关闭后只发送解析文本或卡片。", Label = "发送 VideoSegment")]
    public bool SendVideoSegment { get; set; } = true;

    [ConfigField("是否上传视频为群/私聊文件。", Label = "上传视频文件")]
    public bool UploadVideoAsFile { get; set; } = false;

    [ConfigField("仅当 VideoSegment 发送失败时才上传视频文件。", Label = "失败时才上传文件")]
    public bool UploadVideoAsFileOnlyOnVideoSendFailure { get; set; } = true;

    [ConfigField("解析 Bilibili 直播时是否尝试发送短回溯片段。", Label = "发送 Bilibili 直播回溯")]
    public bool SendBilibiliLiveReplayClip { get; set; } = true;

    [ConfigField("Bilibili 直播短回溯片段时长，单位秒。", Label = "直播回溯秒数", Min = 3, Max = 3000)]
    public int BilibiliLiveReplayClipSeconds { get; set; } = 30;

    [ConfigField("Bilibili 直播短回溯片段最大下载大小，单位 MB。", Label = "直播回溯最大 MB", Min = 1, Max = 2048)]
    public int BilibiliLiveReplayClipMaxMegabytes { get; set; } = 256;

    [ConfigField("视频发送完成后是否清理插件 tmp 目录中的本地视频。", Label = "发送后删除本地视频")]
    public bool DeleteLocalVideoAfterSend { get; set; } = true;

    [ConfigField("发送完成后延迟多少秒删除本地视频。0 表示立即删除。", Label = "延迟删除秒数", Min = 0, Max = 86400)]
    public int DeleteLocalVideoDelaySeconds { get; set; } = 0;

    [ConfigField("Bilibili 分 P 总览最多展示封面数量。", Label = "Bilibili 分P封面上限", Min = 0, Max = 200)]
    public int BilibiliMultiPageCoverImageLimit { get; set; } = 50;

    [ConfigField("视频超过大小限制时是否自动降级尝试较低画质。", Label = "超限自动降画质")]
    public bool AutoFallbackQualityBySize { get; set; } = true;

    [ConfigField("是否记录视频下载进度日志。", Label = "记录下载进度")]
    public bool LogDownloadProgress { get; set; } = true;

    [ConfigField("并行下载线程数。", Label = "并行下载线程数", Min = 1, Max = 64)]
    public int ParallelDownloadThreads { get; set; } = 16;
}

public enum VideoSegmentFileProtocol
{
    Base64,
    File,
    Http
}

public enum PreferredVideoCodec
{
    H265,
    H264,
    AV1
}
