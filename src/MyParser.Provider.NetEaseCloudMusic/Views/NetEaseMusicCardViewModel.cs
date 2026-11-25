using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Shirobot.Plugin.MyParser.CardRendering;

namespace MyParser.Provider.NetEaseCloudMusic.Views;

/// <summary>
/// 网易云音乐卡片。与视频卡片同一视觉语言：专辑封面模糊氛围底 + 平台双色调 + 毛玻璃面板。
/// </summary>
public sealed partial class NetEaseMusicCardViewModel
{
    public MediaCardPlatform Platform => MediaCardPlatform.NetEase;
    public IBrush AccentGradient => Platform.CreateGradient(1, 1);
    public IBrush AccentSoftGradient => Platform.CreateGradient(0.13, 0.13);
    public IBrush AccentTintGradient => Platform.CreateGradient(0.2, 0.16);
    public IBrush BadgeBrush => new SolidColorBrush(Platform.BadgeBackground);

    /// <summary>原尺寸专辑封面，用于氛围底和唱片中心。</summary>
    public Bitmap? Cover { get; init; } = LoadDesignCover();
    public bool HasCover => Cover is not null;
    /// <summary>缩放后的专辑封面，用于左上角封面。</summary>
    public Bitmap? Avatar { get; init; } = LoadDesignCover(avatar: true);
    public bool HasAvatar => Avatar is not null;

    public string Title { get; init; } = "电波妄想美少女Q";
    public string Artists { get; init; } = "格瑞普喵";
    public string Album { get; init; } = "MuseDash 特别集";
    public bool HasAlbum => !string.IsNullOrWhiteSpace(Album);
    public string DurationText { get; init; } = "3:45";
    public bool HasDuration => !string.IsNullOrWhiteSpace(DurationText);

    /// <summary>"词 xx · 曲 yy"。</summary>
    public string Credits { get; init; } = "词 格瑞普 · 曲 格瑞普";
    public bool HasCredits => !string.IsNullOrWhiteSpace(Credits);

    public IReadOnlyList<NetEaseLyricLine> Lyrics { get; init; } =
    [
        new("妄想のステップ刻んで、キミのハートにフルコン", "踏着妄想的舞步，向你的心打出全连"),
        new("響けメロディ、夜空を突き抜けて", "响彻吧旋律，穿透夜空"),
        new("指先が紡ぐ奇跡、誰も止められない", "指尖编织的奇迹，谁也无法阻止"),
        new("夢のステージへ、もう一度手を伸ばして", "向着梦想的舞台，再一次伸出手"),
        new("示例歌词第五行，跟着节奏向前", ""),
        new("示例歌词第六行，让旋律继续回响", ""),
    ];
    public bool HasLyrics => Lyrics.Count > 0;
    public bool HasNoLyrics => Lyrics.Count == 0;
    public string EmptyLyricText { get; init; } = "暂无歌词，请欣赏";

    public string SongIdText { get; init; } = "1234567890";
    public string FooterText => string.IsNullOrWhiteSpace(SongIdText) ? Platform.Name : $"{Platform.Name} · {SongIdText}";

    public string BrandText { get; } = "By ShirokaProject && MyParser v" +
        (typeof(NetEaseMusicCardViewModel).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion.Split('+')[0] ?? "unknown");

    private static Bitmap? LoadDesignCover(bool avatar = false, [CallerFilePath] string sourcePath = "")
    {
        if (!Design.IsDesignMode) return null;
        var path = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourcePath)!, "..", "..", "MyParser", "Assets", "Demo", "avatar.jpg"));
        return File.Exists(path) ? (avatar ? AvatarBitmapResizer.DecodeFile(path, 92) : new Bitmap(path)) : null;
    }

    // ---------------- 格式化 ----------------

    public static string FormatDuration(string? milliseconds) =>
        long.TryParse(milliseconds, out var ms) && ms > 0 ? MediaCardViewModel.FormatDuration(TimeSpan.FromMilliseconds(ms)) : string.Empty;

    // ---------------- 歌词 ----------------

    /// <summary>
    /// 解析 LRC：去时间轴、按时间戳对齐翻译，并把开头的 "作词 : xx" 等制作信息单独取出。
    /// 新版歌词开头的 JSON 行（{"t":0,"c":[{"tx":"作词: "},…]}）同样按制作信息处理。
    /// </summary>
    public static (IReadOnlyList<NetEaseLyricLine> Lines, string Credits) ParseLyrics(string? lyrics, string? translated, int maxLines)
    {
        var credits = new List<(string Role, string Name)>();
        var lines = new List<(long? Time, string Text)>();
        var inHeader = true;
        foreach (var (time, text) in ReadLrc(lyrics))
        {
            if (inHeader && TryParseCredit(text, out var role, out var name))
            {
                credits.Add((role, name));
                continue;
            }

            inHeader = false;
            lines.Add((time, text));
        }

        var translations = new Dictionary<long, string>();
        foreach (var (time, text) in ReadLrc(translated))
        {
            if (time is not { } key || TryParseCredit(text, out _, out _)) continue;
            translations.TryAdd(key, text);
        }

        var result = lines
            .Select(line => new NetEaseLyricLine(line.Text,
                line.Time is { } key && translations.TryGetValue(key, out var translation) && translation != line.Text ? translation : string.Empty))
            .Take(Math.Max(0, maxLines))
            .ToArray();
        return (result, FormatCredits(credits));
    }

    private static IEnumerable<(long? Time, string Text)> ReadLrc(string? lyrics)
    {
        if (string.IsNullOrWhiteSpace(lyrics)) yield break;
        foreach (var raw in lyrics.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = raw.Trim();
            if (line.StartsWith('{'))
            {
                var jsonText = TryReadJsonLine(line);
                if (!string.IsNullOrWhiteSpace(jsonText)) yield return (null, jsonText);
                continue;
            }

            long? time = null;
            var match = TimeTagRegex().Match(line);
            if (match.Success)
            {
                var minutes = long.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
                var seconds = long.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
                var fraction = match.Groups[3].Success ? match.Groups[3].Value.PadRight(3, '0')[..3] : "000";
                time = (minutes * 60 + seconds) * 1000 + long.Parse(fraction, CultureInfo.InvariantCulture);
            }

            var text = LeadingTagsRegex().Replace(line, string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(text)) continue;
            // [ar:xx] / [ti:xx] 之类的 ID 标签整行都是标签，会被上面替换为空
            yield return (time, text);
        }
    }

    private static string? TryReadJsonLine(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            if (!doc.RootElement.TryGetProperty("c", out var parts) || parts.ValueKind != JsonValueKind.Array) return null;
            return string.Concat(parts.EnumerateArray()
                .Select(part => part.TryGetProperty("tx", out var tx) && tx.ValueKind == JsonValueKind.String ? tx.GetString() : null))
                .Trim();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool TryParseCredit(string text, out string role, out string name)
    {
        var match = CreditRegex().Match(text);
        role = match.Success ? match.Groups[1].Value.Trim() : string.Empty;
        name = match.Success ? match.Groups[2].Value.Trim() : string.Empty;
        return match.Success;
    }

    private static string FormatCredits(IEnumerable<(string Role, string Name)> credits)
    {
        var parts = new List<string>();
        foreach (var (role, name) in credits)
        {
            var shortRole = role switch
            {
                "作词" or "词" or "Lyricist" or "Lyrics" => "词",
                "作曲" or "曲" or "Composer" or "Music" => "曲",
                "编曲" or "Arranger" => "编曲",
                _ => null,
            };
            if (shortRole is null || parts.Any(part => part.StartsWith(shortRole + " ", StringComparison.Ordinal))) continue;
            parts.Add($"{shortRole} {name}");
        }

        return string.Join(" · ", parts);
    }

    [GeneratedRegex(@"^\[(\d{1,3}):(\d{1,2})(?:[.:](\d{1,3}))?\]")]
    private static partial Regex TimeTagRegex();

    [GeneratedRegex(@"^(?:\[[^\]]*\])+")]
    private static partial Regex LeadingTagsRegex();

    /// <summary>"作词 : 张三" / "Composer：Foo"，角色名不超过 10 个字符且不含空格以外的标点。</summary>
    [GeneratedRegex(@"^([\p{L}\p{N} /&]{1,10}?)\s*[:：]\s*(\S.*)$")]
    private static partial Regex CreditRegex();
}

public sealed record NetEaseLyricLine(string Text, string Translation)
{
    public bool HasTranslation => !string.IsNullOrWhiteSpace(Translation);
}
