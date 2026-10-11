using Shirobot.Plugin.MyParser.CardRendering;
using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Media.Imaging;

namespace MyParser.Provider.WeixinChannels.Views;

public sealed class WeixinChannelsCardViewModel
{
    public WeixinChannelsCardViewModel()
    {
        Cover = LoadDesignBitmap("background.png");
        Avatar = LoadDesignBitmap("avatar.jpg");
        Title = "微信视频号标题";
        AuthorName = "视频号作者";
        Description = "这里展示微信视频号视频简介。";
        DurationText = "00:30";
        PublishText = "微信视频号";
        LikeCount = "赞 --";
        CommentCount = "评论 --";
        FavoriteCount = "收藏 --";
        ForwardCount = "转发 --";
    }

    public Bitmap? Cover { get; init; }
    public Bitmap? Avatar { get; init; }
    public string Title { get; init; } = string.Empty;
    public string AuthorName { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public string DurationText { get; init; } = string.Empty;
    public string PublishText { get; init; } = string.Empty;
    public string LikeCount { get; init; } = string.Empty;
    public string CommentCount { get; init; } = string.Empty;
    public string FavoriteCount { get; init; } = string.Empty;
    public string ForwardCount { get; init; } = string.Empty;

    private static Bitmap? LoadDesignBitmap(string fileName, [CallerFilePath] string sourceFilePath = "")
    {
        if (!Design.IsDesignMode)
        {
            return null;
        }

        var viewModelsDir = Path.GetDirectoryName(sourceFilePath);
        if (string.IsNullOrWhiteSpace(viewModelsDir))
        {
            return null;
        }

        for (var directory = new DirectoryInfo(viewModelsDir); directory is not null; directory = directory.Parent)
        {
            var path = Path.Combine(directory.FullName, "Assets", "Demo", fileName);
            if (File.Exists(path))
            {
                return fileName == "avatar.jpg" ? AvatarBitmapResizer.DecodeFile(path, 46) : new Bitmap(path);
            }
        }

        return null;
    }
}
