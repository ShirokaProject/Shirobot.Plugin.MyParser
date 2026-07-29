using System.Text.Json;
using System.Text.RegularExpressions;

namespace MyParser.Provider.Douyin.Utilities;

public static class DouyinParseHelpers
{
    public static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out value))
        {
            return true;
        }

        value = default;
        return false;
    }

    public static string? GetString(JsonElement element, string name)
    {
        return TryGetProperty(element, name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    public static long GetLong(JsonElement element, string name)
    {
        if (!TryGetProperty(element, name, out var value))
        {
            return 0;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt64(out var i) => i,
            JsonValueKind.String when long.TryParse(value.GetString(), out var i) => i,
            _ => 0,
        };
    }

    public static long GetFirstLong(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            var value = GetLong(element, name);
            if (value > 0)
            {
                return value;
            }
        }

        return 0;
    }

    public static long GetStatisticLong(JsonElement aweme, params string[] names)
    {
        foreach (var statistics in EnumerateStatistics(aweme))
        {
            var value = GetFirstLong(statistics, names);
            if (value > 0)
            {
                return value;
            }
        }

        return 0;
    }

    private static IEnumerable<JsonElement> EnumerateStatistics(JsonElement aweme)
    {
        foreach (var property in new[] { "statistics", "statistics_data", "statisticsData", "stats" })
        {
            if (TryGetProperty(aweme, property, out var statistics) && statistics.ValueKind == JsonValueKind.Object)
            {
                yield return statistics;
            }
        }
    }

    public static int GetInt(JsonElement element, string name)
    {
        if (!TryGetProperty(element, name, out var value))
        {
            return 0;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt32(out var i) => i,
            JsonValueKind.String when int.TryParse(value.GetString(), out var i) => i,
            _ => 0,
        };
    }

    public static bool GetBool(JsonElement element, string name)
    {
        if (!TryGetProperty(element, name, out var value))
        {
            return false;
        }

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number when value.TryGetInt32(out var i) => i != 0,
            JsonValueKind.String when bool.TryParse(value.GetString(), out var b) => b,
            JsonValueKind.String when int.TryParse(value.GetString(), out var i) => i != 0,
            _ => false,
        };
    }

    public static string? ExtractFirstUrl(JsonElement parent, string propertyName)
    {
        return !TryGetProperty(parent, propertyName, out var obj)
            ? null
            : EnumerateUrlList(obj).FirstOrDefault();
    }

    public static IEnumerable<string> EnumerateUrlList(JsonElement element)
    {
        if (TryGetProperty(element, "url_list", out var urls) && urls.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in urls.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(item.GetString()))
                {
                    yield return item.GetString()!;
                }
            }
        }
    }

    public static string? ExtractAuthorAvatarUrl(JsonElement author)
    {
        return ExtractFirstUrl(author, "avatar_larger")
               ?? ExtractFirstUrl(author, "avatar_medium")
               ?? ExtractFirstUrl(author, "avatar_thumb");
    }

    public static List<string> ExtractTags(JsonElement aweme)
    {
        var result = new List<string>();
        if (TryGetProperty(aweme, "text_extra", out var textExtra) && textExtra.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in textExtra.EnumerateArray())
            {
                var tag = GetString(item, "hashtag_name");
                if (!string.IsNullOrWhiteSpace(tag))
                {
                    result.Add(tag.TrimStart('#'));
                }
            }
        }

        if (TryGetProperty(aweme, "cha_list", out var chaList) && chaList.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in chaList.EnumerateArray())
            {
                var tag = GetString(item, "cha_name");
                if (!string.IsNullOrWhiteSpace(tag))
                {
                    result.Add(tag.TrimStart('#'));
                }
            }
        }

        return result
            .Where(i => !string.IsNullOrWhiteSpace(i))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(8)
            .ToList();
    }

    public static string? ExtractSimpleCoverUrl(JsonElement video)
    {
        if (video.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var property in new[] { "animated_cover", "dynamic_cover", "cover_original_scale", "cover", "origin_cover", "raw_cover" })
        {
            var url = ExtractFirstUrl(video, property);
            if (!string.IsNullOrWhiteSpace(url))
            {
                return url;
            }
        }

        return null;
    }

    public static string NormalizeNoWatermarkUrl(string url) => url.Replace("playwm", "play", StringComparison.OrdinalIgnoreCase);

    public static string GuessRatio(string gearName, int bitRate, int width = 0, int height = 0)
    {
        var longSide = Math.Max(width, height);
        var shortSide = Math.Min(width, height);
        if (longSide >= 2560 || shortSide >= 1440) return "2k";
        if (longSide >= 1920 || shortSide >= 1080) return "1080p";
        if (longSide >= 1280 || shortSide >= 720) return "720p";
        if (longSide >= 960 || shortSide >= 540) return "540p";

        var match = Regex.Match(gearName, "(\\d{3,4})p?", RegexOptions.IgnoreCase);
        if (match.Success && int.TryParse(match.Groups[1].Value, out var gearNumber))
        {
            return gearNumber switch
            {
                >= 1440 => "2k",
                >= 1080 => "1080p",
                >= 720 => "720p",
                >= 540 => "540p",
                >= 480 => "480p",
                >= 360 => "360p",
                _ => $"{gearNumber}p",
            };
        }

        return bitRate switch
        {
            >= 2_000_000 => "1080p",
            >= 1_000_000 => "720p",
            >= 500_000 => "540p",
            > 0 => "480p",
            _ => "默认",
        };
    }

    public static int RatioRank(string ratio) => ratio switch
    {
        "2k" => 6,
        "1080p" => 5,
        "720p" => 4,
        "540p" => 3,
        "480p" => 2,
        "360p" => 1,
        _ => 0,
    };
}
