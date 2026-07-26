using System.Net;
using System.Text.Json;
using Shirobot.Plugin.MyParser.Providers.Bilibili.Facade;
using Shirobot.Plugin.MyParser.Providers.Bilibili.Infrastructure;
using Shirobot.Plugin.MyParser.Providers.Bilibili.Models;
using Shirobot.Plugin.MyParser.Providers.Bilibili.Utilities;

namespace Shirobot.Plugin.MyParser.Providers.Bilibili.Impl.Services;

internal sealed class BilibiliBangumiParser(HttpClient http, PluginConfig config)
{
    private const string PgcSeasonApi = "https://api.bilibili.com/pgc/view/web/season";
    private const string PgcMediaApi = "https://api.bilibili.com/pgc/view/web/media";
    private const string PgcPlayUrlApi = "https://api.bilibili.com/pgc/player/web/v2/playurl";

    private static readonly Dictionary<int, string> QualityNames = new()
    {
        [6] = "240P 极速",
        [16] = "360P 流畅",
        [32] = "480P 清晰",
        [64] = "720P 高清",
        [74] = "720P60 高帧率",
        [80] = "1080P 高清",
        [112] = "1080P+ 高码率",
        [116] = "1080P60 高帧率",
        [120] = "4K 超清",
        [125] = "HDR 真彩色",
        [126] = "杜比视界",
        [127] = "8K 超高清",
    };

    private static readonly Dictionary<int, string> CodecNames = new()
    {
        [7] = "AVC/H.264",
        [12] = "HEVC/H.265",
        [13] = "AV1",
    };

    public async Task<object> ParseAsync(string text, CancellationToken cancellationToken = default)
    {
        var ids = BilibiliUrlParser.ExtractBangumiIds(text);
        if (!ids.HasAny)
        {
            throw new BilibiliParseException("无法从输入中提取番剧 md/ss/ep ID。");
        }

        JsonElement? media = null;
        var seasonId = ids.SeasonId;
        if (ids.MediaId is { } mediaId)
        {
            using var mediaJson = await GetJsonDocumentAsync(PgcMediaApi, new Dictionary<string, string> { ["media_id"] = mediaId.ToString() }, cancellationToken);
            media = (mediaJson.RootElement.GetPropertyOrDefault("result") ?? mediaJson.RootElement.GetPropertyOrDefault("data"))?.Clone();
            seasonId = NullIfZero(media?.GetInt64OrDefault("season_id") ?? 0) ?? seasonId;
        }

        var parameters = new Dictionary<string, string>();
        if (ids.EpId is { } epId)
        {
            parameters["ep_id"] = epId.ToString();
        }
        else if (seasonId is { } sid)
        {
            parameters["season_id"] = sid.ToString();
        }
        else
        {
            throw new BilibiliParseException("无法通过 media_id 获取番剧 season_id。");
        }

        using var seasonJson = await GetJsonDocumentAsync(PgcSeasonApi, parameters, cancellationToken);
        var season = (seasonJson.RootElement.GetPropertyOrDefault("result") ?? seasonJson.RootElement.GetPropertyOrDefault("data"))
                     ?? throw new BilibiliParseException("番剧 season 接口未返回 result/data。");
        var episodes = season.GetPropertyOrDefault("episodes").EnumerateArrayOrEmpty().ToArray();
        if (episodes.Length == 0)
        {
            throw new BilibiliParseException("番剧没有可用剧集列表。");
        }

        var stat = season.GetPropertyOrDefault("stat") ?? media?.GetPropertyOrDefault("stat");
        var publish = season.GetPropertyOrDefault("publish") ?? media?.GetPropertyOrDefault("publish");
        var rating = season.GetPropertyOrDefault("rating") ?? media?.GetPropertyOrDefault("rating");
        var styles = season.GetPropertyOrDefault("styles").EnumerateArrayOrEmpty()
            .Select(ReadStyleName)
            .Where(i => !string.IsNullOrWhiteSpace(i))
            .Cast<string>()
            .ToList();
        if (styles.Count == 0 && media is not null)
        {
            styles = media.Value.GetPropertyOrDefault("styles").EnumerateArrayOrEmpty()
                .Select(ReadStyleName)
                .Where(i => !string.IsNullOrWhiteSpace(i))
                .Cast<string>()
                .ToList();
        }

        var resultMediaId = NullIfZero(season.GetInt64OrDefault("media_id"))
                            ?? NullIfZero(media?.GetInt64OrDefault("media_id") ?? 0)
                            ?? ids.MediaId;
        var resultSeasonId = NullIfZero(season.GetInt64OrDefault("season_id")) ?? seasonId;
        var bangumi = new BilibiliBangumiParseResult
        {
            MediaId = resultMediaId,
            SeasonId = resultSeasonId,
            RequestedEpId = ids.EpId,
            Title = FirstNonEmpty(season.GetStringOrDefault("title"), media?.GetStringOrDefault("title"), season.GetStringOrDefault("season_title")),
            CoverUrl = FirstNonEmpty(season.GetStringOrDefault("cover"), season.GetStringOrDefault("square_cover"), media?.GetStringOrDefault("cover"), episodes[0].GetStringOrDefault("cover")),
            Evaluate = FirstNonEmpty(season.GetStringOrDefault("evaluate"), media?.GetStringOrDefault("evaluate")),
            MediaUrl = resultMediaId is null ? null : $"https://www.bilibili.com/bangumi/media/md{resultMediaId}",
            SeasonUrl = resultSeasonId is null ? null : $"https://www.bilibili.com/bangumi/play/ss{resultSeasonId}",
            PublishText = BuildPublishText(publish, episodes.Length),
            RatingText = BuildRatingText(rating),
            PlayText = FormatCountText(FirstPositive(stat?.GetInt64OrDefault("views") ?? 0, stat?.GetInt64OrDefault("view") ?? 0), "播放"),
            FollowText = FormatCountText(FirstPositive(stat?.GetInt64OrDefault("favorites") ?? 0, stat?.GetInt64OrDefault("follow") ?? 0), "追番"),
            Styles = styles,
            Episodes = episodes.Select((item, index) => new BilibiliBangumiEpisodeInfo
            {
                Index = index + 1,
                EpId = FirstPositive(item.GetInt64OrDefault("id"), item.GetInt64OrDefault("ep_id")),
                Cid = item.GetInt64OrDefault("cid"),
                Aid = item.GetInt64OrDefault("aid"),
                Bvid = item.GetStringOrDefault("bvid"),
                Title = item.GetStringOrDefault("title"),
                LongTitle = item.GetStringOrDefault("long_title"),
                CoverUrl = item.GetStringOrDefault("cover"),
                DurationMilliseconds = item.GetInt64OrDefault("duration"),
            }).ToList(),
        };

        if (ids.EpId is { } requestedEpId)
        {
            return new BilibiliBangumiEpisodeVideoParseResult
            {
                Bangumi = bangumi,
                Video = await BuildEpisodeVideoResultAsync(bangumi, requestedEpId, stat, cancellationToken),
            };
        }

        return bangumi;
    }

    private async Task<BilibiliParseResult> BuildEpisodeVideoResultAsync(BilibiliBangumiParseResult bangumi, long requestedEpId, JsonElement? stat, CancellationToken cancellationToken)
    {
        var episode = bangumi.Episodes.FirstOrDefault(i => i.EpId == requestedEpId)
                      ?? throw new BilibiliParseException($"未在番剧剧集列表中找到 ep{requestedEpId}。");
        if (episode.EpId is null || episode.Cid is null or <= 0)
        {
            throw new BilibiliParseException("番剧单集缺少 ep_id/cid，无法解析视频。");
        }

        var playInfo = await GetPgcPlayUrlAsync(episode.EpId.Value, cancellationToken);
        var videos = ParseVideoStreams(playInfo).OrderByDescending(GetVideoScore).ToList();
        var audios = ParseAudioStreams(playInfo).OrderByDescending(i => i.Bandwidth).ToList();
        if (videos.Count == 0 || audios.Count == 0)
        {
            throw new BilibiliParseException("没有获取到可用的番剧 DASH 视频/音频流；请确认 BilibiliCookie 登录有效。 ");
        }

        var episodeTitle = string.Join(" ", new[] { episode.Title, episode.LongTitle }.Where(i => !string.IsNullOrWhiteSpace(i)));
        var title = string.IsNullOrWhiteSpace(episodeTitle) ? bangumi.Title : $"{bangumi.Title} - {episodeTitle}";
        return new BilibiliParseResult
        {
            Bvid = string.IsNullOrWhiteSpace(episode.Bvid) ? $"ep{episode.EpId}" : episode.Bvid!,
            Aid = episode.Aid,
            Cid = episode.Cid.Value,
            Page = episode.Index,
            SourceUrl = episode.Url,
            Title = title,
            PartTitle = episodeTitle,
            Description = bangumi.Evaluate,
            AuthorName = "Bilibili 番剧",
            AuthorId = bangumi.MediaId?.ToString(),
            AuthorAvatarUrl = null,
            CoverUrl = FirstNonEmpty(episode.CoverUrl, bangumi.CoverUrl),
            DurationSeconds = episode.DurationMilliseconds > 0 ? episode.DurationMilliseconds / 1000 : 0,
            ViewCount = FirstPositive(stat?.GetInt64OrDefault("views") ?? 0, stat?.GetInt64OrDefault("view") ?? 0) ?? 0,
            LikeCount = 0,
            CoinCount = 0,
            FavoriteCount = FirstPositive(stat?.GetInt64OrDefault("favorites") ?? 0, stat?.GetInt64OrDefault("follow") ?? 0) ?? 0,
            ShareCount = 0,
            ReplyCount = 0,
            VideoStreams = videos,
            AudioStreams = audios,
        };
    }

    private async Task<JsonElement> GetPgcPlayUrlAsync(long epId, CancellationToken cancellationToken)
    {
        using var json = await GetJsonDocumentAsync(PgcPlayUrlApi, new Dictionary<string, string>
        {
            ["ep_id"] = epId.ToString(),
            ["qn"] = "127",
            ["fnver"] = "0",
            ["fnval"] = "12240",
            ["fourk"] = "1",
            ["from_client"] = "BROWSER",
        }, cancellationToken);
        var result = json.RootElement.GetPropertyOrDefault("result")
                     ?? json.RootElement.GetPropertyOrDefault("data")?.GetPropertyOrDefault("result")
                     ?? json.RootElement.GetPropertyOrDefault("data");
        return result?.GetPropertyOrDefault("video_info")?.Clone() ?? result?.Clone() ?? throw new BilibiliParseException("番剧 playurl 接口未返回 video_info。");
    }

    private async Task<JsonDocument> GetJsonDocumentAsync(string url, Dictionary<string, string> parameters, CancellationToken cancellationToken)
    {
        var uri = url + "?" + string.Join("&", parameters.Select(i => $"{WebUtility.UrlEncode(i.Key)}={WebUtility.UrlEncode(i.Value)}"));
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.TryAddWithoutValidation("User-Agent", BilibiliConstants.UserAgent);
        request.Headers.TryAddWithoutValidation("Referer", "https://www.bilibili.com/bangumi/");
        request.Headers.TryAddWithoutValidation("Origin", BilibiliConstants.Origin);
        request.Headers.TryAddWithoutValidation("Accept", "application/json, text/plain, */*");
        if (!string.IsNullOrWhiteSpace(MyParserRuntime.BilibiliCookie))
        {
            request.Headers.TryAddWithoutValidation("Cookie", MyParserRuntime.BilibiliCookie);
        }

        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var code = json.RootElement.GetInt32OrDefault("code");
        if (code != 0)
        {
            throw new BilibiliParseException($"B站番剧接口错误 {code}: {json.RootElement.GetStringOrDefault("message")}");
        }

        return JsonDocument.Parse(json.RootElement.GetRawText());
    }

    private List<BilibiliMediaStream> ParseVideoStreams(JsonElement playInfo)
    {
        var dash = playInfo.GetPropertyOrDefault("dash");
        var videos = dash?.GetPropertyOrDefault("video").EnumerateArrayOrEmpty() ?? [];
        var result = new List<BilibiliMediaStream>();
        var index = 0;
        foreach (var item in videos)
        {
            var url = item.GetStringOrDefault("baseUrl") ?? item.GetStringOrDefault("base_url") ?? string.Empty;
            var qualityId = item.GetInt32OrDefault("id");
            var codecId = item.GetInt32OrDefault("codecid");
            result.Add(new BilibiliMediaStream
            {
                StreamId = $"pgcv{index++}",
                Url = url,
                BackupUrls = item.GetStringArrayOrEmpty("backupUrl", "backup_url"),
                QualityId = qualityId,
                QualityName = QualityNames.GetValueOrDefault(qualityId, qualityId.ToString()),
                Width = item.GetInt32OrDefault("width"),
                Height = item.GetInt32OrDefault("height"),
                Fps = ParseFps(item.GetStringOrDefault("frameRate") ?? item.GetStringOrDefault("frame_rate")),
                Bandwidth = item.GetInt32OrDefault("bandwidth"),
                CodecId = codecId,
                CodecName = CodecNames.GetValueOrDefault(codecId, item.GetStringOrDefault("codecs") ?? "未知编码"),
                Codecs = item.GetStringOrDefault("codecs") ?? string.Empty,
                MimeType = item.GetStringOrDefault("mimeType") ?? item.GetStringOrDefault("mime_type") ?? string.Empty,
                IsAudio = false,
            });
        }

        return result;
    }

    private static List<BilibiliMediaStream> ParseAudioStreams(JsonElement playInfo)
    {
        var result = new List<BilibiliMediaStream>();
        var index = 0;
        var dash = playInfo.GetPropertyOrDefault("dash");
        var source = new List<JsonElement>();
        source.AddRange(dash?.GetPropertyOrDefault("audio").EnumerateArrayOrEmpty() ?? []);
        var flac = dash?.GetPropertyOrDefault("flac")?.GetPropertyOrDefault("audio");
        if (flac is { ValueKind: JsonValueKind.Object })
        {
            source.Add(flac.Value);
        }

        source.AddRange(dash?.GetPropertyOrDefault("dolby")?.GetPropertyOrDefault("audio").EnumerateArrayOrEmpty() ?? []);
        foreach (var item in source)
        {
            var url = item.GetStringOrDefault("baseUrl") ?? item.GetStringOrDefault("base_url") ?? string.Empty;
            result.Add(new BilibiliMediaStream
            {
                StreamId = $"pgca{index++}",
                Url = url,
                BackupUrls = item.GetStringArrayOrEmpty("backupUrl", "backup_url"),
                QualityId = item.GetInt32OrDefault("id"),
                QualityName = "音频",
                Bandwidth = item.GetInt32OrDefault("bandwidth"),
                Codecs = item.GetStringOrDefault("codecs") ?? string.Empty,
                MimeType = item.GetStringOrDefault("mimeType") ?? item.GetStringOrDefault("mime_type") ?? string.Empty,
                IsAudio = true,
            });
        }

        return result;
    }

    private long GetVideoScore(BilibiliMediaStream stream)
    {
        var codecScore = stream.CodecId == GetPreferredCodecId() ? 1_000_000L : 0L;
        var fpsScore = config.PreferHighFps ? (long)(stream.Fps * 1_000) : 0;
        return stream.QualityId * 10_000_000L + codecScore + stream.Width * stream.Height + fpsScore + stream.Bandwidth / 1_000;
    }

    private int GetPreferredCodecId()
    {
        return config.PreferredVideoCodec switch
        {
            PreferredVideoCodec.H264 => 7,
            PreferredVideoCodec.H265 => 12,
            PreferredVideoCodec.AV1 => 13,
            _ => 12
        };
    }

    private static double ParseFps(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return 0;
        if (value.Contains('/'))
        {
            var parts = value.Split('/', 2);
            return double.TryParse(parts[0], out var a) && double.TryParse(parts[1], out var b) && b != 0 ? Math.Round(a / b, 3) : 0;
        }

        return double.TryParse(value, out var fps) ? Math.Round(fps, 3) : 0;
    }

    private static string? ReadStyleName(JsonElement item)
    {
        return item.ValueKind == JsonValueKind.Object ? item.GetStringOrDefault("name") : item.GetString();
    }

    private static long? NullIfZero(long value) => value > 0 ? value : null;

    private static long? FirstPositive(params long[] values)
    {
        return values.FirstOrDefault(i => i > 0) is var value && value > 0 ? value : null;
    }

    private static string? GetScoreText(JsonElement? value)
    {
        return value?.ValueKind switch
        {
            JsonValueKind.String => value.Value.GetString(),
            JsonValueKind.Number when value.Value.TryGetDouble(out var n) => n.ToString("0.0"),
            _ => null,
        };
    }

    private static string? BuildRatingText(JsonElement? rating)
    {
        if (rating is not { ValueKind: JsonValueKind.Object }) return null;
        var score = GetScoreText(rating.Value.GetPropertyOrDefault("score"));
        var count = rating.Value.GetInt64OrDefault("count");
        if (string.IsNullOrWhiteSpace(score)) return null;
        return count > 0 ? $"评分 {score} / {FormatCount(count)}人" : $"评分 {score}";
    }

    private static string BuildPublishText(JsonElement? publish, int episodeCount)
    {
        var isFinish = publish?.GetInt32OrDefault("is_finish") == 1;
        var countText = $"全{episodeCount}话";
        return isFinish ? $"已完结, {countText}" : $"连载中, {countText}";
    }

    private static string? FormatCountText(long? value, string label)
    {
        return value is > 0 ? $"{label} {FormatCount(value.Value)}" : null;
    }

    private static string FormatCount(long value)
    {
        return value >= 100_000_000 ? $"{value / 100_000_000d:0.#}亿" : value >= 10_000 ? $"{value / 10_000d:0.#}万" : value.ToString();
    }

    private static string? FirstNonEmpty(params string?[] values)
    {
        return values.FirstOrDefault(i => !string.IsNullOrWhiteSpace(i));
    }
}
