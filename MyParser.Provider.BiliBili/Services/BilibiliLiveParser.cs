using System.Net;
using System.Text.Json;
using MyParser.Provider.BiliBili.Parsing;
using MyParser.Provider.BiliBili.Infrastructure;
using MyParser.Provider.BiliBili.Models;
using MyParser.Provider.BiliBili.Utilities;

namespace MyParser.Provider.BiliBili.Services;

public sealed class BilibiliLiveParser(HttpClient http, PluginConfig config)
{
    private const string RoomInitApi = "https://api.live.bilibili.com/room/v1/Room/room_init";
    private const string RoomInfoApi = "https://api.live.bilibili.com/xlive/web-room/v1/index/getInfoByRoom";
    private const string RoomPlayInfoApi = "https://api.live.bilibili.com/xlive/web-room/v2/index/getRoomPlayInfo";

    public async Task<BilibiliLiveParseResult> ParseAsync(string text, CancellationToken cancellationToken = default)
    {
        var roomId = BilibiliUrlParser.ExtractLiveRoomId(text);
        if (roomId is null && BilibiliUrlParser.ExtractB23Url(text) is { } shortUrl)
        {
            var resolved = await new BilibiliParser(config, http).ResolveBilibiliRedirectUrlAsync(shortUrl, cancellationToken);
            roomId = BilibiliUrlParser.ExtractLiveRoomId(resolved);
        }

        if (string.IsNullOrWhiteSpace(roomId))
        {
            throw new BilibiliParseException("无法从输入中提取 Bilibili 直播间号。");
        }

        using var roomJson = await GetJsonDocumentAsync(RoomInitApi, new Dictionary<string, string> { ["id"] = roomId }, $"https://live.bilibili.com/{roomId}", cancellationToken);
        var roomData = roomJson.RootElement.GetPropertyOrDefault("data") ?? throw new BilibiliParseException("B站直播 room_init 接口未返回 data。");
        var realRoomId = roomData.GetInt64OrDefault("room_id").ToString();
        if (string.IsNullOrWhiteSpace(realRoomId) || realRoomId == "0")
        {
            realRoomId = roomId;
        }

        var liveStatus = roomData.GetInt32OrDefault("live_status");
        JsonElement? infoData = null;
        try
        {
            using var infoJson = await GetJsonDocumentAsync(RoomInfoApi, new Dictionary<string, string> { ["room_id"] = realRoomId }, $"https://live.bilibili.com/{realRoomId}", cancellationToken);
            infoData = infoJson.RootElement.GetPropertyOrDefault("data")?.Clone();
        }
        catch
        {
            // getRoomPlayInfo is enough for playback; room info/cover is best-effort.
        }

        using var playJson = await GetJsonDocumentAsync(RoomPlayInfoApi, new Dictionary<string, string>
        {
            ["room_id"] = realRoomId,
            ["no_playurl"] = "0",
            ["mask"] = "1",
            ["qn"] = "10000",
            ["platform"] = "web",
            ["protocol"] = "0,1",
            ["format"] = "0,1,2",
            ["codec"] = "0,1,2",
            ["dolby"] = "5",
            ["panorama"] = "1",
        }, $"https://live.bilibili.com/{realRoomId}", cancellationToken);

        var data = playJson.RootElement.GetPropertyOrDefault("data") ?? throw new BilibiliParseException("B站直播播放接口未返回 data。");
        var playRoomInfo = data.GetPropertyOrDefault("room_info");
        var infoRoomInfo = infoData?.GetPropertyOrDefault("room_info");
        var anchorInfo = infoData?.GetPropertyOrDefault("anchor_info")?.GetPropertyOrDefault("base_info");
        var streams = ParseStreams(data);
        var watchedShow = infoData?.GetPropertyOrDefault("watched_show");
        var audienceEntry = infoData?
            .GetPropertyOrDefault("room_rank_info")
            ?.GetPropertyOrDefault("user_rank_entry")
            ?.GetPropertyOrDefault("user_contribution_rank_entry");
        var liveStartTs = Math.Max(infoRoomInfo?.GetInt64OrDefault("live_start_time") ?? 0, roomData.GetInt64OrDefault("live_time"));
        var liveStartTime = liveStartTs > 0 ? DateTimeOffset.FromUnixTimeSeconds(liveStartTs).ToLocalTime() : (DateTimeOffset?)null;
        var now = DateTimeOffset.Now;

        return new BilibiliLiveParseResult
        {
            RoomId = roomId,
            RealRoomId = realRoomId,
            SourceUrl = $"https://live.bilibili.com/{realRoomId}",
            LiveStatus = liveStatus == 0 ? infoRoomInfo?.GetInt32OrDefault("live_status") ?? 0 : liveStatus,
            Title = FirstNonEmpty(infoRoomInfo?.GetStringOrDefault("title"), playRoomInfo?.GetStringOrDefault("title")),
            AnchorName = FirstNonEmpty(anchorInfo?.GetStringOrDefault("uname"), playRoomInfo?.GetPropertyOrDefault("anchor_info")?.GetPropertyOrDefault("base_info")?.GetStringOrDefault("uname")),
            AnchorAvatarUrl = FirstNonEmpty(anchorInfo?.GetStringOrDefault("face"), playRoomInfo?.GetPropertyOrDefault("anchor_info")?.GetPropertyOrDefault("base_info")?.GetStringOrDefault("face")),
            CoverUrl = FirstNonEmpty(
                infoRoomInfo?.GetStringOrDefault("cover"),
                infoRoomInfo?.GetStringOrDefault("user_cover"),
                infoRoomInfo?.GetStringOrDefault("keyframe"),
                playRoomInfo?.GetStringOrDefault("cover"),
                playRoomInfo?.GetStringOrDefault("user_cover"),
                playRoomInfo?.GetStringOrDefault("keyframe")),
            OnlineCount = infoRoomInfo?.GetInt64OrDefault("online") ?? 0,
            RoomAudienceCount = audienceEntry?.GetInt64OrDefault("count") ?? 0,
            RoomAudienceText = audienceEntry?.GetStringOrDefault("count_text"),
            WatchedCount = watchedShow?.GetInt64OrDefault("num") ?? 0,
            WatchedText = FirstNonEmpty(watchedShow?.GetStringOrDefault("text_large"), watchedShow?.GetStringOrDefault("text_small")),
            LiveStartTime = liveStartTime,
            LiveDuration = liveStartTime is not null && liveStatus == 1 && now > liveStartTime.Value ? now - liveStartTime.Value : null,
            Streams = streams,
        };
    }

    private static List<BilibiliLiveStream> ParseStreams(JsonElement data)
    {
        var result = new List<BilibiliLiveStream>();
        var playurl = data.GetPropertyOrDefault("playurl_info")?.GetPropertyOrDefault("playurl");
        var streamArray = playurl?.GetPropertyOrDefault("stream").EnumerateArrayOrEmpty() ?? [];
        foreach (var stream in streamArray)
        {
            var protocol = stream.GetStringOrDefault("protocol_name") ?? string.Empty;
            foreach (var format in stream.GetPropertyOrDefault("format").EnumerateArrayOrEmpty())
            {
                var formatName = format.GetStringOrDefault("format_name") ?? string.Empty;
                foreach (var codec in format.GetPropertyOrDefault("codec").EnumerateArrayOrEmpty())
                {
                    var codecName = codec.GetStringOrDefault("codec_name") ?? string.Empty;
                    var currentQn = codec.GetInt32OrDefault("current_qn");
                    var acceptQn = GetInt32ArrayOrEmpty(codec, "accept_qn");
                    var baseUrl = codec.GetStringOrDefault("base_url") ?? string.Empty;
                    var cdnIndex = 0;
                    foreach (var urlInfo in codec.GetPropertyOrDefault("url_info").EnumerateArrayOrEmpty())
                    {
                        cdnIndex++;
                        var url = (urlInfo.GetStringOrDefault("host") ?? string.Empty) + baseUrl + (urlInfo.GetStringOrDefault("extra") ?? string.Empty);
                        if (string.IsNullOrWhiteSpace(url))
                        {
                            continue;
                        }

                        result.Add(new BilibiliLiveStream
                        {
                            Protocol = protocol,
                            Format = formatName,
                            Codec = codecName,
                            CurrentQn = currentQn,
                            AcceptQn = acceptQn,
                            CdnIndex = cdnIndex,
                            Url = url,
                        });
                    }
                }
            }
        }

        return result.OrderBy(StreamRank).ThenByDescending(i => i.CurrentQn).ThenBy(i => i.CdnIndex).ToList();
    }

    private static string? FirstNonEmpty(params string?[] values)
    {
        return values.FirstOrDefault(i => !string.IsNullOrWhiteSpace(i));
    }

    private static List<int> GetInt32ArrayOrEmpty(JsonElement element, string name)
    {
        var value = element.GetPropertyOrDefault(name);
        if (value is not { ValueKind: JsonValueKind.Array })
        {
            return [];
        }

        var result = new List<int>();
        foreach (var item in value.Value.EnumerateArray())
        {
            switch (item.ValueKind)
            {
                case JsonValueKind.Number when item.TryGetInt32(out var n):
                    result.Add(n);
                    break;
                case JsonValueKind.String when int.TryParse(item.GetString(), out var n):
                    result.Add(n);
                    break;
            }
        }

        return result;
    }

    private static int StreamRank(BilibiliLiveStream stream)
    {
        return (stream.Protocol, stream.Format, stream.Codec) switch
        {
            ("http_hls", "fmp4", "avc") => 0,
            ("http_hls", "ts", "avc") => 1,
            ("http_stream", "flv", "avc") => 2,
            ("http_hls", "fmp4", "hevc") => 3,
            ("http_hls", "ts", "hevc") => 4,
            ("http_stream", "flv", "hevc") => 5,
            ("http_hls", "fmp4", "av1") => 6,
            _ => 99,
        };
    }

    private async Task<JsonDocument> GetJsonDocumentAsync(string url, Dictionary<string, string> parameters, string referer, CancellationToken cancellationToken)
    {
        var uri = url + "?" + string.Join("&", parameters.Select(i => $"{WebUtility.UrlEncode(i.Key)}={WebUtility.UrlEncode(i.Value)}"));
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        ApplyHeaders(request, referer);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var code = json.RootElement.GetInt32OrDefault("code");
        if (code != 0)
        {
            throw new BilibiliParseException($"B站直播接口错误 {code}: {json.RootElement.GetStringOrDefault("message")}");
        }

        return JsonDocument.Parse(json.RootElement.GetRawText());
    }

    private void ApplyHeaders(HttpRequestMessage request, string referer)
    {
        request.Headers.TryAddWithoutValidation("User-Agent", BilibiliConstants.UserAgent);
        request.Headers.TryAddWithoutValidation("Referer", referer);
        request.Headers.TryAddWithoutValidation("Origin", "https://live.bilibili.com");
        request.Headers.TryAddWithoutValidation("Accept", "application/json, text/plain, */*");
        if (!string.IsNullOrWhiteSpace(MyParserRuntime.BilibiliCookie))
        {
            request.Headers.TryAddWithoutValidation("Cookie", MyParserRuntime.BilibiliCookie);
        }
    }
}
