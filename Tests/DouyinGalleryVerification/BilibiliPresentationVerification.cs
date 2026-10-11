using System.Reflection;
using ShiroBot.SDK.Models;
using ShiroBot.SDK.Plugin;
using Shirobot.Plugin.MyParser;
using Shirobot.Plugin.MyParser.Parsing;

internal static class BilibiliPresentationVerification
{
    public static async Task RunAsync()
    {
        foreach (var scenario in new[] { "dynamic-card", "dynamic-no-card", "live-info", "live-replay" })
        {
            var services = DispatchProxy.Create<IProviderHostServices, BiliPresentationHost>();
            var host = (BiliPresentationHost)services;
            var messages = DispatchProxy.Create<IMessageContext, GalleryMessages>();
            var bot = DispatchProxy.Create<IBotContext, GalleryBot>();
            ((GalleryBot)bot).Messages = messages;
            var live = scenario.StartsWith("live");
            var media = new ParsedMedia
            {
                ProviderId = live ? "bilibili-live" : "bilibili-article", ProviderName = "Bilibili",
                MediaId = live ? "123" : "opus123", Kind = live ? ParsedMediaKind.Live : ParsedMediaKind.Article,
                CoverUrl = "https://example.com/cover.jpg", Title = "测试动态",
                Content = live ? [] : [new() { Kind = MediaContentKind.Text, Text = "动态正文" },
                    new() { Kind = MediaContentKind.Image, Url = "https://example.com/image1.jpg" },
                    new() { Kind = MediaContentKind.Image, Url = "https://example.com/image2.jpg" }],
                Assets = live ? [new() { Kind = MediaAssetKind.LiveStream, Url = "https://example.com/live.m3u8" }] : [],
            };
            var type = typeof(MyParser.Provider.Douyin.Parsing.DouyinParser).Assembly
                .GetType("Shirobot.Plugin.MyParser.MessageHandling.UnifiedMediaMessageHandler", true)!;
            var card = new BiliPresentationCard();
            var handler = Activator.CreateInstance(type, [new ProviderMessageHandlerContext(bot,
                new PluginConfig { SendCoverImages = scenario != "dynamic-no-card", SendBilibiliLiveReplayClip = scenario == "live-replay" },
                new GalleryProvider(), services, card)])!;
            var source = new MessageEvent { Platform = "qq", InstanceId = "test", Channel = new Channel("group", ChannelType.Group),
                MessageId = "source", Sender = new User("tester"), Segments = [] };
            await ((Task)type.GetMethod("PresentAsync", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(handler, [source, media, false, CancellationToken.None])!).WaitAsync(TimeSpan.FromSeconds(5));
            var expectedImages = scenario == "dynamic-no-card" ? 2 : 1;
            if (host.ImagesSent != expectedImages) throw new Exception("Bilibili sent duplicate/missing article images: " + scenario);
            if (host.TextReplies != (scenario == "dynamic-no-card" ? 1 : 0) || ((GalleryMessages)messages).Forwards != 0)
                throw new Exception("Bilibili sent redundant dynamic text or live stream-count text: " + scenario);
            if (host.ImageDownloads != (scenario == "dynamic-no-card" ? 2 : 0)) throw new Exception("Rendered dynamic images were downloaded a second time.");
            if (host.ReplayDownloads != (scenario == "live-replay" ? 1 : 0) || host.VideoBuilds != host.ReplayDownloads)
                throw new Exception("Removing live status text affected replay delivery.");
            if (scenario == "dynamic-card" && card.Purpose != ProviderCardPurpose.Article)
                throw new Exception("Dynamic did not render a full article card.");
        }
        Console.WriteLine("Bilibili dynamic cards avoid duplicate text/images; disabled cards preserve content; live status stays silent and replay delivery remains enabled.");
    }
}

public class BiliPresentationHost : GalleryHost
{
    public int ImageDownloads { get; private set; }
    public int ReplayDownloads { get; private set; }
    public int TextReplies { get; private set; }
    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        if (method!.Name == "ReplyTextAsync") { TextReplies++; return Task.FromResult(new SentMessage("text")); }
        if (method!.Name == "BuildProviderImageAsync") ImageDownloads++;
        if (method.Name == "DownloadLiveReplayClipAsync")
        {
            ReplayDownloads++;
            var request = (ProviderLiveReplayClipDownloadRequest)args![1]!;
            return Task.FromResult(new ProviderLiveReplayClipDownloadResult("file:///tmp/live.mp4", "/tmp/live.mp4",
                request.Streams[0], 1, 1, 30, "/tmp/live.m3u8"));
        }
        return base.Invoke(method, args);
    }
}

internal sealed class BiliPresentationCard : IProviderMediaCardRenderer
{
    public ProviderCardPurpose Purpose { get; private set; }
    public Task<string?> RenderAsync(ParsedMedia media, ProviderCardPurpose purpose, CancellationToken cancellationToken = default)
    {
        Purpose = purpose;
        return Task.FromResult<string?>("base64://full-article");
    }
}
