using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Shirobot.Plugin.MyParser.Parsing;
using MyParser.Provider.BiliBili.Infrastructure;
using MyParser.Provider.BiliBili.Services;
using MyParser.Provider.BiliBili.Models;
using MyParser.Provider.BiliBili.Parsing;
using ShiroBot.SDK.Abstractions;

namespace MyParser.Provider.BiliBili.Parsing;

public sealed class BilibiliParser : IParserHttpClientAccessor, IVideoDownloadGate, IDisposable
{
    private static readonly Dictionary<int, string> QualityNames = new()
    {
        [6] = "240P 极速",
        [16] = "360P 流畅",
        [32] = "480P 清晰",
        [64] = "720P 高清",
        [74] = "720P60 高帧率",
        [80] = "1080P 高清",
        [100] = "智能修复",
        [112] = "1080P+ 高码率",
        [116] = "1080P60 高帧率",
        [120] = "4K 超清",
        [125] = "HDR 真彩色",
        [126] = "杜比视界",
        [127] = "8K 超高清",
        [129] = "HDR Vivid",
    };

    private static readonly Dictionary<int, string> CodecNames = new()
    {
        [7] = "AVC/H.264",
        [12] = "HEVC/H.265",
        [13] = "AV1",
    };

    private readonly HttpClient _http;
    private readonly HttpMessageHandler? _handler;
    private readonly bool _ownsHttpClient;
    private readonly PluginConfig _config;
    private readonly BilibiliArticleParser _articleParser;

    public HttpClient HttpClient => _http;
    private string? _mixinKey;
    private DateTimeOffset _mixinKeyExpiresAt;

    public BilibiliParser(PluginConfig config, HttpClient? httpClient = null)
    {
        _config = config;
        _ownsHttpClient = httpClient is null;
        if (httpClient is null)
        {
            _handler = BilibiliHttpClientFactory.CreateHandler();
            _http = new HttpClient(_handler) { Timeout = BilibiliHttpClientFactory.GetTimeout(config) };
        }
        else
        {
            _http = httpClient;
        }

        _articleParser = new BilibiliArticleParser(_http);
    }

    public Task<object> ParseMediaAsync(string text, CancellationToken cancellationToken = default)
    {
        var explicitPage = BilibiliUrlParser.ExtractVideoPage(text);
        return ParseMediaAsync(text, explicitPage ?? 1, explicitPage is not null, cancellationToken);
    }

    public Task<object> ParseMediaAsync(string text, int page = 1, CancellationToken cancellationToken = default)
    {
        return ParseMediaAsync(text, page, explicitPage: true, cancellationToken);
    }

    private async Task<object> ParseMediaAsync(string text, int page, bool explicitPage, CancellationToken cancellationToken)
    {
        EnsureLoginCookie();
        var bvid = await ResolveBvidAsync(text, cancellationToken);
        var view = await GetViewAsync(bvid, cancellationToken);
        var pages = view.GetPropertyOrDefault("pages")?.EnumerateArray().ToArray() ?? [];
        if (pages.Length == 0)
        {
            throw new BilibiliParseException("视频没有分 P 信息。");
        }

        if (pages.Length > 1 && !explicitPage)
        {
            var metadata = await GetVideoMetadataAsync(bvid, view, pages[0].GetInt64OrDefault("cid"), cancellationToken);
            return BuildMultiPageResult(bvid, view, pages, page) with
            {
                CategoryName = metadata.CategoryName,
                AuthorLocation = metadata.Location,
                Tags = metadata.Tags,
            };
        }

        var previewOnly = Uri.TryCreate(BilibiliUrlParser.ExtractStrictBilibiliUrl(text), UriKind.Absolute, out var uri)
            && uri.Fragment == "#myparser-preview";
        return await BuildSinglePageVideoResultAsync(bvid, view, pages, page, cancellationToken, previewOnly);
    }

    public async Task<BilibiliParseResult> ParseAsync(string text, CancellationToken cancellationToken = default)
    {
        var result = await ParseMediaAsync(text, cancellationToken);
        return result as BilibiliParseResult ?? throw new BilibiliParseException("该视频是分 P 视频，已按分 P 列表解析，不下载视频。");
    }

    public async Task<BilibiliParseResult> ParseAsync(string text, int page = 1, CancellationToken cancellationToken = default)
    {
        var result = await ParseMediaAsync(text, page, cancellationToken);
        return result as BilibiliParseResult ?? throw new BilibiliParseException("该视频是分 P 视频，已按分 P 列表解析，不下载视频。");
    }

    private async Task<BilibiliParseResult> BuildSinglePageVideoResultAsync(string bvid, JsonElement view, JsonElement[] pages, int page, CancellationToken cancellationToken, bool previewOnly = false)
    {
        var selectedPage = Math.Clamp(page, 1, pages.Length);
        var pageJson = pages[selectedPage - 1];
        var cid = pageJson.GetInt64OrDefault("cid");
        if (cid <= 0)
        {
            throw new BilibiliParseException("视频缺少 cid，无法获取播放流。");
        }

        var metadataTask = GetVideoMetadataAsync(bvid, view, cid, cancellationToken);
        var playInfoTask = previewOnly ? Task.FromResult(default(JsonElement)) : GetPlayUrlAsync(bvid, cid, cancellationToken);
        await Task.WhenAll(metadataTask, playInfoTask);
        var metadata = await metadataTask;
        var playInfo = await playInfoTask;
        var videos = ParseVideoStreams(playInfo).OrderByDescending(GetVideoScore).ToList();
        var audios = ParseAudioStreams(playInfo).OrderByDescending(i => i.Bandwidth).ToList();
        if (!previewOnly && videos.Count == 0)
        {
            throw new BilibiliParseException($"播放接口未返回可用的 DASH 视频流：bvid={bvid}, page={selectedPage}, format={playInfo.GetStringOrDefault("format") ?? "unknown"}。请检查视频访问权限或接口返回格式。");
        }

        var owner = view.GetPropertyOrDefault("owner");
        var stat = view.GetPropertyOrDefault("stat");
        var mainTitle = view.GetStringOrDefault("title");
        var partTitle = pageJson.GetStringOrDefault("part");
        var displayTitle = selectedPage > 1 && !string.IsNullOrWhiteSpace(partTitle)
            ? $"{mainTitle} - P{selectedPage} {partTitle}"
            : mainTitle;
        var mainCover = view.GetStringOrDefault("pic");
        var pageCover = FirstNonEmpty(pageJson.GetStringOrDefault("first_frame"), mainCover);
        var result = new BilibiliParseResult
        {
            Bvid = bvid,
            Aid = view.GetInt64OrDefault("aid"),
            Cid = cid,
            Page = selectedPage,
            PageCount = pages.Length,
            IsPreview = previewOnly,
            SourceUrl = selectedPage == 1 ? $"https://www.bilibili.com/video/{bvid}/" : $"https://www.bilibili.com/video/{bvid}/?p={selectedPage}",
            Title = displayTitle,
            PartTitle = partTitle,
            PublishTimestamp = view.GetInt64OrDefault("pubdate"),
            CategoryName = metadata.CategoryName,
            AuthorLocation = metadata.Location,
            Tags = metadata.Tags,
            Description = view.GetStringOrDefault("desc"),
            AuthorName = owner?.GetStringOrDefault("name"),
            AuthorId = owner?.GetInt64OrDefault("mid").ToString(),
            AuthorAvatarUrl = owner?.GetStringOrDefault("face"),
            CoverUrl = pageCover,
            DurationSeconds = pageJson.GetInt64OrDefault("duration") > 0 ? pageJson.GetInt64OrDefault("duration") : view.GetInt64OrDefault("duration"),
            ViewCount = stat?.GetInt64OrDefault("view") ?? 0,
            LikeCount = stat?.GetInt64OrDefault("like") ?? 0,
            CoinCount = stat?.GetInt64OrDefault("coin") ?? 0,
            FavoriteCount = stat?.GetInt64OrDefault("favorite") ?? 0,
            ShareCount = stat?.GetInt64OrDefault("share") ?? 0,
            ReplyCount = stat?.GetInt64OrDefault("reply") ?? 0,
            VideoStreams = videos,
            AudioStreams = audios,
        };
        return result;
    }

    private static BilibiliMultiPageParseResult BuildMultiPageResult(string bvid, JsonElement view, JsonElement[] pages, int requestedPage)
    {
        var owner = view.GetPropertyOrDefault("owner");
        var stat = view.GetPropertyOrDefault("stat");
        var coverUrl = view.GetStringOrDefault("pic");
        return new BilibiliMultiPageParseResult
        {
            Bvid = bvid,
            Aid = view.GetInt64OrDefault("aid"),
            PublishTimestamp = view.GetInt64OrDefault("pubdate"),
            DurationSeconds = pages.Sum(item => item.GetInt64OrDefault("duration")),
            ViewCount = stat?.GetInt64OrDefault("view") ?? 0,
            LikeCount = stat?.GetInt64OrDefault("like") ?? 0,
            CoinCount = stat?.GetInt64OrDefault("coin") ?? 0,
            FavoriteCount = stat?.GetInt64OrDefault("favorite") ?? 0,
            ShareCount = stat?.GetInt64OrDefault("share") ?? 0,
            SourceUrl = $"https://www.bilibili.com/video/{bvid}/",
            Title = view.GetStringOrDefault("title"),
            Description = view.GetStringOrDefault("desc"),
            AuthorName = owner?.GetStringOrDefault("name"),
            AuthorId = owner?.GetInt64OrDefault("mid").ToString(),
            AuthorAvatarUrl = owner?.GetStringOrDefault("face"),
            CoverUrl = coverUrl,
            RequestedPage = Math.Clamp(requestedPage, 1, pages.Length),
            Pages = pages.Select((item, index) => new BilibiliVideoPageInfo
            {
                Page = index + 1,
                Cid = item.GetInt64OrDefault("cid"),
                PartTitle = item.GetStringOrDefault("part"),
                DurationSeconds = item.GetInt64OrDefault("duration"),
                SourceUrl = $"https://www.bilibili.com/video/{bvid}/?p={index + 1}",
                CoverUrl = FirstNonEmpty(item.GetStringOrDefault("first_frame"), coverUrl),
            }).ToList(),
        };
    }

    private static string? FirstNonEmpty(params string?[] values)
    {
        return values.FirstOrDefault(i => !string.IsNullOrWhiteSpace(i));
    }

    public void EnsureVideoDownloadAllowed()
    {
        EnsureLoginCookie();
    }

    public Task<BilibiliArticleParseResult> ParseArticleAsync(string text, CancellationToken cancellationToken = default)
    {
        return _articleParser.ParseAsync(text, cancellationToken);
    }

    public static bool ContainsBilibiliUrl(string text) => BilibiliUrlParser.ContainsBilibiliUrl(text);

    public static bool LooksLikeBilibiliCookie(string cookie)
    {
        return cookie.Contains("SESSDATA=", StringComparison.OrdinalIgnoreCase)
               && cookie.Contains("bili_jct=", StringComparison.OrdinalIgnoreCase)
               && cookie.Contains(';');
    }

    private void EnsureLoginCookie()
    {
        if (!LooksLikeBilibiliCookie(MyParserRuntime.BilibiliCookie))
        {
            throw new BilibiliLoginRequiredException("解析 Bilibili 视频需要登录态。请在插件配置的 Cookie 分组填入哔哩哔哩网页 Cookie，保存后自动重载。");
        }
    }

    private async Task<string> ResolveBvidAsync(string text, CancellationToken cancellationToken)
    {
        var bvid = BilibiliUrlParser.ExtractBvid(text);
        if (bvid is not null)
        {
            return bvid;
        }

        var aid = BilibiliUrlParser.ExtractAid(text);
        if (aid is not null)
        {
            var view = await GetViewByAidAsync(aid.Value, cancellationToken);
            return view.GetStringOrDefault("bvid") ?? throw new BilibiliParseException("B站 view 接口未返回 bvid。");
        }

        throw new BilibiliParseException("无法从已展开的 Bilibili 链接中提取 BV/AV 号。");
    }

    private async Task<JsonElement> GetViewAsync(string bvid, CancellationToken cancellationToken)
    {
        using var json = await GetJsonDocumentAsync(BilibiliConstants.ViewApi, new Dictionary<string, string> { ["bvid"] = bvid }, BilibiliConstants.Origin + "/", cancellationToken);
        return json.RootElement.GetPropertyOrDefault("data")?.Clone() ?? throw new BilibiliParseException("B站 view 接口未返回 data。");
    }

    private async Task<(string? CategoryName, string? Location, IReadOnlyList<string> Tags)> GetVideoMetadataAsync(
        string bvid, JsonElement view, long cid, CancellationToken cancellationToken)
    {
        var referer = $"https://www.bilibili.com/video/{bvid}/";
        var appParameters = BilibiliAppSigner.Sign(new Dictionary<string, string>
        {
            ["aid"] = view.GetInt64OrDefault("aid").ToString(),
        });
        var appTask = TryGetSupplementaryDataAsync(BilibiliConstants.AppViewApi, appParameters, referer, cancellationToken);
        var tagsTask = TryGetSupplementaryDataAsync(BilibiliConstants.VideoTagsApi, new Dictionary<string, string>
        {
            ["bvid"] = bvid,
            ["cid"] = cid.ToString(),
        }, referer, cancellationToken);
        await Task.WhenAll(appTask, tagsTask);
        var app = await appTask;
        var tagsData = await tagsTask;
        var category = FirstNonEmpty(view.GetStringOrDefault("tname"), app?.GetStringOrDefault("tname"),
            view.GetStringOrDefault("tname_v2"), app?.GetStringOrDefault("tnamev2"));
        var location = FirstNonEmpty(app?.GetStringOrDefault("pub_location"), view.GetStringOrDefault("pub_location"));
        var tags = tagsData is { ValueKind: JsonValueKind.Array } array
            ? array.EnumerateArray().Select(tag => tag.GetStringOrDefault("tag_name"))
                .Where(name => !string.IsNullOrWhiteSpace(name)).Select(name => name!.Trim())
                .Distinct(StringComparer.Ordinal).ToArray()
            : [];
        return (category, location, tags);
    }

    private async Task<JsonElement?> TryGetSupplementaryDataAsync(string url, IReadOnlyDictionary<string, string> parameters,
        string referer, CancellationToken cancellationToken)
    {
        try
        {
            using var json = await GetJsonDocumentAsync(url, parameters, referer, cancellationToken);
            return json.RootElement.GetPropertyOrDefault("data")?.Clone();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or BilibiliParseException or OperationCanceledException)
        {
            BotLog.Warning($"MyParser Bilibili 补充卡片数据加载失败: endpoint={url}, error={ex.GetType().Name}");
            return null;
        }
    }

    private async Task<JsonElement> GetViewByAidAsync(long aid, CancellationToken cancellationToken)
    {
        using var json = await GetJsonDocumentAsync(BilibiliConstants.ViewApi, new Dictionary<string, string> { ["aid"] = aid.ToString() }, BilibiliConstants.Origin + "/", cancellationToken);
        return json.RootElement.GetPropertyOrDefault("data")?.Clone() ?? throw new BilibiliParseException("B站 view 接口未返回 data。");
    }

    private async Task<JsonElement> GetPlayUrlAsync(string bvid, long cid, CancellationToken cancellationToken)
    {
        var parameters = new Dictionary<string, object?>
        {
            ["bvid"] = bvid,
            ["cid"] = cid,
            ["qn"] = 127,
            ["fnver"] = 0,
            ["fnval"] = 4048,
            ["fourk"] = 1,
            ["gaia_source"] = string.Empty,
            ["from_client"] = "BROWSER",
            ["is_main_page"] = "true",
            ["need_fragment"] = "false",
        };
        var signed = BilibiliWbiSigner.Sign(parameters, await GetMixinKeyAsync(cancellationToken));
        using var json = await GetJsonDocumentAsync(BilibiliConstants.PlayUrlApi, signed, $"https://www.bilibili.com/video/{bvid}/", cancellationToken);
        return json.RootElement.GetPropertyOrDefault("data")?.Clone() ?? throw new BilibiliParseException("B站 playurl 接口未返回 data。");
    }

    internal async Task<string> GetMixinKeyAsync(CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(_mixinKey) && _mixinKeyExpiresAt > DateTimeOffset.UtcNow)
        {
            return _mixinKey;
        }

        using var json = await GetJsonDocumentAsync(BilibiliConstants.NavApi, null, BilibiliConstants.Origin + "/", cancellationToken);
        var wbi = json.RootElement.GetPropertyOrDefault("data")?.GetPropertyOrDefault("wbi_img");
        var imgUrl = wbi?.GetStringOrDefault("img_url") ?? string.Empty;
        var subUrl = wbi?.GetStringOrDefault("sub_url") ?? string.Empty;
        _mixinKey = BilibiliWbiSigner.CreateMixinKey(imgUrl, subUrl);
        _mixinKeyExpiresAt = DateTimeOffset.UtcNow.AddMinutes(30);
        return _mixinKey;
    }

    private async Task<JsonDocument> GetJsonDocumentAsync(string url, IReadOnlyDictionary<string, string>? parameters, string referer, CancellationToken cancellationToken)
    {
        var requestUrl = parameters is null || parameters.Count == 0
            ? url
            : url + "?" + string.Join("&", parameters.Select(i => $"{Uri.EscapeDataString(i.Key)}={Uri.EscapeDataString(i.Value)}"));
        using var request = new HttpRequestMessage(HttpMethod.Get, requestUrl);
        ApplyHeaders(request, referer);
        if (url == BilibiliConstants.AppViewApi)
        {
            request.Headers.Remove("User-Agent");
            request.Headers.TryAddWithoutValidation("User-Agent", BilibiliAppSigner.UserAgent);
        }
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadFromJsonAsync<JsonDocument>(cancellationToken: cancellationToken)
                   ?? throw new BilibiliParseException("B站接口返回空响应。");
        var code = json.RootElement.GetInt32OrDefault("code");
        if (code != 0)
        {
            var message = json.RootElement.GetStringOrDefault("message") ?? "未知错误";
            throw new BilibiliParseException($"B站接口错误 {code}: {message}");
        }

        return json;
    }

    private void ApplyHeaders(HttpRequestMessage request, string referer)
    {
        request.Headers.TryAddWithoutValidation("User-Agent", BilibiliConstants.UserAgent);
        request.Headers.TryAddWithoutValidation("Referer", referer);
        request.Headers.TryAddWithoutValidation("Origin", BilibiliConstants.Origin);
        request.Headers.TryAddWithoutValidation("Accept", "application/json, text/plain, */*");
        if (!string.IsNullOrWhiteSpace(MyParserRuntime.BilibiliCookie))
        {
            request.Headers.TryAddWithoutValidation("Cookie", MyParserRuntime.BilibiliCookie);
        }
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
                StreamId = $"v{index++}",
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
                StreamId = $"a{index++}",
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
        var fpsScore = _config.PreferHighFps ? (long)(stream.Fps * 1_000) : 0;
        return stream.QualityId * 10_000_000L + codecScore + stream.Width * stream.Height + fpsScore + stream.Bandwidth / 1_000;
    }

    private int GetPreferredCodecId()
    {
        return _config.PreferredVideoCodec switch
        {
            PreferredVideoCodec.H264 => 7,
            PreferredVideoCodec.H265 => 12,
            PreferredVideoCodec.AV1 => 13,
            _ => 12
        };
    }

    private static double ParseFps(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return 0;
        }

        if (value.Contains('/'))
        {
            var parts = value.Split('/', 2);
            return double.TryParse(parts[0], out var a) && double.TryParse(parts[1], out var b) && b != 0 ? Math.Round(a / b, 3) : 0;
        }

        return double.TryParse(value, out var fps) ? Math.Round(fps, 3) : 0;
    }

    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _http.Dispose();
        }
    }
}

public static class BilibiliJsonExtensions
{
    public static JsonElement? GetPropertyOrDefault(this JsonElement element, string name)
    {
        return element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) ? value : null;
    }

    public static string? GetStringOrDefault(this JsonElement element, string name)
    {
        return element.GetPropertyOrDefault(name)?.GetString();
    }

    public static int GetInt32OrDefault(this JsonElement element, string name)
    {
        var value = element.GetPropertyOrDefault(name);
        return value?.ValueKind switch
        {
            JsonValueKind.Number when value.Value.TryGetInt32(out var n) => n,
            JsonValueKind.String when int.TryParse(value.Value.GetString(), out var n) => n,
            _ => 0,
        };
    }

    public static long GetInt64OrDefault(this JsonElement element, string name)
    {
        var value = element.GetPropertyOrDefault(name);
        return value?.ValueKind switch
        {
            JsonValueKind.Number when value.Value.TryGetInt64(out var n) => n,
            JsonValueKind.String when long.TryParse(value.Value.GetString(), out var n) => n,
            _ => 0,
        };
    }

    public static bool GetBoolOrDefault(this JsonElement element, string name)
    {
        var value = element.GetPropertyOrDefault(name);
        return value?.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number when value.Value.TryGetInt32(out var n) => n != 0,
            JsonValueKind.String when bool.TryParse(value.Value.GetString(), out var b) => b,
            _ => false,
        };
    }

    public static IEnumerable<JsonElement> EnumerateArrayOrEmpty(this JsonElement? element)
    {
        return element is { ValueKind: JsonValueKind.Array } ? element.Value.EnumerateArray() : [];
    }

    public static List<string> GetStringArrayOrEmpty(this JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            var value = element.GetPropertyOrDefault(name);
            if (value is { ValueKind: JsonValueKind.Array })
            {
                return value.Value.EnumerateArray().Select(i => i.GetString()).Where(i => !string.IsNullOrWhiteSpace(i)).Cast<string>().ToList();
            }
        }

        return [];
    }
}
