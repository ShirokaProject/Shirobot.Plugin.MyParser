using Shirobot.Plugin.MyParser.CardRendering;
using Avalonia.Media.Imaging;
using Avalonia.Controls;
using System.Runtime.CompilerServices;
using System.Reflection;

namespace MyParser.Provider.NetEaseCloudMusic.Views;

public sealed class NetEaseMusicCardViewModel
{
    public Bitmap? Cover { get; init; } = LoadDesignCover();
    public Bitmap? Avatar { get; init; } = LoadDesignCover(avatar: true);
    public string Title { get; init; } = "歌曲标题";
    public string Artists { get; init; } = "歌手";
    public string Album { get; init; } = "专辑";
    public string Subtitle => string.Join(" · ", new[] { Artists, Album }.Where(value => !string.IsNullOrWhiteSpace(value)));
    private readonly string _lyricText = "无歌词";
    public string LyricText
    {
        get => _lyricText;
        init => _lyricText = string.IsNullOrWhiteSpace(value) ? "无歌词" : value;
    }
    public bool HasLyrics => LyricText != "无歌词";
    public string BrandText { get; } = "ShirokaProject && MyParser v" +
        (typeof(NetEaseMusicCardViewModel).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion.Split('+')[0] ?? "unknown");

    private static Bitmap? LoadDesignCover(bool avatar = false, [CallerFilePath] string sourcePath = "")
    {
        if (!Design.IsDesignMode) return null;
        var path = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourcePath)!, "..", "..", "MyParser", "Assets", "Demo", "avatar.jpg"));
        return File.Exists(path) ? (avatar ? AvatarBitmapResizer.DecodeFile(path, 52) : new Bitmap(path)) : null;
    }

    public string QualityText { get; init; } = "音质";
    public string SizeText { get; init; } = "大小";
    public string BitrateText { get; init; } = "码率";
    public string SongIdText { get; init; } = "ID";
    public string SourceText { get; init; } = "网易云音乐";
}
