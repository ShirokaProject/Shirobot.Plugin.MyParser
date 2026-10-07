using System.Text.Json;
using System.Text.RegularExpressions;
using System.Web;
using static MyParser.Provider.Douyin.Parsing.DouyinParseHelpers;

namespace MyParser.Provider.Douyin.Parsing;

public static class DouyinCoverSelector
{
    public static string? ExtractSearchCoverUrl(JsonElement aweme)
    {
        var candidates = CollectCoverUrlCandidates(aweme);
        return candidates
                   .Where(IsLikelySearchCover)
                   .OrderByDescending(CoverUrlScore)
                   .FirstOrDefault()
               ?? candidates
                   .Where(i => i.Contains("douyinpic.com", StringComparison.OrdinalIgnoreCase))
                   .OrderByDescending(CoverUrlScore)
                   .FirstOrDefault();
    }

    public static string? ExtractPublishCoverUrl(JsonElement aweme)
    {
        var candidates = CollectCoverUrlCandidates(aweme);
        return candidates
                   .Where(IsLikelyPublishCover)
                   .OrderByDescending(CoverUrlScore)
                   .FirstOrDefault()
               ?? candidates
                   .Where(i => i.Contains("douyinpic.com", StringComparison.OrdinalIgnoreCase))
                   .OrderByDescending(CoverUrlScore)
                   .FirstOrDefault();
    }

    public static int CoverUrlScore(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return 0;
        }

        var score = 0;
        if (url.Contains("PackSourceEnum_SEARCH", StringComparison.OrdinalIgnoreCase) || url.Contains("s=SEARCH", StringComparison.OrdinalIgnoreCase))
        {
            score += 1500;
        }

        if (url.Contains("PackSourceEnum_PUBLISH", StringComparison.OrdinalIgnoreCase) || url.Contains("s=PUBLISH", StringComparison.OrdinalIgnoreCase))
        {
            score += 1000;
        }

        if (url.Contains("tplv-dy-resize-walign-adapt-aq", StringComparison.OrdinalIgnoreCase))
        {
            score += 700;
        }

        if (url.Contains("tplv-dy-cropcenter", StringComparison.OrdinalIgnoreCase))
        {
            score += 500;
        }

        var resizeMatch = Regex.Match(url, @":(\d{3,4}):q\d+", RegexOptions.IgnoreCase);
        if (resizeMatch.Success && int.TryParse(resizeMatch.Groups[1].Value, out var resize))
        {
            score += resize;
        }

        var cropMatch = Regex.Match(url, @"cropcenter:(\d{3,4}):(\d{3,4})", RegexOptions.IgnoreCase);
        if (cropMatch.Success
            && int.TryParse(cropMatch.Groups[1].Value, out var cropW)
            && int.TryParse(cropMatch.Groups[2].Value, out var cropH))
        {
            score += Math.Max(cropW, cropH);
        }

        if (url.Contains("sc=cover", StringComparison.OrdinalIgnoreCase))
        {
            score += 100;
        }

        if (url.Contains("biz_tag=pcweb_cover", StringComparison.OrdinalIgnoreCase))
        {
            score += 50;
        }

        if (url.Contains("tos-cn-i-dy", StringComparison.OrdinalIgnoreCase))
        {
            score += 25;
        }

        return score;
    }

    private static List<string> CollectCoverUrlCandidates(JsonElement aweme)
    {
        var candidates = new List<string>();
        if (TryGetProperty(aweme, "video", out var video) && video.ValueKind == JsonValueKind.Object)
        {
            AddCandidate(GetString(video, "cover_url"));
            AddCandidate(GetString(video, "coverUrl"));

            foreach (var field in new[] { "cover", "origin_cover", "cover_original_scale", "raw_cover" })
            {
                if (TryGetProperty(video, field, out var cover))
                {
                    foreach (var url in EnumerateUrlList(cover))
                    {
                        AddCandidate(url);
                    }
                }
            }
        }

        CollectUrlStrings(aweme, candidates);

        return candidates;

        void AddCandidate(string? url)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                return;
            }

            url = HttpUtility.HtmlDecode(url).Replace("\\u002F", "/");
            if (!candidates.Contains(url, StringComparer.Ordinal))
            {
                candidates.Add(url);
            }
        }
    }

    private static bool IsLikelyPublishCover(string url)
    {
        return url.Contains("PackSourceEnum_PUBLISH", StringComparison.OrdinalIgnoreCase)
               || url.Contains("s=PUBLISH", StringComparison.OrdinalIgnoreCase)
               || url.Contains("tplv-dy-cropcenter", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsLikelySearchCover(string url)
    {
        return url.Contains("PackSourceEnum_SEARCH", StringComparison.OrdinalIgnoreCase)
               || url.Contains("s=SEARCH", StringComparison.OrdinalIgnoreCase)
               || url.Contains("tplv-dy-resize-walign-adapt-aq", StringComparison.OrdinalIgnoreCase);
    }

    private static void CollectUrlStrings(JsonElement element, List<string> result)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    CollectUrlStrings(property.Value, result);
                }
                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    CollectUrlStrings(item, result);
                }
                break;
            case JsonValueKind.String:
                var value = element.GetString();
                if (!string.IsNullOrWhiteSpace(value) && value.Contains("douyinpic.com", StringComparison.OrdinalIgnoreCase))
                {
                    value = HttpUtility.HtmlDecode(value).Replace("\\u002F", "/");
                    if (!result.Contains(value, StringComparer.Ordinal))
                    {
                        result.Add(value);
                    }
                }
                break;
        }
    }
}
