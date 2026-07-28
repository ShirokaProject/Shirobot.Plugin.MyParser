namespace MyParser.Provider.Douyin.Infrastructure;

public static class DouyinConstants
{
    // Keep request headers, query fingerprint, msToken report and a_bogus UA in lockstep.
    public const string UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/150.0.0.0 Safari/537.36";
    public const string ChromeVersion = "150.0.0.0";
    public const string BrowserLanguage = "zh-CN";
    public const string ScreenWidth = "1920";
    public const string ScreenHeight = "1080";
    public const string BrowserFingerprint = "1920|937|1920|1040|1920|1040|1920|1080|Win32";

    public const string HomeUrl = "https://www.douyin.com/";
}
