using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace MyParser.Provider.BiliBili.Infrastructure;

internal static class BilibiliAppSigner
{
    // Public Android client signing keys; no user access token is required for video metadata.
    private const string AppKey = "1d8b6e7d45233436";
    private const string AppSecret = "560c52ccd288fed045859ed18bffd973";
    public const string UserAgent = "Mozilla/5.0 BiliDroid/8.43.0 (bili@android)";

    public static Dictionary<string, string> Sign(IReadOnlyDictionary<string, string> parameters)
    {
        var signed = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["appkey"] = AppKey,
            ["build"] = "8430300",
            ["mobi_app"] = "android",
            ["platform"] = "android",
            ["ts"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture),
        };
        foreach (var parameter in parameters) signed[parameter.Key] = parameter.Value;
        var query = string.Join("&", signed.Select(pair => $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"));
        signed["sign"] = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(query + AppSecret))).ToLowerInvariant();
        return signed.ToDictionary(pair => pair.Key, pair => pair.Value);
    }
}
