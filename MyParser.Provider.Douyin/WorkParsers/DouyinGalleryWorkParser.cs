using MyParser.Provider.Douyin.Models;
using System.Text.Json;
using MyParser.Provider.Douyin.Abstractions;
using static MyParser.Provider.Douyin.Utilities.DouyinParseHelpers;

namespace MyParser.Provider.Douyin.WorkParsers;

public sealed class DouyinGalleryWorkParser : IDouyinWorkParser
{
    public bool CanParse(JsonElement aweme)
    {
        return TryGetProperty(aweme, "images", out var images)
               && images.ValueKind == JsonValueKind.Array
               && images.GetArrayLength() > 0
               && images.EnumerateArray().Any(image => EnumerateUrlList(image).Any());
    }

    public DouyinParseResult Parse(JsonElement aweme, string fallbackAwemeId, string sourceUrl)
    {
        var awemeId = GetString(aweme, "aweme_id") ?? fallbackAwemeId;
        var title = GetString(aweme, "desc");
        var author = TryGetProperty(aweme, "author", out var authorEl) ? authorEl : default;
        var video = TryGetProperty(aweme, "video", out var videoEl) ? videoEl : default;
        var images = ExtractImages(aweme);
        var musicEl = TryGetProperty(aweme, "music", out var music) ? music : default;

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
            CoverUrl = images.FirstOrDefault()?.Url ?? ExtractSimpleCoverUrl(video),
            CoverSource = "detail_image",
            VideoUrl = null,
            MusicUrl = ExtractGalleryMusicUrl(musicEl, video),
            MusicTitle = musicEl.ValueKind == JsonValueKind.Object ? GetString(musicEl, "title") : null,
            MusicAuthor = musicEl.ValueKind == JsonValueKind.Object ? GetString(musicEl, "author") : null,
            LikeCount = GetStatisticLong(aweme, "digg_count", "diggCount"),
            CollectCount = GetStatisticLong(aweme, "collect_count", "collectCount"),
            CommentCount = GetStatisticLong(aweme, "comment_count", "commentCount"),
            ShareCount = GetStatisticLong(aweme, "share_count", "shareCount"),
            Tags = ExtractTags(aweme),
            Qualities = [],
            Images = images,
        };
    }

    private static List<DouyinImageInfo> ExtractImages(JsonElement aweme)
    {
        var result = new List<DouyinImageInfo>();
        if (!TryGetProperty(aweme, "images", out var images) || images.ValueKind != JsonValueKind.Array)
        {
            return result;
        }

        foreach (var img in images.EnumerateArray())
        {
            var imageUrl = EnumerateUrlList(img).FirstOrDefault(u => !u.Contains(".webp", StringComparison.OrdinalIgnoreCase))
                ?? EnumerateUrlList(img).FirstOrDefault();
            var livePhotoUrl = ExtractLivePhotoUrl(img);
            var clipType = GetLong(img, "clip_type");
            var livePhotoType = GetLong(img, "live_photo_type");
            if (!string.IsNullOrWhiteSpace(livePhotoUrl))
            {
                ShiroBot.SDK.Abstractions.BotLog.Info($"MyParser 抖音 Live Photo 动态地址命中: clip_type={clipType}, live_photo_type={livePhotoType}, url={livePhotoUrl}");
            }
            else
            {
                var properties = img.ValueKind == JsonValueKind.Object
                    ? string.Join(',', img.EnumerateObject().Select(property => property.Name))
                    : img.ValueKind.ToString();
                ShiroBot.SDK.Abstractions.BotLog.Info($"MyParser 抖音 Live Photo 字段未命中: clip_type={clipType}, live_photo_type={livePhotoType}, image_properties={properties}");
            }

            if (!string.IsNullOrWhiteSpace(imageUrl))
            {
                result.Add(new DouyinImageInfo { Url = imageUrl, LivePhotoUrl = livePhotoUrl });
            }
        }

        return result.GroupBy(i => i.Url).Select(g => g.First()).ToList();
    }

    private static string? ExtractLivePhotoUrl(JsonElement image)
    {
        var videoRoots = new List<JsonElement>();
        foreach (var propertyName in new[]
                 {
                     "video", "video_info", "videoInfo", "video_clip", "videoClip", "clip", "clip_info", "clipInfo",
                 })
        {
            if (TryGetProperty(image, propertyName, out var videoRoot)
                && videoRoot.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
            {
                videoRoots.Add(videoRoot);
            }
        }

        if (new[] { "play_addr", "playAddr", "download_addr", "downloadAddr", "play_addr_h264", "play_addr_265" }
            .Any(propertyName => TryGetProperty(image, propertyName, out _)))
        {
            videoRoots.Add(image);
        }

        foreach (var videoRoot in videoRoots)
        {
            var livePhotoUrl = ExtractVideoRootUrl(videoRoot);
            if (!string.IsNullOrWhiteSpace(livePhotoUrl))
            {
                return livePhotoUrl;
            }
        }

        return null;
    }

    private static string? ExtractVideoRootUrl(JsonElement videoRoot)
    {
        if (videoRoot.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in videoRoot.EnumerateArray())
            {
                var url = ExtractVideoRootUrl(item);
                if (!string.IsNullOrWhiteSpace(url)) return url;
            }

            return null;
        }

        if (videoRoot.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var propertyName in new[]
                 {
                     "play_addr_h264", "play_addr", "playAddr", "PlayAddr", "PlayAddrStruct", "download_addr", "downloadAddr", "play_addr_265",
                 })
        {
            if (!TryGetProperty(videoRoot, propertyName, out var playAddress)) continue;

            var directUrl = EnumerateUrlList(playAddress).FirstOrDefault();
            directUrl ??= EnumerateNestedHttpUrls(playAddress).FirstOrDefault(IsVideoUrl);
            if (!string.IsNullOrWhiteSpace(directUrl))
            {
                return NormalizeNoWatermarkUrl(directUrl);
            }

            var uri = GetString(playAddress, "uri");
            if (!string.IsNullOrWhiteSpace(uri))
            {
                if (Uri.TryCreate(uri, UriKind.Absolute, out var absoluteUri) && IsVideoUrl(absoluteUri.ToString()))
                {
                    return NormalizeNoWatermarkUrl(absoluteUri.ToString());
                }

                return $"https://aweme.snssdk.com/aweme/v1/play/?video_id={Uri.EscapeDataString(uri)}&ratio=1080p&line=0";
            }
        }

        return null;
    }

    private static IEnumerable<string> EnumerateNestedHttpUrls(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.String)
        {
            var text = value.GetString();
            if (Uri.TryCreate(text, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
            {
                yield return uri.ToString();
            }

            yield break;
        }

        if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray())
            foreach (var url in EnumerateNestedHttpUrls(item))
                yield return url;
            yield break;
        }

        if (value.ValueKind != JsonValueKind.Object) yield break;
        foreach (var property in value.EnumerateObject())
        foreach (var url in EnumerateNestedHttpUrls(property.Value))
            yield return url;
    }

    private static bool IsVideoUrl(string url)
    {
        var normalized = url.ToLowerInvariant();
        return !normalized.Contains(".mp3", StringComparison.Ordinal)
               && !normalized.Contains("ies-music", StringComparison.Ordinal)
               && (normalized.Contains(".mp4", StringComparison.Ordinal)
                   || normalized.Contains("video_id=", StringComparison.Ordinal)
                   || normalized.Contains("/aweme/v1/play", StringComparison.Ordinal)
                   || normalized.Contains("douyinvod", StringComparison.Ordinal)
                   || normalized.Contains("mime_type=video", StringComparison.Ordinal));
    }

    private static string? ExtractGalleryMusicUrl(JsonElement music, JsonElement video)
    {
        var musicUrl = music.ValueKind == JsonValueKind.Object
            ? ExtractFirstUrl(music, "play_url")
            : null;
        if (!string.IsNullOrWhiteSpace(musicUrl))
        {
            return musicUrl;
        }

        if (video.ValueKind != JsonValueKind.Object
            || !TryGetProperty(video, "play_addr", out var playAddress)
            || playAddress.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var uri = GetString(playAddress, "uri");
        return Uri.TryCreate(uri, UriKind.Absolute, out var absoluteUri)
               && absoluteUri.Scheme is "http" or "https"
            ? absoluteUri.ToString()
            : null;
    }
}
