using System.Net;
using System.Text.Json;
using MyParser.Provider.X.Infrastructure;
using MyParser.Provider.X.Models;
using ShiroBot.SDK.Abstractions;

namespace MyParser.Provider.X.Parsing;

public sealed class XParser : IParserHttpClientAccessor, IDisposable
{
    private const string SyndicationEndpoint = "https://cdn.syndication.twimg.com/tweet-result";
    private const string FxTwitterEndpoint = "https://api.fxtwitter.com/status";
    private const int PrimaryFailureCooldownMinutes = 10;

    private static readonly Lock CircuitLock = new();
    private static readonly Dictionary<bool, DateTime> PrimaryCooldownUntil = new();

    private readonly HttpClient _http;
    private readonly bool _ownsHttpClient;
    private readonly bool _usesProxy;

    public XParser(PluginConfig config, HttpClient? httpClient = null)
    {
        _ownsHttpClient = httpClient is null;
        _usesProxy = !string.IsNullOrWhiteSpace(config.HttpProxy);
        _http = httpClient ?? XHttpClientFactory.Create(config);
    }

    public HttpClient HttpClient => _http;

    public async Task<XTweetData> ParseAsync(string text, CancellationToken cancellationToken = default)
    {
        if (!XUrlExtractor.TryExtractStatusId(text, out var statusId))
        {
            throw new InvalidOperationException("无法从输入中提取 X (Twitter) 推文链接。");
        }

        Exception? primaryError = null;
        if (IsPrimaryAvailable())
        {
            try
            {
                return await ParseFromFxTwitterAsync(statusId, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                primaryError = ex;
                OpenPrimaryCircuit();
            }
        }

        try
        {
            return await ParseFromSyndicationAsync(statusId, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"X (Twitter) 推文解析失败：{ex.Message}", primaryError ?? ex);
        }
    }

    private bool IsPrimaryAvailable()
    {
        lock (CircuitLock)
        {
            if (!PrimaryCooldownUntil.TryGetValue(_usesProxy, out var until))
            {
                return true;
            }

            if (DateTime.UtcNow >= until)
            {
                PrimaryCooldownUntil.Remove(_usesProxy);
                return true;
            }

            return false;
        }
    }

    private void OpenPrimaryCircuit()
    {
        bool opened;
        lock (CircuitLock)
        {
            var inCooldown = PrimaryCooldownUntil.TryGetValue(_usesProxy, out var until)
                             && DateTime.UtcNow < until;
            opened = !inCooldown;
            PrimaryCooldownUntil[_usesProxy] = DateTime.UtcNow.AddMinutes(PrimaryFailureCooldownMinutes);
        }

        if (opened)
        {
            BotLog.Info($"MyParser X fxTwitter 主通道失败，{PrimaryFailureCooldownMinutes} 分钟内直接回退官方 Syndication（proxy={_usesProxy}）。");
        }
    }

    private async Task<XTweetData> ParseFromSyndicationAsync(string statusId, CancellationToken cancellationToken)
    {
        var url = $"{SyndicationEndpoint}?id={Uri.EscapeDataString(statusId)}&lang=en&token=0";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"Syndication 接口返回 {(int)response.StatusCode}。");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        var root = doc.RootElement;
        if (!root.TryGetProperty("id_str", out _))
        {
            throw new InvalidOperationException("Syndication 未返回推文数据。");
        }

        return new XTweetData(
            Id: root.TryGetProperty("id_str", out var idElement) ? idElement.GetString() ?? statusId : statusId,
            Text: GetString(root, "text"),
            Lang: GetString(root, "lang"),
            AuthorName: GetNestedString(root, "user", "name"),
            ScreenName: GetNestedString(root, "user", "screen_name"),
            AvatarUrl: GetNestedString(root, "user", "profile_image_url_https"),
            IsBlueVerified: GetBool(root, "user", "is_blue_verified"),
            Likes: GetLong(root, "favorite_count"),
            Replies: GetLong(root, "conversation_count"),
            Retweets: null,
            Views: GetVideoViewCount(root),
            CreatedAt: TryGetDateTime(root, "created_at"),
            PossiblySensitive: GetBool(root, "possibly_sensitive"),
            Media: ParseSyndicationMedia(root));
    }

    private async Task<XTweetData> ParseFromFxTwitterAsync(string statusId, CancellationToken cancellationToken)
    {
        var url = $"{FxTwitterEndpoint}/{Uri.EscapeDataString(statusId)}";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"fxTwitter 接口返回 {(int)response.StatusCode}。");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        var root = doc.RootElement;
        if (!root.TryGetProperty("tweet", out var tweet) || tweet.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException("fxTwitter 未返回推文数据。");
        }

        return new XTweetData(
            Id: tweet.TryGetProperty("id", out var idElement) ? idElement.GetString() ?? statusId : statusId,
            Text: GetString(tweet, "text"),
            Lang: GetString(tweet, "lang"),
            AuthorName: GetNestedString(tweet, "author", "name"),
            ScreenName: GetNestedString(tweet, "author", "screen_name"),
            AvatarUrl: GetNestedString(tweet, "author", "avatar_url"),
            IsBlueVerified: GetFxTwitterVerified(tweet),
            Likes: GetLong(tweet, "likes"),
            Replies: GetLong(tweet, "replies"),
            Retweets: GetLong(tweet, "reposts") ?? GetLong(tweet, "retweets"),
            Views: GetLong(tweet, "views"),
            CreatedAt: GetFxTwitterCreatedAt(tweet),
            PossiblySensitive: GetBool(tweet, "possibly_sensitive"),
            Media: ParseFxTwitterMedia(tweet));
    }

    private static IReadOnlyList<XMediaItem> ParseSyndicationMedia(JsonElement root)
    {
        var items = new List<XMediaItem>();
        if (!root.TryGetProperty("mediaDetails", out var details) || details.ValueKind != JsonValueKind.Array)
        {
            return items;
        }

        foreach (var item in details.EnumerateArray())
        {
            var rawKind = GetString(item, "type");
            var kind = rawKind switch
            {
                "photo" => "photo",
                "animated_gif" => "gif",
                _ => "video",
            };
            var url = GetString(item, "media_url_https");
            var variants = new List<XMediaVariant>();
            if (item.TryGetProperty("video_info", out var videoInfo)
                && videoInfo.TryGetProperty("variants", out var variantsElement)
                && variantsElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var variant in variantsElement.EnumerateArray())
                {
                    var variantUrl = GetString(variant, "url");
                    if (string.IsNullOrWhiteSpace(variantUrl)) continue;
                    variants.Add(new XMediaVariant(
                        variantUrl,
                        variant.TryGetProperty("bitrate", out var bitrate) && bitrate.ValueKind == JsonValueKind.Number ? bitrate.GetInt32() : 0,
                        GetString(variant, "content_type")));
                }
            }

            var (width, height) = GetOriginalSize(item);
            items.Add(new XMediaItem(kind, true, width, height, url, url, variants));
        }

        return items;
    }

    private static IReadOnlyList<XMediaItem> ParseFxTwitterMedia(JsonElement tweet)
    {
        var items = new List<XMediaItem>();
        if (!tweet.TryGetProperty("media", out var media) || !media.TryGetProperty("all", out var all) || all.ValueKind != JsonValueKind.Array)
        {
            return items;
        }

        foreach (var item in all.EnumerateArray())
        {
            var kind = GetString(item, "type").ToLowerInvariant();
            if (kind is not ("photo" or "video" or "gif")) continue;
            var variants = ParseFxTwitterVariants(item);

            var url = GetString(item, "url");
            var thumbnailUrl = GetString(item, "thumbnail_url");
            var (width, height) = GetSize(item);
            var isMp4 = variants.Any(variant => variant.ContentType.Contains("mp4", StringComparison.OrdinalIgnoreCase))
                        || "mp4".Equals(System.IO.Path.GetExtension(url), StringComparison.OrdinalIgnoreCase);
            items.Add(new XMediaItem(kind, isMp4, width, height, url, string.IsNullOrWhiteSpace(thumbnailUrl) ? url : thumbnailUrl, variants));
        }

        return items;
    }

    private static List<XMediaVariant> ParseFxTwitterVariants(JsonElement item)
    {
        var variants = new List<XMediaVariant>();
        if (item.TryGetProperty("variants", out var variantsElement) && variantsElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var variant in variantsElement.EnumerateArray())
            {
                var variantUrl = GetString(variant, "url");
                if (string.IsNullOrWhiteSpace(variantUrl)) continue;
                variants.Add(new XMediaVariant(
                    variantUrl,
                    GetInt(variant, "bitrate"),
                    GetString(variant, "content_type")));
            }
        }

        // FxTwitter's current response calls these formats; older responses used variants.
        if (item.TryGetProperty("formats", out var formatsElement) && formatsElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var format in formatsElement.EnumerateArray())
            {
                var formatUrl = GetString(format, "url");
                if (string.IsNullOrWhiteSpace(formatUrl)) continue;
                var container = GetString(format, "container");
                var contentType = container.Equals("mp4", StringComparison.OrdinalIgnoreCase)
                    ? "video/mp4"
                    : container;
                variants.Add(new XMediaVariant(formatUrl, GetInt(format, "bitrate"), contentType));
            }
        }

        return variants
            .DistinctBy(variant => variant.Url, StringComparer.Ordinal)
            .ToList();
    }

    private static (int Width, int Height) GetOriginalSize(JsonElement item)
    {
        if (item.TryGetProperty("original_info", out var original))
        {
            return (GetInt(original, "width"), GetInt(original, "height"));
        }

        return (0, 0);
    }

    private static (int Width, int Height) GetSize(JsonElement item) =>
        (GetInt(item, "width"), GetInt(item, "height"));

    private static long? GetVideoViewCount(JsonElement root)
    {
        if (!root.TryGetProperty("video", out var video) || video.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return GetLong(video, "viewCount");
    }

    private static DateTimeOffset? GetFxTwitterCreatedAt(JsonElement tweet)
    {
        if (tweet.TryGetProperty("created_timestamp", out var timestamp)
            && timestamp.ValueKind == JsonValueKind.Number)
        {
            return DateTimeOffset.FromUnixTimeSeconds(timestamp.GetInt64());
        }

        return TryGetDateTime(tweet, "created_at");
    }

    private static bool GetFxTwitterVerified(JsonElement tweet)
    {
        if (!tweet.TryGetProperty("author", out var author)
            || !author.TryGetProperty("verification", out var verification)
            || verification.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var type = GetString(verification, "type");
        return !string.IsNullOrWhiteSpace(type) && type != "none";
    }

    private static string GetString(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(propertyName, out var value))
        {
            return string.Empty;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? string.Empty,
            JsonValueKind.Number => value.GetRawText(),
            _ => string.Empty,
        };
    }

    private static string GetNestedString(JsonElement root, string container, string propertyName)
    {
        return root.ValueKind == JsonValueKind.Object
               && root.TryGetProperty(container, out var value)
               && value.ValueKind == JsonValueKind.Object
            ? GetString(value, propertyName)
            : string.Empty;
    }

    private static bool GetBool(JsonElement root, string propertyName) =>
        root.ValueKind == JsonValueKind.Object
        && root.TryGetProperty(propertyName, out var value)
        && value.ValueKind == JsonValueKind.True;

    private static bool GetBool(JsonElement root, string container, string propertyName) =>
        root.ValueKind == JsonValueKind.Object
        && root.TryGetProperty(container, out var value)
        && GetBool(value, propertyName);

    private static long? GetLong(JsonElement root, string propertyName)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number)
        {
            return value.TryGetInt64(out var number) ? number : (long?)null;
        }

        if (value.ValueKind == JsonValueKind.String && long.TryParse(value.GetString(), out var parsed))
        {
            return parsed;
        }

        return null;
    }

    private static int GetInt(JsonElement root, string propertyName)
    {
        var number = GetLong(root, propertyName);
        return number is > int.MinValue and < int.MaxValue ? (int)number.Value : 0;
    }

    private static DateTimeOffset? TryGetDateTime(JsonElement root, string propertyName)
    {
        var value = GetString(root, propertyName);
        return DateTimeOffset.TryParse(value, out var parsed) ? parsed : null;
    }

    public void Dispose()
    {
        if (_ownsHttpClient) _http.Dispose();
    }
}
