using System.Collections.Concurrent;
using MyParser.Provider.NetEaseCloudMusic.Parsing;
using ShiroBot.SDK.Models;
using ShiroBot.SDK.Plugin;

namespace MyParser.Provider.NetEaseCloudMusic;

[MyParserProvider("neteasecloudmusic")]
public sealed class NetEaseCloudMusicProviderModule : MyParserProviderModuleBase, IProviderTextNormalizer, IIncomingProviderTextNormalizer, ICookieValidator, IProviderCookieStore, IProviderAutoParsePolicy, IProviderAvailabilityPolicy, IProviderResultMessageClassifier, IProviderCommandContributor, IProviderReplyParseTextBuilder, IProviderMediaCardRendererFactory
{
    private static readonly ConcurrentDictionary<string, IReadOnlyList<long>> SearchReplySongIds = new(StringComparer.Ordinal);

    public override string Id => "neteasecloudmusic";
    public override string DisplayName => "网易云音乐";

    public IProviderMediaCardRenderer CreateMediaCardRenderer(ProviderCardRenderContext context) => new NetEaseMediaCardRenderer(context);

    public IReadOnlyList<ProviderCookieDescriptor> CookieDescriptors =>
    [
        new(
            Id,
            DisplayName,
            "netease.txt",
            cookie => MyParserRuntime.NetEaseCloudMusicCookie = cookie,
            LooksLikeCookie,
            EmptyHint: "请编辑 cookies/netease.txt，保存后自动重载；无 Cookie 仍可搜索，VIP/高音质通常不可用。",
            InvalidHint: "请填入网易云网页请求中的完整 Cookie，建议包含 MUSIC_U/__csrf/NMTID。")
    ];

    public override IReadOnlyList<IParseProvider> CreateProviders(PluginConfig config)
    {
        if (!config.EnableNetEaseCloudMusic)
        {
            return [];
        }

        return [new NetEaseParseProvider(new NetEaseParser(config))];
    }

    public string? NormalizeParseText(string text) => NetEaseUrlParser.NormalizeParseText(text);

    public string? NormalizeParseText(IncomingMessage message)
    {
        var text = GetPlainText(message);
        if (NetEaseUrlParser.ContainsNetEaseSongUrl(text))
        {
            return NetEaseUrlParser.NormalizeParseText(text);
        }

        return NetEaseLightAppUrlExtractor.ExtractParseText(message);
    }

    public bool LooksLikeCookie(string cookie) => NetEaseParser.LooksLikeCookie(cookie);

    public bool IsAutoParseEnabled(PluginConfig config) => config.EnableNetEaseCloudMusic && config.AutoParseNetEaseCloudMusicLinks;

    public bool IsProviderEnabled(PluginConfig config) => config.EnableNetEaseCloudMusic;

    public bool IsPluginResultMessage(string text) => text.StartsWith("网易云音乐解析", StringComparison.OrdinalIgnoreCase);

    public IReadOnlyList<ProviderCommandDescriptor> CreateCommands(ProviderCommandContext context)
    {
        if (!context.Config.EnableNetEaseCloudMusic)
        {
            return [];
        }

        return
        [
            new ProviderCommandDescriptor("#wyy", message => HandleSearchCommandAsync(context, message)),
        ];
    }

    public string? TryBuildParseText(IncomingMessage message)
    {
        var text = GetPlainText(message).Trim();
        if (!int.TryParse(text, out var index) || index <= 0) return null;
        var reply = message.GetQqReply();
        if (reply is null) return null;
        var repliedText = reply.GetPlainText().Trim();
        var ids = TryPickDeferredSongIds(repliedText, index)
                  ?? TryGetCachedSearchReplySongIds(message, reply.MessageId, index);
        return ids is null ? null : NetEaseUrlParser.BuildInternalPickUri(ids, index - 1);
    }

    public bool IsDeferredParseText(string text) => TryPickDeferredSongIds(text, 1) is not null;

    private static async Task HandleSearchCommandAsync(ProviderCommandContext context, IncomingMessage message)
    {
        if (!context.Config.EnableNetEaseCloudMusic)
        {
            await context.BotContext.Message.ReplyAsync(message, "网易云音乐解析已关闭。");
            return;
        }

        var keyword = GetPlainText(message).TrimStart();
        if (keyword.StartsWith("#wyy", StringComparison.OrdinalIgnoreCase)) keyword = keyword[4..].Trim();
        if (string.IsNullOrWhiteSpace(keyword))
        {
            await context.BotContext.Message.ReplyAsync(message, "用法：#wyy <歌名/歌手>。机器人会返回候选列表，回复序号即可发送 QQ 语音。");
            return;
        }

        if (context.PrimaryProvider is not NetEaseParseProvider provider)
        {
            await context.BotContext.Message.ReplyAsync(message, "网易云音乐解析器尚未初始化。");
            return;
        }

        var songs = await provider.Parser.SearchAsync(keyword, 10).ConfigureAwait(false);
        if (songs.Count == 0)
        {
            await context.BotContext.Message.ReplyAsync(message, "未搜索到网易云歌曲：" + keyword);
            return;
        }

        var lines = new List<string> { "网易云音乐搜索结果：" };
        var songIds = new List<long>(songs.Count);
        var i = 1;
        foreach (var song in songs)
        {
            lines.Add($"{i}. {song.Name} - {song.Artists}《{song.Album}》 [id:{song.Id}]");
            songIds.Add(song.Id);
            i++;
        }
        lines.Add("回复本消息序号即可解析并发送 QQ 语音。 ");
        var response = await context.BotContext.Message.ReplyAsync(message, string.Join(Environment.NewLine, lines));
        if (!string.IsNullOrWhiteSpace(response.MessageId))
        {
            SearchReplySongIds[BuildSearchReplyCacheKey(message, response.MessageId)] = songIds;
        }
    }

    private static IReadOnlyList<long>? TryGetCachedSearchReplySongIds(IncomingMessage message, string replyMessageId, int index)
    {
        if (string.IsNullOrWhiteSpace(replyMessageId)) return null;
        return SearchReplySongIds.TryGetValue(BuildSearchReplyCacheKey(message, replyMessageId), out var ids)
               && index > 0
               && index <= ids.Count
            ? ids
            : null;
    }

    private static string BuildSearchReplyCacheKey(IncomingMessage message, string messageId) =>
        $"{message.Channel.Type}:{message.Channel.Id}:{messageId}";

    private static IReadOnlyList<long>? TryPickDeferredSongIds(string text, int index)
    {
        if (!text.StartsWith("网易云音乐搜索结果：", StringComparison.OrdinalIgnoreCase)) return null;
        var ids = new List<long>();
        foreach (var line in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var marker = "[id:";
            var start = line.LastIndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (start < 0) continue;
            start += marker.Length;
            var end = line.IndexOf(']', start);
            var idText = end < 0 ? line[start..] : line[start..end];
            if (long.TryParse(idText, out var id) && id > 0)
            {
                ids.Add(id);
            }
        }

        return index > 0 && index <= ids.Count ? ids : null;
    }

    private static string GetPlainText(IncomingMessage message) => message.GetPlainText();
}
