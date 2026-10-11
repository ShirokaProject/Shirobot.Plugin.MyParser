using Shirobot.Plugin.MyParser.CardRendering;
using System.Runtime.CompilerServices;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;

namespace MyParser.Provider.BiliBili.Views;

public sealed class BiliCardViewModel
{
    public string Title { get; init; } = "这是视频标题";
    public string Description { get; init; } = "这是视频描述这是视频描述这是视频描述这是视频描述这是视频描述这是视频描述这是视频描述这是视频描述";
    public int DescriptionMaxLines { get; init; }
    public double CoverHeight { get; init; } = 290;
    public Thickness ContentMargin { get; init; } = new(14, 175, 14, 14);
    public string AuthorName { get; init; } = "UP主名字";
    public string AuthorMeta { get; init; } = "IP属地：广东";
    public string VideoMeta { get; init; } = "10-24 发布 · 手机游戏 · BV1xx411c7xx";
    public string BrandText { get; init; } = $"By ShirokaProject && MyParser v{PluginVersion}";
    private static string PluginVersion { get; } =
        typeof(BiliCardViewModel).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion.Split('+')[0]
        ?? typeof(BiliCardViewModel).Assembly.GetName().Version?.ToString(3)
        ?? "unknown";
    public string DurationText { get; init; } = "12:34";
    public bool HasMultiPages { get; init; }
    public string CurrentPageText { get; init; } = "当前解析 P1";
    public IReadOnlyList<string> Tags { get; init; } = [];
    public bool HasTags => Tags.Count > 0;
    public bool HasDescription => !string.IsNullOrWhiteSpace(Description);
    public bool HasAuthorMeta => !string.IsNullOrWhiteSpace(AuthorMeta);
    public string ViewCount { get; init; } = "14.8万";
    public string LikeCount { get; init; } = "1.2K";
    public string CoinCount { get; init; } = "345";
    public string CollectCount { get; init; } = "678";
    public string ShareCount { get; init; } = "90";
    public Bitmap? Cover { get; init; } = LoadDesignBackground();
    public Bitmap? Avatar { get; init; } = LoadDesignAvatar();

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
                return fileName == "avatar.jpg" ? AvatarBitmapResizer.DecodeFile(path, 32) : new Bitmap(path);
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
