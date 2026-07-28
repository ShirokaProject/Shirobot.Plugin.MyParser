using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Media.Imaging;

namespace MyParser.Provider.Douyin.Views;

public sealed class DouyinCardViewModel
{
    public DouyinCardViewModel()
    {
        // 设计时数据
        Cover = LoadDesignBackground();
        Avatar = LoadDesignAvatar();
        CoverUri = "https://p3-sign.douyinpic.com/obj/eeaafc00000000000001020000000000_1678889600?x-expires=1678889600&x-signature=Z%2F%2F%2Bzqj%2Fh7n9lXoQyK8%2Fh5s%3D";
        AlbumId = "1234567890";
        Title = "这是视频标题";
        Description = "这是视频描述这是视频描述这是视频描述这是视频描述这是视频描述这是视频描述这是视频描述这是视频描述";
        AuthorName = "UP主名字";
        AuthorMeta = "UP主简介UP主简介";
        DurationText = "12:34";
        PublishTimeText = "发布于 2026-07-28 12:34";
        HasPublishTime = true;
        PageText = "1/1";
        LikeCount = "345";
        CollectCount = "678";
        CommentCount = "90";
        ShareCount = "12";
        MusicText = "背景音乐：某某某 - 某某某";
        TagsText = "#标签1 #标签2 #标签3 #标签4 #标签5";
    }
    
    public Bitmap? Cover { get; init; }
    public Bitmap? Avatar { get; init; }
    public string CoverUri { get; init; } = string.Empty;
    public string AlbumId { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public string AuthorName { get; init; } = string.Empty;
    public string AuthorMeta { get; init; } = string.Empty;
    public string DurationText { get; init; } = string.Empty;
    public string PublishTimeText { get; init; } = string.Empty;
    public bool HasPublishTime { get; init; }
    public string PageText { get; init; } = string.Empty;
    public string LikeCount { get; init; } = string.Empty;
    public string CollectCount { get; init; } = string.Empty;
    public string CommentCount { get; init; } = string.Empty;
    public string ShareCount { get; init; } = string.Empty;
    public string MusicText { get; init; } = string.Empty;
    public string TagsText { get; init; } = string.Empty;
    
    private static Bitmap? LoadDesignBackground([CallerFilePath] string sourceFilePath = "")
    {
        return LoadDesignBitmap("background.png", sourceFilePath);
    }

    private static Bitmap? LoadDesignAvatar([CallerFilePath] string sourceFilePath = "")
    {
        return LoadDesignBitmap("avatar.jpg", sourceFilePath);
    }

    private static Bitmap? LoadDesignBitmap(string fileName, string sourceFilePath)
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

        foreach (var path in EnumerateDesignAssetCandidates(viewModelsDir, fileName))
        {
            if (File.Exists(path))
            {
                return new Bitmap(path);
            }
        }

        return null;
    }

    private static IEnumerable<string> EnumerateDesignAssetCandidates(string startDirectory, string fileName)
    {
        for (var directory = new DirectoryInfo(startDirectory); directory is not null; directory = directory.Parent)
        {
            yield return Path.Combine(directory.FullName, "Assets", "Demo", fileName);
            yield return Path.Combine(directory.FullName, "MyParser", "Assets", "Demo", fileName);
        }
    }
}
