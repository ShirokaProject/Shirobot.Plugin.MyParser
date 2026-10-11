using System.Text.Json.Serialization;

namespace MyParser.Provider.WeixinChannels.Models;

public sealed class WeixinChannelsParseResult
{
    public required string ShareUrl { get; init; }
    public required string SphId { get; init; }
    public string? ObjectId { get; init; }
    public string? ExportId { get; init; }
    public string? Title { get; init; }
    public string? Description { get; init; }
    public string? AuthorName { get; init; }
    public string? AuthorAvatarUrl { get; init; }
    public string? CoverUrl { get; init; }
    public string? VideoUrl { get; init; }
    public string? H264VideoUrl { get; init; }
    public string? H265VideoUrl { get; init; }
    public string? OriginVideoUrl { get; init; }
    public int DurationSeconds { get; init; }
    public long? FileSize { get; init; }
    public DateTimeOffset? PublishTime { get; init; }
    public string? LikeCountText { get; init; }
    public string? FavoriteCountText { get; init; }
    public string? ForwardCountText { get; init; }
    public string? CommentCountText { get; init; }
    public string? LocalVideoPath { get; set; }
    public string? LocalVideoFileUri { get; set; }
    public bool LocalVideoRegisteredToHttpServer { get; set; }
}

internal sealed record WeixinChannelsParseResponse(
    [property: JsonPropertyName("code")] int Code,
    [property: JsonPropertyName("msg")] string? Msg,
    [property: JsonPropertyName("data")] WeixinChannelsParseData? Data);

internal sealed record WeixinChannelsParseData(
    [property: JsonPropertyName("wx_export_id")] string? WxExportId,
    [property: JsonPropertyName("cover_url")] string? CoverUrl,
    [property: JsonPropertyName("author_certification_icon")] string? AuthorCertificationIcon,
    [property: JsonPropertyName("author")] string? Author,
    [property: JsonPropertyName("author_icon")] string? AuthorIcon,
    [property: JsonPropertyName("desc")] string? Desc,
    [property: JsonPropertyName("playable_url")] string? PlayableUrl);

internal sealed class WeixinChannelsFeedResponse
{
    public WeixinChannelsFeedResponseData? Data { get; set; }
    public int ErrCode { get; set; }
    public string? ErrMsg { get; set; }
}

internal sealed class WeixinChannelsFeedResponseData
{
    public WeixinChannelsAuthorInfo? AuthorInfo { get; set; }
    public WeixinChannelsFeedInfo? FeedInfo { get; set; }
    public WeixinChannelsSceneInfo? SceneInfo { get; set; }
}

internal sealed class WeixinChannelsSceneInfo
{
    public string? DynamicExportId { get; set; }
}

internal sealed class WeixinChannelsFeedInfo
{
    public string? VideoUrl { get; set; }
    public string? Description { get; set; }
    public int MediaType { get; set; }
    public string? FavCountFmt { get; set; }
    public string? LikeCountFmt { get; set; }
    public string? ForwardCountFmt { get; set; }
    public string? CommentCountFmt { get; set; }
    public WeixinChannelsVideoInfo? H264VideoInfo { get; set; }
    public WeixinChannelsVideoInfo? H265VideoInfo { get; set; }
    public int CreateTime { get; set; }
    public string? CoverUrl { get; set; }
    public long? FileSize { get; set; }
    public int? VideoPlayLen { get; set; }
}

internal sealed class WeixinChannelsVideoInfo
{
    public string? VideoUrl { get; set; }
}

internal sealed class WeixinChannelsAuthorInfo
{
    public string? Nickname { get; set; }
    public string? HeadImgUrl { get; set; }
    public string? AuthIconUrl { get; set; }
}
