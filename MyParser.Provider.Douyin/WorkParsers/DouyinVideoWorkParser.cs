using MyParser.Provider.Douyin.Models;
using System.Text.Json;
using MyParser.Provider.Douyin.Abstractions;
using static MyParser.Provider.Douyin.Utilities.DouyinParseHelpers;

namespace MyParser.Provider.Douyin.WorkParsers;

public sealed class DouyinVideoWorkParser(PluginConfig config) : IDouyinWorkParser
{
    public bool CanParse(JsonElement aweme)
    {
        return TryGetProperty(aweme, "video", out var video)
               && video.ValueKind == JsonValueKind.Object
               && HasPlayableVideo(video);
    }

    private static bool HasPlayableVideo(JsonElement video)
    {
        if (TryGetProperty(video, "bit_rate", out var bitRates) && bitRates.ValueKind == JsonValueKind.Array)
        {
            foreach (var bitRate in bitRates.EnumerateArray())
            {
                if (TryGetProperty(bitRate, "play_addr", out var playAddr) && EnumerateUrlList(playAddr).Any())
                {
                    return true;
                }
            }
        }

        if (TryGetProperty(video, "play_addr", out var fallbackPlayAddr))
        {
            return EnumerateUrlList(fallbackPlayAddr).Any() || !string.IsNullOrWhiteSpace(GetString(fallbackPlayAddr, "uri"));
        }

        return false;
    }

    public DouyinParseResult Parse(JsonElement aweme, string fallbackAwemeId, string sourceUrl)
    {
        var awemeId = GetString(aweme, "aweme_id") ?? fallbackAwemeId;
        var title = GetString(aweme, "desc");
        var author = TryGetProperty(aweme, "author", out var authorEl) ? authorEl : default;
        var video = TryGetProperty(aweme, "video", out var videoEl) ? videoEl : default;
        var qualities = ExtractQualities(video);
        var coverUrl = ExtractSimpleCoverUrl(video);
        var musicEl = TryGetProperty(aweme, "music", out var music) ? music : default;
        var musicUrl = musicEl.ValueKind == JsonValueKind.Object ? ExtractFirstUrl(musicEl, "play_url") : null;

        return new DouyinParseResult
        {
            AwemeId = awemeId,
            SourceUrl = sourceUrl,
            Title = title,
            AuthorName = author.ValueKind == JsonValueKind.Object ? GetString(author, "nickname") : null,
            AuthorId = author.ValueKind == JsonValueKind.Object ? GetString(author, "sec_uid") ?? GetString(author, "unique_id") : null,
            AuthorAvatarUrl = author.ValueKind == JsonValueKind.Object ? ExtractAuthorAvatarUrl(author) : null,
            AuthorFollowerCount = author.ValueKind == JsonValueKind.Object ? GetLong(author, "follower_count") : 0,
            AuthorRegion = author.ValueKind == JsonValueKind.Object ? GetString(author, "region") ?? GetString(author, "ip_location") : null,
            DurationMilliseconds = video.ValueKind == JsonValueKind.Object ? GetLong(video, "duration") : 0,
            CreateTimeUnixSeconds = GetFirstLong(aweme, "create_time", "createTime"),
            CoverUrl = coverUrl,
            CoverSource = "detail",
            VideoUrl = qualities.FirstOrDefault()?.Url,
            MusicUrl = musicUrl,
            MusicTitle = musicEl.ValueKind == JsonValueKind.Object ? GetString(musicEl, "title") : null,
            MusicAuthor = musicEl.ValueKind == JsonValueKind.Object ? GetString(musicEl, "author") : null,
            LikeCount = GetStatisticLong(aweme, "digg_count", "diggCount"),
            CollectCount = GetStatisticLong(aweme, "collect_count", "collectCount"),
            CommentCount = GetStatisticLong(aweme, "comment_count", "commentCount"),
            ShareCount = GetStatisticLong(aweme, "share_count", "shareCount"),
            Tags = ExtractTags(aweme),
            Qualities = qualities,
            Images = [],
        };
    }

    private List<DouyinVideoQuality> ExtractQualities(JsonElement video)
    {
        var result = new List<DouyinVideoQuality>();
        if (video.ValueKind != JsonValueKind.Object)
        {
            return result;
        }

        if (TryGetProperty(video, "bit_rate", out var bitRates) && bitRates.ValueKind == JsonValueKind.Array)
        {
            foreach (var bitRate in bitRates.EnumerateArray())
            {
                var rate = GetInt(bitRate, "bit_rate");
                var gear = GetString(bitRate, "gear_name") ?? string.Empty;
                var fps = GetInt(bitRate, "FPS");
                var isByteVc1 = GetBool(bitRate, "is_bytevc1") || GetBool(bitRate, "is_h265");
                var codec = isByteVc1 ? "h265" : "h264";
                if (!TryGetProperty(bitRate, "play_addr", out var playAddr))
                {
                    continue;
                }

                var width = GetInt(playAddr, "width");
                var height = GetInt(playAddr, "height");
                var ratio = GuessRatio(gear, rate, width, height);
                foreach (var url in EnumerateUrlList(playAddr))
                {
                    AddQuality(result, NormalizeNoWatermarkUrl(url), GetString(playAddr, "uri"), ratio, rate, gear, fps, width, height, codec, isByteVc1);
                }
            }
        }

        if (result.Count == 0 && TryGetProperty(video, "play_addr", out var playAddrFallback))
        {
            foreach (var url in EnumerateUrlList(playAddrFallback))
            {
                var width = GetInt(video, "width");
                var height = GetInt(video, "height");
                AddQuality(result, NormalizeNoWatermarkUrl(url), GetString(playAddrFallback, "uri"), GuessRatio(string.Empty, 0, width, height), 0, string.Empty, 0, width, height, string.Empty, false);
            }

            var uri = GetString(playAddrFallback, "uri");
            if (result.Count == 0 && !string.IsNullOrWhiteSpace(uri))
            {
                foreach (var ratio in new[] { "1080p", "720p", "540p", "480p", "360p" })
                {
                    AddQuality(result, $"https://aweme.snssdk.com/aweme/v1/play/?video_id={Uri.EscapeDataString(uri)}&ratio={ratio}&line=0", uri, ratio, 0, string.Empty, 0, 0, 0, string.Empty, false);
                }
            }
        }

        return result
            .GroupBy(i => i.Url)
            .Select(g => g.First())
            .OrderByDescending(QualityScore)
            .ThenByDescending(i => i.BitRate)
            .ToList();
    }

    private void AddQuality(
        List<DouyinVideoQuality> result,
        string? url,
        string? uri,
        string ratio,
        int bitRate,
        string gearName,
        int fps,
        int width,
        int height,
        string codec,
        bool isByteVc1)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return;
        }

        var labelParts = new List<string> { ratio };
        if (fps > 0) labelParts.Add($"{fps}fps");
        if (bitRate > 0) labelParts.Add($"{bitRate / 1000}Kbps");
        if (!string.IsNullOrWhiteSpace(codec)) labelParts.Add(codec);
        if (!string.IsNullOrWhiteSpace(gearName)) labelParts.Add(gearName);

        result.Add(new DouyinVideoQuality
        {
            Url = url,
            Uri = uri,
            Ratio = ratio,
            BitRate = bitRate,
            Fps = fps,
            Width = width,
            Height = height,
            Codec = codec,
            GearName = gearName,
            IsByteVc1 = isByteVc1,
            Label = string.Join(" ", labelParts.Where(i => !string.IsNullOrWhiteSpace(i))),
        });
    }

    private long QualityScore(DouyinVideoQuality quality)
    {
        var width = Math.Max(quality.Width, quality.Height);
        var height = Math.Min(quality.Width, quality.Height);
        var pixels = width > 0 && height > 0 ? width * height : RatioRank(quality.Ratio) * 400_000;
        var fps = quality.Fps > 0 ? quality.Fps : 30;
        var codecBonus = IsPreferredCodec(quality) ? 500_000_000L : 0;
        var fpsScore = config.PreferHighFps ? fps * 10_000_000L : 0;
        return pixels * 1000L + fpsScore + quality.BitRate + codecBonus;
    }

    private bool IsPreferredCodec(DouyinVideoQuality quality)
    {
        return config.PreferredVideoCodec switch
        {
            PreferredVideoCodec.H264 => string.Equals(quality.Codec, "h264", StringComparison.OrdinalIgnoreCase),
            PreferredVideoCodec.H265 => quality.IsByteVc1 || string.Equals(quality.Codec, "h265", StringComparison.OrdinalIgnoreCase),
            PreferredVideoCodec.AV1 => string.Equals(quality.Codec, "av1", StringComparison.OrdinalIgnoreCase),
            _ => quality.IsByteVc1
        };
    }
}
