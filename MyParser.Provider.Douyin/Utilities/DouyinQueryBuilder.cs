using System.Web;
using MyParser.Provider.Douyin.Infrastructure;

namespace MyParser.Provider.Douyin.Utilities;

public static class DouyinQueryBuilder
{
    public static string BuildUserProfileQuery(string secUserId)
    {
        var pairs = CreateCommonWebPairs();
        pairs["publish_video_strategy_type"] = "2";
        pairs["source"] = "channel_pc_web";
        pairs["sec_user_id"] = secUserId;
        return Build(pairs);
    }

    public static string BuildSearchItemQuery(string keyword)
    {
        var pairs = CreateCommonWebPairs();
        pairs["search_channel"] = "aweme_video_web";
        pairs["sort_type"] = "0";
        pairs["publish_time"] = "0";
        pairs["keyword"] = keyword;
        pairs["search_source"] = "normal_search";
        pairs["query_correct_type"] = "1";
        pairs["is_filter_search"] = "0";
        pairs["offset"] = "0";
        pairs["count"] = "12";
        AddNetworkFields(pairs);
        return Build(pairs);
    }

    public static string BuildUserPostQuery(string secUserId, string awemeId, long maxCursor)
    {
        var pairs = CreateCommonWebPairs();
        pairs["sec_user_id"] = secUserId;
        pairs["max_cursor"] = maxCursor.ToString();
        pairs["locate_item_id"] = awemeId;
        pairs["locate_query"] = "false";
        pairs["show_live_replay_strategy"] = "1";
        pairs["need_time_list"] = "0";
        pairs["time_list_query"] = "0";
        pairs["whale_cut_token"] = string.Empty;
        pairs["cut_version"] = "1";
        pairs["count"] = "18";
        pairs["publish_video_strategy_type"] = "2";
        AddNetworkFields(pairs);
        return Build(pairs);
    }

    public static string BuildDetailQuery(string awemeId)
    {
        var pairs = CreateCommonWebPairs();
        pairs["aweme_id"] = awemeId;
        return Build(pairs);
    }

    public static string BuildCommentListQuery(
        string awemeId,
        long cursor,
        int count,
        string? msToken,
        string? webId,
        string? verifyFp)
    {
        var pairs = CreateCommonWebPairs();
        pairs["aweme_id"] = awemeId;
        pairs["cursor"] = cursor.ToString();
        pairs["count"] = Math.Clamp(count, 1, 20).ToString();
        pairs["item_type"] = "0";
        pairs["insert_ids"] = string.Empty;
        pairs["whale_cut_token"] = string.Empty;
        pairs["cut_version"] = "1";
        pairs["rcFT"] = string.Empty;
        pairs["update_version_code"] = "170400";
        pairs["pc_img_format"] = "webp";
        pairs["webid"] = string.IsNullOrWhiteSpace(webId) ? GenerateNumericId() : webId;
        pairs["msToken"] = string.IsNullOrWhiteSpace(msToken) ? GenerateMsTokenFallback() : msToken;
        if (!string.IsNullOrWhiteSpace(verifyFp))
        {
            pairs["verifyFp"] = verifyFp;
            pairs["fp"] = verifyFp;
        }
        AddNetworkFields(pairs);
        return Build(pairs);
    }

    public static string BuildHjDetailQuery(string awemeId, string? msToken = null, string? webId = null, string? verifyFp = null)
    {
        var fp = string.IsNullOrWhiteSpace(verifyFp) ? GenerateVerifyFp() : verifyFp;
        var pairs = new Dictionary<string, string?>
        {
            ["device_platform"] = "webapp",
            ["aid"] = "6383",
            ["channel"] = "channel_pc_web",
            ["aweme_id"] = awemeId,
            ["request_source"] = "600",
            ["origin_type"] = "video_page",
            ["update_version_code"] = "170400",
            ["pc_client_type"] = "1",
            ["pc_libra_divert"] = "Windows",
            ["support_h265"] = "1",
            ["support_dash"] = "0",
            ["cpu_core_num"] = "8",
            ["version_code"] = "190500",
            ["version_name"] = "19.5.0",
            ["cookie_enabled"] = "true",
            ["screen_width"] = DouyinConstants.ScreenWidth,
            ["screen_height"] = DouyinConstants.ScreenHeight,
            ["browser_language"] = DouyinConstants.BrowserLanguage,
            ["browser_platform"] = "Win32",
            ["browser_name"] = "Chrome",
            ["browser_version"] = DouyinConstants.ChromeVersion,
            ["browser_online"] = "true",
            ["engine_name"] = "Blink",
            ["engine_version"] = DouyinConstants.ChromeVersion,
            ["os_name"] = "Windows",
            ["os_version"] = "10",
            ["device_memory"] = "8",
            ["platform"] = "PC",
            ["downlink"] = "9.3",
            ["effective_type"] = "4g",
            ["round_trip_time"] = "0",
            ["webid"] = string.IsNullOrWhiteSpace(webId) ? GenerateNumericId() : webId,
            ["verifyFp"] = fp,
            ["fp"] = fp,
            ["msToken"] = string.IsNullOrWhiteSpace(msToken) ? GenerateMsTokenFallback() : msToken,
        };
        return Build(pairs);
    }

    private static Dictionary<string, string?> CreateCommonWebPairs()
    {
        return new Dictionary<string, string?>
        {
            ["device_platform"] = "webapp",
            ["aid"] = "6383",
            ["channel"] = "channel_pc_web",
            ["pc_client_type"] = "1",
            ["version_code"] = "290100",
            ["version_name"] = "29.1.0",
            ["cookie_enabled"] = "true",
            ["browser_language"] = DouyinConstants.BrowserLanguage,
            ["browser_platform"] = "Win32",
            ["browser_name"] = "Chrome",
            ["browser_version"] = DouyinConstants.ChromeVersion,
            ["browser_online"] = "true",
            ["engine_name"] = "Blink",
            ["engine_version"] = DouyinConstants.ChromeVersion,
            ["os_name"] = "Windows",
            ["os_version"] = "10",
            ["platform"] = "PC",
            ["screen_width"] = DouyinConstants.ScreenWidth,
            ["screen_height"] = DouyinConstants.ScreenHeight,
            ["device_memory"] = "8",
            ["cpu_core_num"] = "8",
            ["msToken"] = string.Empty,
        };
    }

    private static void AddNetworkFields(Dictionary<string, string?> pairs)
    {
        pairs["downlink"] = "10";
        pairs["effective_type"] = "4g";
        pairs["round_trip_time"] = "50";
    }

    private static string Build(Dictionary<string, string?> pairs)
    {
        return string.Join("&", pairs.Select(kv => $"{HttpUtility.UrlEncode(kv.Key)}={HttpUtility.UrlEncode(kv.Value ?? string.Empty)}"));
    }

    private static string GenerateVerifyFp()
    {
        const string chars = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";
        var milliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var base36 = ToBase36(milliseconds);
        var random = new char[36];
        for (var i = 0; i < random.Length; i++)
        {
            random[i] = chars[Random.Shared.Next(chars.Length)];
        }

        random[8] = random[13] = random[18] = random[23] = '_';
        random[14] = '4';
        random[19] = chars[(chars.ToString().IndexOf(random[19]) & 3) | 8];
        return "verify_" + base36 + "_" + new string(random);
    }

    private static string ToBase36(long value)
    {
        const string chars = "0123456789abcdefghijklmnopqrstuvwxyz";
        if (value == 0)
        {
            return "0";
        }

        var result = string.Empty;
        while (value > 0)
        {
            result = chars[(int)(value % 36)] + result;
            value /= 36;
        }

        return result;
    }

    private static string GenerateNumericId()
    {
        return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString() + Random.Shared.Next(100000, 999999).ToString();
    }

    private static string GenerateMsTokenFallback()
    {
        const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_";
        return new string(Enumerable.Range(0, 182).Select(_ => chars[Random.Shared.Next(chars.Length)]).ToArray());
    }
}
