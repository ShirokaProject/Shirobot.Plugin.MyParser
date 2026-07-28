using System.Text.Json;
using System.Text.RegularExpressions;
using static MyParser.Provider.Douyin.Utilities.DouyinParseHelpers;

namespace MyParser.Provider.Douyin.Utilities;

public static class DouyinAwemeExtractor
{
    public static bool TryGetAwemeDetail(JsonElement root, out JsonElement aweme)
    {
        if (TryGetProperty(root, "aweme_detail", out var detail) && detail.ValueKind == JsonValueKind.Object)
        {
            aweme = detail;
            return true;
        }

        if (TryGetProperty(root, "loaderData", out var loaderData) && loaderData.ValueKind == JsonValueKind.Object)
        {
            foreach (var pageKey in new[] { "video_(id)/page", "note_(id)/page" })
            {
                if (!TryGetProperty(loaderData, pageKey, out var page)
                    || !TryGetProperty(page, "videoInfoRes", out var videoInfo)
                    || !TryGetProperty(videoInfo, "item_list", out var itemList)
                    || itemList.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                var first = itemList.EnumerateArray().FirstOrDefault();
                if (first.ValueKind == JsonValueKind.Object)
                {
                    aweme = first;
                    return true;
                }
            }
        }

        aweme = default;
        return false;
    }

    public static bool TryExtractShareAweme(string html, string awemeId, out JsonDocument document)
    {
        foreach (Match match in Regex.Matches(html, @"(?:__UNIVERSAL_DATA_FOR_REHYDRATION__|_?ROUTER_DATA|RENDER_DATA|SSR_DATA|hydration)\s*=\s*(?<value>.+?)(?:</script>|;</script>|;\s*(?:window\.|</script>))", RegexOptions.IgnoreCase | RegexOptions.Singleline))
        {
            var raw = match.Groups["value"].Value.Trim().TrimEnd(';');
            foreach (var candidate in DecodeCandidates(raw))
            {
                try
                {
                    using var parsed = JsonDocument.Parse(candidate);
                    if (TryFindBestAweme(parsed.RootElement, awemeId, out var aweme))
                    {
                        document = JsonDocument.Parse("{\"aweme_detail\":" + aweme.GetRawText() + "}");
                        return true;
                    }
                }
                catch (JsonException) { }
            }
        }

        document = null!;
        return false;
    }

    private static IEnumerable<string> DecodeCandidates(string raw)
    {
        yield return raw;
        var unescaped = Regex.Unescape(raw.Trim('"', '\''));
        if (!string.Equals(unescaped, raw, StringComparison.Ordinal)) yield return unescaped;
        string? decoded = null;
        try { decoded = Uri.UnescapeDataString(raw.Trim('"', '\'')); } catch (UriFormatException) { }
        if (decoded is not null) yield return decoded;
    }

    private static bool TryFindBestAweme(JsonElement value, string awemeId, out JsonElement aweme)
    {
        var bestScore = int.MinValue;
        var best = default(JsonElement);
        Visit(value);
        aweme = best;
        return bestScore != int.MinValue;

        void Visit(JsonElement current)
        {
            if (current.ValueKind == JsonValueKind.Object)
            {
                if ((TryGetProperty(current, "aweme_id", out var id) || TryGetProperty(current, "awemeId", out id))
                    && string.Equals(id.ToString(), awemeId, StringComparison.Ordinal))
                {
                    var score = ScoreAwemeCandidate(current);
                    if (score > bestScore)
                    {
                        bestScore = score;
                        best = current.Clone();
                    }
                }

                foreach (var property in current.EnumerateObject()) Visit(property.Value);
                return;
            }

            if (current.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in current.EnumerateArray()) Visit(item);
                return;
            }

            if (current.ValueKind != JsonValueKind.String) return;
            var nested = current.GetString();
            if (string.IsNullOrWhiteSpace(nested) || !nested.TrimStart().StartsWith('{')) return;
            try
            {
                using var parsed = JsonDocument.Parse(nested);
                Visit(parsed.RootElement);
            }
            catch (JsonException)
            {
            }
        }
    }

    private static int ScoreAwemeCandidate(JsonElement candidate)
    {
        var score = 0;
        if (TryGetProperty(candidate, "images", out var images) && images.ValueKind == JsonValueKind.Array)
        {
            score += 200 + images.GetArrayLength();
            foreach (var image in images.EnumerateArray())
            {
                if (HasEmbeddedVideo(image)) score += 1000;
            }
        }

        if (TryGetProperty(candidate, "music", out var music) && music.ValueKind == JsonValueKind.Object) score += 200;
        if (TryGetProperty(candidate, "video", out var video) && video.ValueKind == JsonValueKind.Object) score += 100;
        if (TryGetProperty(candidate, "author", out var author) && author.ValueKind == JsonValueKind.Object) score += 20;
        if (TryGetProperty(candidate, "statistics", out var statistics) && statistics.ValueKind == JsonValueKind.Object) score += 10;
        return score;
    }

    private static bool HasEmbeddedVideo(JsonElement image)
    {
        if (image.ValueKind != JsonValueKind.Object) return false;
        foreach (var propertyName in new[]
                 {
                     "video", "video_info", "videoInfo", "video_clip", "videoClip", "clip", "clip_info", "clipInfo",
                     "play_addr", "playAddr", "download_addr", "downloadAddr",
                 })
        {
            if (TryGetProperty(image, propertyName, out var value)
                && value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
            {
                return true;
            }
        }

        return false;
    }
}
