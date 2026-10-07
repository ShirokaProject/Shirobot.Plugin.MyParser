using System.Text.RegularExpressions;

namespace MyParser.Provider.BiliBili.Parsing;

internal enum BilibiliLinkKind
{
    Unknown,
    Video,
    Article,
    Bangumi,
    Live,
}

internal static partial class BilibiliUrlParser
{
    public static bool ContainsStrictBilibiliUrl(string text)
    {
        return BilibiliUrlMatcher.ContainsStrictBilibiliUrl(text);
    }

    public static string? ExtractStrictBilibiliUrl(string text)
    {
        return BilibiliUrlMatcher.ExtractStrictBilibiliUrl(text);
    }

    public static BilibiliLinkKind ClassifyLink(string text)
    {
        var strictUrl = ExtractStrictBilibiliUrl(text);
        if (strictUrl is not null && Uri.TryCreate(strictUrl, UriKind.Absolute, out var uri))
        {
            return ClassifyUri(uri);
        }

        var value = text.Trim();
        var standalone = StandaloneBilibiliIdRegex().Match(value);
        if (!standalone.Success) return BilibiliLinkKind.Unknown;
        return standalone.Groups["prefix"].Value.ToLowerInvariant() switch
        {
            "bv" or "av" => BilibiliLinkKind.Video,
            "cv" or "opus" => BilibiliLinkKind.Article,
            "ep" or "ss" or "md" => BilibiliLinkKind.Bangumi,
            _ => BilibiliLinkKind.Unknown,
        };
    }

    private static BilibiliLinkKind ClassifyUri(Uri uri)
    {
        var host = uri.Host;
        var path = uri.AbsolutePath;
        if (string.Equals(host, "live.bilibili.com", StringComparison.OrdinalIgnoreCase)
            && LiveRoomPathRegex().IsMatch(path))
        {
            return BilibiliLinkKind.Live;
        }

        if (!IsBilibiliWebHost(host)) return BilibiliLinkKind.Unknown;
        if (VideoPathRegex().IsMatch(path)) return BilibiliLinkKind.Video;
        if (ArticleOpusPathRegex().IsMatch(path) || ArticleCvidPathRegex().IsMatch(path)) return BilibiliLinkKind.Article;
        if (BangumiEpisodePathRegex().IsMatch(path)
            || BangumiSeasonPathRegex().IsMatch(path)
            || BangumiMediaPathRegex().IsMatch(path))
        {
            return BilibiliLinkKind.Bangumi;
        }

        return BilibiliLinkKind.Unknown;
    }

    private static bool IsBilibiliWebHost(string host) =>
        string.Equals(host, "bilibili.com", StringComparison.OrdinalIgnoreCase)
        || host.EndsWith(".bilibili.com", StringComparison.OrdinalIgnoreCase);

    private static string GetIdentitySearchText(string text)
    {
        var strictUrl = ExtractStrictBilibiliUrl(text);
        if (strictUrl is not null && Uri.TryCreate(strictUrl, UriKind.Absolute, out var uri))
        {
            return IsBilibiliWebHost(uri.Host) ? uri.AbsolutePath : string.Empty;
        }

        if (text.Contains("://", StringComparison.Ordinal)) return string.Empty;
        return text.Trim();
    }

    public static string? NormalizeStandaloneBilibiliId(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var value = text.Trim().TrimEnd('.', '。', ',', '，');
        var match = StandaloneBilibiliIdRegex().Match(value);
        if (!match.Success)
        {
            return null;
        }

        var prefix = match.Groups["prefix"].Value.ToLowerInvariant();
        var id = match.Groups["id"].Value;
        return prefix switch
        {
            "bv" => $"https://www.bilibili.com/video/BV{id}/",
            "av" => $"https://www.bilibili.com/video/av{id}/",
            "cv" => $"https://www.bilibili.com/read/cv{id}/",
            "opus" => $"https://www.bilibili.com/opus/{id}",
            "ep" => $"https://www.bilibili.com/bangumi/play/ep{id}",
            "ss" => $"https://www.bilibili.com/bangumi/play/ss{id}",
            "md" => $"https://www.bilibili.com/bangumi/media/md{id}",
            _ => null,
        };
    }

    public static string? NormalizeStandaloneBvid(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var value = text.Trim().TrimEnd('.', '。', ',', '，');
        var match = StandaloneBvidRegex().Match(value);
        return match.Success ? $"https://www.bilibili.com/video/{match.Value}/" : null;
    }

    public static string? NormalizeStandaloneVideoId(string text)
    {
        return NormalizeStandaloneId(text, ["bv", "av"]);
    }

    public static string? NormalizeStandaloneBangumiId(string text)
    {
        return NormalizeStandaloneId(text, ["ep", "ss", "md"]);
    }

    public static string? NormalizeStandaloneArticleId(string text)
    {
        return NormalizeStandaloneId(text, ["cv", "opus"]);
    }

    public static bool ContainsBilibiliUrl(string text)
    {
        return ClassifyLink(text) != BilibiliLinkKind.Unknown || ExtractB23Url(text) is not null;
    }

    public static string? ExtractBvid(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var strictUrl = ExtractStrictBilibiliUrl(text);
        if (strictUrl is not null && Uri.TryCreate(strictUrl, UriKind.Absolute, out var uri))
        {
            if (ClassifyUri(uri) != BilibiliLinkKind.Video) return null;
            var routeMatch = VideoPathRegex().Match(uri.AbsolutePath);
            return routeMatch.Success && routeMatch.Groups[1].Value.StartsWith("BV", StringComparison.OrdinalIgnoreCase)
                ? routeMatch.Groups[1].Value
                : null;
        }

        var standalone = StandaloneBilibiliIdRegex().Match(text.Trim());
        return standalone.Success && string.Equals(standalone.Groups["prefix"].Value, "bv", StringComparison.OrdinalIgnoreCase)
            ? "BV" + standalone.Groups["id"].Value
            : null;
    }

    public static long? ExtractCvid(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var strictUrl = ExtractStrictBilibiliUrl(text);
        if (strictUrl is not null && Uri.TryCreate(strictUrl, UriKind.Absolute, out var uri))
        {
            if (ClassifyUri(uri) != BilibiliLinkKind.Article) return null;
            var routeMatch = ArticleCvidPathRegex().Match(uri.AbsolutePath);
            return routeMatch.Success && long.TryParse(routeMatch.Groups[1].Value, out var routeCvid) ? routeCvid : null;
        }

        var standalone = StandaloneCvidRegex().Match(text.Trim());
        return standalone.Success && long.TryParse(standalone.Groups[1].Value, out var cvid) ? cvid : null;
    }

    public static string? ExtractOpusId(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var strictUrl = ExtractStrictBilibiliUrl(text);
        if (strictUrl is not null && Uri.TryCreate(strictUrl, UriKind.Absolute, out var uri))
        {
            if (ClassifyUri(uri) != BilibiliLinkKind.Article) return null;
            var routeMatch = ArticleOpusPathRegex().Match(uri.AbsolutePath);
            return routeMatch.Success ? routeMatch.Groups[1].Value : null;
        }

        var standalone = StandaloneOpusRegex().Match(text.Trim());
        return standalone.Success ? standalone.Groups[1].Value : null;
    }

    public static string? ExtractLiveRoomId(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var strictUrl = ExtractStrictBilibiliUrl(text);
        if (strictUrl is null || !Uri.TryCreate(strictUrl, UriKind.Absolute, out var uri)
            || ClassifyUri(uri) != BilibiliLinkKind.Live)
        {
            return null;
        }

        var match = LiveRoomPathRegex().Match(uri.AbsolutePath);
        return match.Success ? match.Groups[1].Value : null;
    }

    public static int? ExtractVideoPage(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var strictUrl = ExtractStrictBilibiliUrl(text);
        if (strictUrl is null || !Uri.TryCreate(strictUrl, UriKind.Absolute, out var uri)
            || ClassifyUri(uri) != BilibiliLinkKind.Video)
        {
            return null;
        }

        foreach (var part in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = part.Split('=', 2);
            if (pair.Length == 2 && string.Equals(Uri.UnescapeDataString(pair[0]), "p", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(Uri.UnescapeDataString(pair[1]), out var page) && page > 0)
                return page;
        }

        return null;
    }

    public static BilibiliBangumiIds ExtractBangumiIds(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return new BilibiliBangumiIds(null, null, null);
        }

        var strictUrl = ExtractStrictBilibiliUrl(text);
        if (strictUrl is not null && Uri.TryCreate(strictUrl, UriKind.Absolute, out var uri))
        {
            if (ClassifyUri(uri) != BilibiliLinkKind.Bangumi) return new BilibiliBangumiIds(null, null, null);
            var path = uri.AbsolutePath;
            var epPath = BangumiEpisodePathRegex().Match(path);
            var ssPath = BangumiSeasonPathRegex().Match(path);
            var mdPath = BangumiMediaPathRegex().Match(path);
            return new BilibiliBangumiIds(
                epPath.Success && long.TryParse(epPath.Groups[1].Value, out var routeEp) ? routeEp : null,
                ssPath.Success && long.TryParse(ssPath.Groups[1].Value, out var routeSs) ? routeSs : null,
                mdPath.Success && long.TryParse(mdPath.Groups[1].Value, out var routeMd) ? routeMd : null);
        }

        var value = text.Trim();
        var ep = StandaloneBangumiEpRegex().Match(value);
        var ss = StandaloneBangumiSeasonRegex().Match(value);
        var md = StandaloneBangumiMediaRegex().Match(value);
        return new BilibiliBangumiIds(
            ep.Success && long.TryParse(ep.Groups[1].Value, out var epId) ? epId : null,
            ss.Success && long.TryParse(ss.Groups[1].Value, out var seasonId) ? seasonId : null,
            md.Success && long.TryParse(md.Groups[1].Value, out var mediaId) ? mediaId : null);
    }

    public static string? ExtractB23Url(string text)
    {
        return BilibiliUrlMatcher.ExtractB23Url(text);
    }

    public static string? ExtractBilibiliUrl(string text)
    {
        var strict = ExtractStrictBilibiliUrl(text);
        if (strict is not null)
        {
            return strict;
        }

        var standalone = NormalizeStandaloneBilibiliId(text);
        if (standalone is not null)
        {
            return standalone;
        }

        var bvid = ExtractBvid(text);
        if (bvid is not null)
        {
            return $"https://www.bilibili.com/video/{bvid}/";
        }

        return ExtractB23Url(text);
    }

    public static long? ExtractAid(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var strictUrl = ExtractStrictBilibiliUrl(text);
        if (strictUrl is not null && Uri.TryCreate(strictUrl, UriKind.Absolute, out var uri))
        {
            if (ClassifyUri(uri) != BilibiliLinkKind.Video) return null;
            var routeMatch = VideoPathRegex().Match(uri.AbsolutePath);
            var routeId = routeMatch.Success ? routeMatch.Groups[1].Value : string.Empty;
            return routeId.StartsWith("av", StringComparison.OrdinalIgnoreCase)
                   && long.TryParse(routeId[2..], out var routeAid)
                ? routeAid
                : null;
        }

        var standalone = StandaloneAidRegex().Match(text.Trim());
        return standalone.Success && long.TryParse(standalone.Groups[1].Value, out var aid) ? aid : null;
    }

    private static string? NormalizeStandaloneId(string text, string[] allowedPrefixes)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var value = text.Trim().TrimEnd('.', '。', ',', '，');
        var match = StandaloneBilibiliIdRegex().Match(value);
        if (!match.Success)
        {
            return null;
        }

        var prefix = match.Groups["prefix"].Value.ToLowerInvariant();
        return allowedPrefixes.Contains(prefix, StringComparer.OrdinalIgnoreCase) ? NormalizeStandaloneBilibiliId(value) : null;
    }

    [GeneratedRegex(@"^(?<prefix>BV)(?<id>[0-9A-Za-z]{10})$|^(?<prefix>av|cv|opus|ep|ss|md)(?<id>\d+)$", RegexOptions.IgnoreCase)]
    private static partial Regex StandaloneBilibiliIdRegex();

    [GeneratedRegex(@"^BV[0-9A-Za-z]{10}$", RegexOptions.IgnoreCase)]
    private static partial Regex StandaloneBvidRegex();

    [GeneratedRegex(@"^av(\d+)$", RegexOptions.IgnoreCase)]
    private static partial Regex StandaloneAidRegex();

    [GeneratedRegex(@"^cv(\d+)$", RegexOptions.IgnoreCase)]
    private static partial Regex StandaloneCvidRegex();

    [GeneratedRegex(@"^opus(\d+)$", RegexOptions.IgnoreCase)]
    private static partial Regex StandaloneOpusRegex();

    [GeneratedRegex(@"^ep(\d+)$", RegexOptions.IgnoreCase)]
    private static partial Regex StandaloneBangumiEpRegex();

    [GeneratedRegex(@"^ss(\d+)$", RegexOptions.IgnoreCase)]
    private static partial Regex StandaloneBangumiSeasonRegex();

    [GeneratedRegex(@"^md(\d+)$", RegexOptions.IgnoreCase)]
    private static partial Regex StandaloneBangumiMediaRegex();

    [GeneratedRegex(@"^/video/(BV[0-9A-Za-z]{10}|av\d+)(?:/|$)", RegexOptions.IgnoreCase)]
    private static partial Regex VideoPathRegex();

    [GeneratedRegex(@"^/read/cv(\d+)(?:/|$)", RegexOptions.IgnoreCase)]
    private static partial Regex ArticleCvidPathRegex();

    [GeneratedRegex(@"^/opus/(\d+)(?:/|$)", RegexOptions.IgnoreCase)]
    private static partial Regex ArticleOpusPathRegex();

    [GeneratedRegex(@"^/(?:blanc/)?(\d+)(?:/|$)", RegexOptions.IgnoreCase)]
    private static partial Regex LiveRoomPathRegex();

    [GeneratedRegex(@"^/bangumi/play/ep(\d+)(?:/|$)", RegexOptions.IgnoreCase)]
    private static partial Regex BangumiEpisodePathRegex();

    [GeneratedRegex(@"^/bangumi/play/ss(\d+)(?:/|$)", RegexOptions.IgnoreCase)]
    private static partial Regex BangumiSeasonPathRegex();

    [GeneratedRegex(@"^/bangumi/media/md(\d+)(?:/|$)", RegexOptions.IgnoreCase)]
    private static partial Regex BangumiMediaPathRegex();
}

public sealed record BilibiliBangumiIds(long? EpId, long? SeasonId, long? MediaId)
{
    public bool HasAny => EpId is not null || SeasonId is not null || MediaId is not null;
}
