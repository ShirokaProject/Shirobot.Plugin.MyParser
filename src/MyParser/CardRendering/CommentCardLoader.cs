using System.Collections.Concurrent;
using Avalonia.Media.Imaging;
using Shirobot.Plugin.MyParser.Parsing;

namespace Shirobot.Plugin.MyParser.CardRendering;

/// <summary>
/// 把 provider 产出的评论 <see cref="MediaContentBlock"/>（category=comment）转换为评论卡片条目。
/// 约定的属性：avatar_url、image_url、created（unix 秒）、ip、likes、replies、is_author。
/// </summary>
public static class CommentCardLoader
{
    public const double AvatarDecodeSize = 30;

    /// <param name="loadImage">(url, cacheName, avatarSize, token)，avatarSize 为 0 时按原图解码；失败应返回 null。</param>
    /// <param name="bitmaps">收集已解码的位图，调用方在渲染后统一释放。</param>
    public static async Task<IReadOnlyList<CommentCardItem>> LoadItemsAsync(
        IReadOnlyList<MediaContentBlock> blocks,
        ConcurrentBag<Bitmap> bitmaps,
        Func<string?, string, double, CancellationToken, Task<Bitmap?>> loadImage,
        CancellationToken cancellationToken,
        DateTimeOffset? now = null)
    {
        using var slots = new SemaphoreSlim(4);
        var tasks = blocks.Select(async (block, index) =>
        {
            await slots.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var avatarTask = loadImage(block.Attributes.GetValueOrDefault("avatar_url"), $"comment_{index + 1}_avatar", AvatarDecodeSize, cancellationToken);
                var imageTask = loadImage(block.Attributes.GetValueOrDefault("image_url"), $"comment_{index + 1}_image", 0, cancellationToken);
                try { await Task.WhenAll(avatarTask, imageTask).ConfigureAwait(false); }
                finally
                {
                    if (avatarTask.IsCompletedSuccessfully && avatarTask.Result is { } avatarBitmap) bitmaps.Add(avatarBitmap);
                    if (imageTask.IsCompletedSuccessfully && imageTask.Result is { } imageBitmap) bitmaps.Add(imageBitmap);
                }

                return CreateItem(block, index, await avatarTask.ConfigureAwait(false), await imageTask.ConfigureAwait(false), now);
            }
            finally { slots.Release(); }
        }).ToArray();
        return await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    public static CommentCardItem CreateItem(MediaContentBlock block, int index, Bitmap? avatar, Bitmap? image, DateTimeOffset? now = null)
    {
        var timeText = long.TryParse(block.Attributes.GetValueOrDefault("created"), out var timestamp) && timestamp > 0
            ? CommentCardViewModel.FormatRelativeTime(DateTimeOffset.FromUnixTimeSeconds(timestamp).ToOffset(TimeSpan.FromHours(8)), now)
            : string.Empty;
        var location = CommentCardViewModel.NormalizeLocation(block.Attributes.GetValueOrDefault("ip"));
        var replies = long.TryParse(block.Attributes.GetValueOrDefault("replies"), out var count) ? count : 0;
        var likes = block.Attributes.GetValueOrDefault("likes", "0");
        return new CommentCardItem
        {
            Avatar = avatar,
            Image = image,
            UserName = string.IsNullOrWhiteSpace(block.Caption) ? "未知用户" : block.Caption.Trim(),
            IsAuthor = string.Equals(block.Attributes.GetValueOrDefault("is_author"), bool.TrueString, StringComparison.OrdinalIgnoreCase),
            Meta = string.Join(" · ", new[] { timeText, location }.Where(value => !string.IsNullOrWhiteSpace(value))),
            Message = block.Text.Trim(),
            LikeText = long.TryParse(likes, out var likeCount) && likeCount > 0 ? MediaCardViewModel.FormatCount(likes) : "0",
            ReplyText = replies > 0 ? $"{MediaCardViewModel.FormatCount(replies.ToString())} 条回复" : string.Empty,
            ShowDivider = index > 0,
        };
    }
}
