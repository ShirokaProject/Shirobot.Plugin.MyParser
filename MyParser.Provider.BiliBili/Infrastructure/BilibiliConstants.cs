namespace MyParser.Provider.BiliBili.Infrastructure;

public static class BilibiliConstants
{
    public const string UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/125.0 Safari/537.36";
    public const string Origin = "https://www.bilibili.com";
    public const string NavApi = "https://api.bilibili.com/x/web-interface/nav";
    public const string ViewApi = "https://api.bilibili.com/x/web-interface/view";
    public const string PlayUrlApi = "https://api.bilibili.com/x/player/wbi/playurl";
    public const string CommentMainApi = "https://api.bilibili.com/x/v2/reply/wbi/main";
    public const string ArticleViewApi = "https://api.bilibili.com/x/article/view";
    public const string OpusDetailApi = "https://api.bilibili.com/x/polymer/web-dynamic/v1/opus/detail";
    public const string OpusDetailFeatures = "onlyfansVote,onlyfansAssetsV2,decorationCard,htmlNewStyle,ugcDelete,editable,opusPrivateVisible,tribeeEdit,avatarAutoTheme,avatarTypeOpus";
    public const string QrGenerateApi = "https://passport.bilibili.com/x/passport-login/web/qrcode/generate";
    public const string QrPollApi = "https://passport.bilibili.com/x/passport-login/web/qrcode/poll";
}
