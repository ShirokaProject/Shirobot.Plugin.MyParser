using System.Diagnostics;
using System.Text.Json;
using MyParser.Provider.NetEaseCloudMusic.Infrastructure;
using ShiroBot.SDK.Abstractions;
using MyParser.Provider.NetEaseCloudMusic.Models;
using MyParser.Provider.NetEaseCloudMusic.Utilities;

namespace MyParser.Provider.NetEaseCloudMusic.Parsing;

public sealed class NetEaseParser : IParserHttpClientAccessor, IDisposable
{
    private const string SongUrlApi = "https://interface3.music.163.com/eapi/song/enhance/player/url/v1";
    // interface3 在部分网络环境下会提前断开连接；歌曲详情走网页版域名更稳定。
    private const string SongDetailApi = "https://music.163.com/api/v3/song/detail";
    private const string LyricApi = "https://music.163.com/api/song/lyric";
    private const string SearchApi = "https://music.163.com/api/cloudsearch/pc";
    private const string QrUnikeyApi = "https://interface3.music.163.com/eapi/login/qrcode/unikey";
    private const string QrLoginApi = "https://interface3.music.163.com/eapi/login/qrcode/client/login";
    private readonly NetEaseHttp _http;
    private readonly TimeSpan _apiTimeout;
    public HttpClient HttpClient => _http.Client;

    public NetEaseParser(PluginConfig config)
    {
        _apiTimeout = TimeSpan.FromSeconds(Math.Clamp(config.RequestTimeoutSeconds, 5, 300));
        _http = new NetEaseHttp(_apiTimeout);
    }

    public static bool LooksLikeCookie(string cookie) => !string.IsNullOrWhiteSpace(cookie)
        && (cookie.Contains("MUSIC_U=", StringComparison.OrdinalIgnoreCase)
            || cookie.Contains("__csrf=", StringComparison.OrdinalIgnoreCase)
            || cookie.Contains("NMTID=", StringComparison.OrdinalIgnoreCase));

    public Task<ProviderLoginStatus> CheckLoginStatusAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(MyParserRuntime.NetEaseCloudMusicCookie))
        {
            return Task.FromResult(new ProviderLoginStatus(false, null, null, "Cookie 为空；请编辑 cookies/netease.txt。无 Cookie 仍可搜索，VIP/高音质通常不可用。"));
        }
        return Task.FromResult(LooksLikeCookie(MyParserRuntime.NetEaseCloudMusicCookie)
            ? new ProviderLoginStatus(true, null, null, "Cookie 格式可用（未调用账号接口校验）。")
            : new ProviderLoginStatus(false, null, null, "Cookie 缺少 MUSIC_U/__csrf/NMTID 等关键字段。"));
    }

    public async Task<NetEaseQrLoginSession> GenerateQrLoginSessionAsync(CancellationToken cancellationToken = default)
    {
        var config = CreateEApiHeader();
        var payload = new
        {
            type = 1,
            header = JsonSerializer.Serialize(config, NetEaseJson.Options),
        };
        var encrypted = NetEaseCrypto.EncryptEApiParams(QrUnikeyApi, payload);
        var json = await _http.PostFormAsync(QrUnikeyApi, new Dictionary<string, string> { ["params"] = encrypted }, string.Empty, cancellationToken).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var code = root.TryGetProperty("code", out var codeElement) ? codeElement.GetInt32() : 0;
        if (code != 200)
        {
            throw new NetEaseParseException("生成网易云登录二维码失败：" + (GetString(root, "message") ?? code.ToString()));
        }

        var key = GetString(root, "unikey") ?? throw new NetEaseParseException("生成网易云登录二维码失败：响应缺少 unikey。");
        return new NetEaseQrLoginSession(key, $"https://music.163.com/login?codekey={Uri.EscapeDataString(key)}");
    }

    public async Task<NetEaseQrLoginPollResult> PollQrLoginAsync(string key, CancellationToken cancellationToken = default)
    {
        var config = CreateEApiHeader();
        var payload = new
        {
            key,
            type = 1,
            header = JsonSerializer.Serialize(config, NetEaseJson.Options),
        };
        var encrypted = NetEaseCrypto.EncryptEApiParams(QrLoginApi, payload);
        using var response = await _http.PostFormResponseAsync(QrLoginApi, new Dictionary<string, string> { ["params"] = encrypted }, string.Empty, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var code = root.TryGetProperty("code", out var codeElement) ? codeElement.GetInt32() : -1;
        var message = GetString(root, "message") ?? code switch
        {
            801 => "等待扫码",
            802 => "已扫码，等待确认",
            803 => "登录成功",
            800 => "二维码已过期",
            _ => "未知状态",
        };
        var cookie = code == 803 ? ExtractCookie(response) : null;
        if (!string.IsNullOrWhiteSpace(cookie))
        {
            MyParserRuntime.NetEaseCloudMusicCookie = cookie;
        }

        return new NetEaseQrLoginPollResult(code, message, code == 803, code == 800, code == 802, cookie);
    }

    public async Task<IReadOnlyList<NetEaseSearchSong>> SearchAsync(string keywords, int limit = 10, CancellationToken cancellationToken = default)
    {
        keywords = keywords.Trim();
        if (string.IsNullOrWhiteSpace(keywords)) throw new NetEaseParseException("搜索关键词不能为空。");
        limit = Math.Clamp(limit, 1, 20);
        var json = await _http.PostFormAsync(SearchApi, new Dictionary<string, string>
        {
            ["s"] = keywords, ["type"] = "1", ["limit"] = limit.ToString(),
        }, MyParserRuntime.NetEaseCloudMusicCookie, cancellationToken).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.TryGetProperty("code", out var code) && code.GetInt32() != 200)
        {
            throw new NetEaseParseException("搜索接口返回错误：" + code.GetInt32());
        }
        if (!root.TryGetProperty("result", out var result) || !result.TryGetProperty("songs", out var songs) || songs.ValueKind != JsonValueKind.Array) return [];
        var list = new List<NetEaseSearchSong>();
        foreach (var song in songs.EnumerateArray())
        {
            var id = GetInt64(song, "id");
            if (id <= 0) continue;
            var album = song.TryGetProperty("al", out var al) ? al : song.TryGetProperty("album", out var alb) ? alb : default;
            list.Add(new NetEaseSearchSong(
                id,
                GetString(song, "name") ?? id.ToString(),
                JoinNames(song, "ar") ?? JoinNames(song, "artists") ?? "未知歌手",
                album.ValueKind == JsonValueKind.Object ? GetString(album, "name") ?? "未知专辑" : "未知专辑",
                album.ValueKind == JsonValueKind.Object ? GetString(album, "picUrl") : null));
        }
        return list;
    }

    public async Task<NetEaseParseResult> ParseAsync(string text, CancellationToken cancellationToken = default)
    {
        if (NetEaseUrlParser.TryParseInternalPickUri(text, out var startIndex, out var songIds))
        {
            return await ParseFirstAvailableAsync(songIds, startIndex, cancellationToken).ConfigureAwait(false);
        }

        text = await ResolveShortUrlIfNeededAsync(text, cancellationToken).ConfigureAwait(false);
        var songId = NetEaseUrlParser.ExtractSongId(text) ?? throw new NetEaseParseException("无法从输入中提取网易云歌曲 ID。");
        return await ParseSongByIdAsync(songId, cancellationToken).ConfigureAwait(false);
    }

    private async Task<string> ResolveShortUrlIfNeededAsync(string text, CancellationToken cancellationToken)
    {
        var shortUrl = NetEaseUrlParser.ExtractShortUrl(text);
        if (string.IsNullOrWhiteSpace(shortUrl))
        {
            return text;
        }

        try
        {
            var resolved = await _http.ResolveRedirectUrlAsync(shortUrl, cancellationToken).ConfigureAwait(false);
            BotLog.Info($"MyParser 网易云音乐短链展开: {shortUrl} -> {resolved}");
            return string.IsNullOrWhiteSpace(resolved) ? text : resolved;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            BotLog.Warning($"MyParser 网易云音乐短链展开失败: {shortUrl}, error={ex.Message}");
            return text;
        }
    }

    private async Task<NetEaseParseResult> ParseFirstAvailableAsync(IReadOnlyList<long> songIds, int startIndex, CancellationToken cancellationToken)
    {
        Exception? lastError = null;
        for (var offset = 0; offset < songIds.Count; offset++)
        {
            var index = (startIndex + offset) % songIds.Count;
            var songId = songIds[index];
            try
            {
                if (offset > 0)
                {
                    BotLog.Info($"MyParser 网易云音乐候选回退: start_index={startIndex + 1}, try_index={index + 1}, song_id={songId}");
                }

                return await ParseSongByIdAsync(songId, cancellationToken).ConfigureAwait(false);
            }
            catch (NetEaseParseException ex) when (ex.Message.Contains("无法获取音频直链", StringComparison.OrdinalIgnoreCase))
            {
                lastError = ex;
            }
        }

        throw new NetEaseParseException($"候选列表均无法获取音频直链：{lastError?.Message ?? "未知错误"}");
    }

    private async Task<NetEaseParseResult> ParseSongByIdAsync(long songId, CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(_apiTimeout + TimeSpan.FromSeconds(8));
        var token = timeoutCts.Token;
        var detailTask = TimedAsync("歌曲详情", () => GetSongDetailAsync(songId, token));
        var lyricTask = TimedAsync("歌词", () => GetLyricAsync(songId, token));
        var urlTask = TimedAsync("播放链接", () => GetMp3SongUrlAsync(songId, token));
        await Task.WhenAll(detailTask, lyricTask, urlTask).ConfigureAwait(false);
        var detail = await detailTask.ConfigureAwait(false);
        var lyric = await lyricTask.ConfigureAwait(false);
        var url = await urlTask.ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(url.AudioUrl)) throw new NetEaseParseException("无法获取音频直链，可能是版权限制、Cookie 失效或音质不支持。");
        return detail with
        {
            AudioUrl = url.AudioUrl,
            Quality = url.Quality ?? "standard",
            FileType = url.FileType,
            FileSize = url.FileSize,
            Bitrate = url.Bitrate,
            Lyric = lyric.Lyric,
            TranslatedLyric = lyric.TranslatedLyric,
            SourceUrl = NetEaseUrlParser.BuildSongUrl(songId),
        };
    }

    private static object CreateEApiHeader()
    {
        return new
        {
            os = "pc",
            appver = "",
            osver = "",
            deviceId = "pyncm!",
            requestId = Random.Shared.Next(20000000, 30000000).ToString(),
        };
    }

    private static string? ExtractCookie(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("Set-Cookie", out var values))
        {
            return null;
        }

        var parts = new List<string>();
        foreach (var value in values)
        {
            foreach (var raw in value.Split(','))
            {
                var segment = raw.Trim();
                var first = segment.Split(';', 2)[0].Trim();
                if (string.IsNullOrWhiteSpace(first) || !first.Contains('='))
                {
                    continue;
                }

                if (first.StartsWith("MUSIC_U=", StringComparison.OrdinalIgnoreCase)
                    || first.StartsWith("__csrf=", StringComparison.OrdinalIgnoreCase)
                    || first.StartsWith("NMTID=", StringComparison.OrdinalIgnoreCase))
                {
                    parts.Add(first);
                }
            }
        }

        if (!parts.Any(i => i.StartsWith("os=", StringComparison.OrdinalIgnoreCase))) parts.Add("os=pc");
        if (!parts.Any(i => i.StartsWith("appver=", StringComparison.OrdinalIgnoreCase))) parts.Add("appver=8.9.70");
        return parts.Count == 0 ? null : string.Join("; ", parts.Distinct(StringComparer.OrdinalIgnoreCase));
    }

    private static async Task<T> TimedAsync<T>(string name, Func<Task<T>> action)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var result = await action().ConfigureAwait(false);
            BotLog.Info($"MyParser 网易云音乐接口完成: {name}, elapsed={stopwatch.Elapsed:mm\\:ss}");
            return result;
        }
        catch (Exception ex)
        {
            BotLog.Warning($"MyParser 网易云音乐接口失败: {name}, elapsed={stopwatch.Elapsed:mm\\:ss}, error={ex.GetType().Name}: {ex.Message}");
            throw;
        }
    }

    private async Task<NetEaseParseResult> GetSongDetailAsync(long songId, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Serialize(new[] { new { id = songId, v = 0 } }, NetEaseJson.Options);
        var json = await _http.PostFormAsync(SongDetailApi, new Dictionary<string, string> { ["c"] = payload }, MyParserRuntime.NetEaseCloudMusicCookie, cancellationToken).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (!root.TryGetProperty("songs", out var songs) || songs.ValueKind != JsonValueKind.Array || songs.GetArrayLength() == 0) throw new NetEaseParseException("未找到歌曲详情。");
        var song = songs[0];
        var album = song.GetProperty("al");
        return new NetEaseParseResult
        {
            SongId = songId,
            Title = GetString(song, "name") ?? songId.ToString(),
            Artists = JoinNames(song, "ar") ?? "未知歌手",
            Album = GetString(album, "name") ?? "未知专辑",
            CoverUrl = GetString(album, "picUrl"),
            SourceUrl = NetEaseUrlParser.BuildSongUrl(songId),
            AudioUrl = string.Empty,
        };
    }

    private async Task<(string? Lyric, string? TranslatedLyric)> GetLyricAsync(long songId, CancellationToken cancellationToken)
    {
        try
        {
            var json = await _http.PostFormAsync(LyricApi, new Dictionary<string, string>
            {
                ["id"] = songId.ToString(),
                ["cp"] = "false",
                ["tv"] = "0",
                ["lv"] = "0",
                ["rv"] = "0",
                ["kv"] = "0",
                ["yv"] = "0",
                ["ytv"] = "0",
                ["yrv"] = "0",
            }, MyParserRuntime.NetEaseCloudMusicCookie, cancellationToken).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            return (
                TryGetNestedString(root, "lrc", "lyric"),
                TryGetNestedString(root, "tlyric", "lyric"));
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
        {
            return (null, null);
        }
    }

    private async Task<(string AudioUrl, string? Quality, string? FileType, long? FileSize, int? Bitrate)> GetMp3SongUrlAsync(long songId, CancellationToken cancellationToken)
    {
        // QQ 语音侧优先使用 MP3。不要默认取 lossless/FLAC，否则 RecordSegment 或文件语音兼容性较差。
        foreach (var quality in new[] { "exhigh", "standard" })
        {
            var result = await GetSongUrlAsync(songId, quality, cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(result.AudioUrl)
                && string.Equals(result.FileType, "mp3", StringComparison.OrdinalIgnoreCase))
            {
                return result;
            }

            BotLog.Info($"MyParser 网易云音乐跳过非 MP3 音频: song_id={songId}, quality={quality}, type={result.FileType ?? "null"}");
        }

        return (string.Empty, null, null, null, null);
    }

    private async Task<(string AudioUrl, string? Quality, string? FileType, long? FileSize, int? Bitrate)> GetSongUrlAsync(long songId, string quality, CancellationToken cancellationToken)
    {
        var config = CreateEApiHeader();
        var payload = new { ids = new[] { songId }, level = quality, encodeType = "mp3", header = JsonSerializer.Serialize(config, NetEaseJson.Options) };
        var encrypted = NetEaseCrypto.EncryptEApiParams(SongUrlApi, payload);
        var json = await _http.PostFormAsync(SongUrlApi, new Dictionary<string, string> { ["params"] = encrypted }, MyParserRuntime.NetEaseCloudMusicCookie, cancellationToken).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array || data.GetArrayLength() == 0) return (string.Empty, null, null, null, null);
        var item = data[0];
        return (GetString(item, "url") ?? string.Empty, GetString(item, "level"), GetString(item, "type"), GetNullableInt64(item, "size"), GetNullableInt32(item, "br"));
    }

    private static string? GetString(JsonElement element, string name) => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static string? TryGetNestedString(JsonElement element, string objectName, string propertyName)
    {
        return element.ValueKind == JsonValueKind.Object
               && element.TryGetProperty(objectName, out var obj)
               && obj.ValueKind == JsonValueKind.Object
               && obj.TryGetProperty(propertyName, out var value)
               && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }
    private static long GetInt64(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value)) return 0;
        return value.ValueKind switch { JsonValueKind.Number when value.TryGetInt64(out var n) => n, JsonValueKind.String when long.TryParse(value.GetString(), out var n) => n, _ => 0 };
    }
    private static long? GetNullableInt64(JsonElement element, string name) => GetInt64(element, name) is var v && v > 0 ? v : null;
    private static int? GetNullableInt32(JsonElement element, string name) => GetInt64(element, name) is var v && v > 0 ? (int)v : null;
    private static string? JoinNames(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var array) || array.ValueKind != JsonValueKind.Array) return null;
        var names = array.EnumerateArray().Select(i => GetString(i, "name")).Where(i => !string.IsNullOrWhiteSpace(i)).ToArray();
        return names.Length == 0 ? null : string.Join("/", names);
    }
    public void Dispose() => _http.Dispose();
}
