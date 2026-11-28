using System.Globalization;
using System.Reflection;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace Shirobot.Plugin.MyParser.CardRendering;

/// <summary>
/// B 站 / 抖音共用的评论区卡片。与 <see cref="MediaCardViewModel"/> 同一视觉语言：
/// 封面模糊氛围底 + 平台双色调，评论落在一块毛玻璃面板上。
/// </summary>
public sealed class CommentCardViewModel
{
    public MediaCardPlatform Platform { get; init; } = MediaCardPlatform.Bilibili;
    public string PlatformName => Platform.Name;
    public bool IsBilibili => Platform.IsBilibili;
    public bool IsDouyin => Platform.IsDouyin;
    public IBrush AccentGradient => Platform.CreateGradient(1, 1);
    public IBrush AccentSoftGradient => Platform.CreateGradient(0.13, 0.13);
    public IBrush AccentTintGradient => Platform.CreateGradient(0.22, 0.16);
    public IBrush PrimaryBrush => new SolidColorBrush(Platform.Primary);

    /// <summary>作品封面：用作氛围底和标题旁的缩略图。</summary>
    public Bitmap? Cover { get; init; }
    public bool HasCover => Cover is not null;
    public bool HasNoCover => Cover is null;

    public string SourceTitle { get; init; } = "作品标题";
    /// <summary>底部说明，例如 "共 1.2万 条评论"。</summary>
    public string SourceMeta { get; init; } = string.Empty;
    public bool HasSourceMeta => !string.IsNullOrWhiteSpace(SourceMeta);
    public string FooterText => HasSourceMeta ? $"{PlatformName} · {SourceMeta}" : PlatformName;
    /// <summary>右上角胶囊，例如 "热评 10"。</summary>
    public string CountText { get; init; } = string.Empty;
    public bool HasCountText => !string.IsNullOrWhiteSpace(CountText);
    /// <summary>评论里有图片时，底部提示「回复 原图 获取评论图片」。</summary>
    public bool HasImageHint => Comments.Any(comment => comment.Image is not null);

    /// <summary>点赞图标：B 站用竖拇指，抖音用爱心。</summary>
    public string LikeIcon => Platform.IsDouyin ? MediaCardIcons.Heart : MediaCardIcons.ThumbUp;
    public string PlatformIcon => Platform.IsDouyin ? MediaCardIcons.DouyinNote : MediaCardIcons.BilibiliTv;

    public IReadOnlyList<CommentCardItem> Comments { get; init; } = [];



    /// <summary>"3 分钟前" / "5 天前" / "11-02" / "2025-11-02"。</summary>
    public static string FormatRelativeTime(DateTimeOffset timestamp, DateTimeOffset? now = null)
    {
        var current = now ?? DateTimeOffset.Now;
        var elapsed = current - timestamp;
        if (elapsed < TimeSpan.FromMinutes(1)) return "刚刚";
        if (elapsed < TimeSpan.FromHours(1)) return $"{Math.Max(1, (int)elapsed.TotalMinutes)} 分钟前";
        if (elapsed < TimeSpan.FromDays(1)) return $"{Math.Max(1, (int)elapsed.TotalHours)} 小时前";
        if (elapsed < TimeSpan.FromDays(7)) return $"{Math.Max(1, (int)elapsed.TotalDays)} 天前";
        var local = timestamp.ToOffset(current.Offset);
        return local.Year == current.Year
            ? local.ToString("MM-dd", CultureInfo.InvariantCulture)
            : local.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }

    /// <summary>去掉 "IP属地：" 前缀，只留地名。</summary>
    public static string NormalizeLocation(string? location)
    {
        if (string.IsNullOrWhiteSpace(location)) return string.Empty;
        var value = location.Trim();
        if (value.StartsWith("IP属地", StringComparison.Ordinal)) value = value[4..];
        return value.TrimStart('：', ':', ' ');
    }
}

public sealed class CommentCardItem
{
    public Bitmap? Avatar { get; init; }
    public bool HasAvatar => Avatar is not null;
    public string UserName { get; init; } = "未知用户";
    public bool IsAuthor { get; init; }
    /// <summary>时间 · IP 属地。</summary>
    public string Meta { get; init; } = string.Empty;
    public bool HasMeta => !string.IsNullOrWhiteSpace(Meta);
    public string Message { get; init; } = string.Empty;

    public Bitmap? Image { get; init; }
    public bool HasImage => Image is not null;

    public string LikeText { get; init; } = "0";
    public bool HasLikes => LikeText is not ("" or "0");
    public string ReplyText { get; init; } = string.Empty;
    public bool HasReplies => !string.IsNullOrWhiteSpace(ReplyText);

    /// <summary>第一条不画分隔线。</summary>
    public bool ShowDivider { get; init; } = true;
}
