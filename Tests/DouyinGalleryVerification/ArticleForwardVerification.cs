using System.Collections;
using System.Reflection;
using ShiroBot.SDK.Models;
using ShiroBot.SDK.Plugin;
using Shirobot.Plugin.MyParser;
using Shirobot.Plugin.MyParser.Parsing;

internal static class ArticleForwardVerification
{
    public static async Task RunAsync()
    {
        foreach (var scenario in new[] { "mixed-links", "text-only", "mixed-video", "non-qq", "no-raw", "forward-rejected" })
        {
            var single = scenario == "text-only";
            var videoEnabled = scenario == "mixed-video";
            var services = DispatchProxy.Create<IProviderHostServices, GalleryHost>();
            var messages = DispatchProxy.Create<IMessageContext, ArticleMessages>();
            ((ArticleMessages)messages).SupportsRaw = scenario != "no-raw";
            ((ArticleMessages)messages).RejectForward = scenario == "forward-rejected";
            var bot = DispatchProxy.Create<IBotContext, GalleryBot>();
            ((GalleryBot)bot).Messages = messages;
            var media = new ParsedMedia
            {
                ProviderId = "heybox", ProviderName = "小黑盒", MediaId = "article", Title = "测试文章",
                AuthorName = "作者", Kind = ParsedMediaKind.Article,
                Assets = [new() { Kind = MediaAssetKind.Video, Url = "https://example.com/video.mp4" }],
                Content = single ? [new() { Kind = MediaContentKind.Text, Text = "只有正文" }]
                    : [new() { Kind = MediaContentKind.Heading, Text = "标题" },
                       new() { Kind = MediaContentKind.Text, Text = "第一段" },
                       new() { Kind = MediaContentKind.Image, Url = "https://example.com/image.jpg", Caption = "图片说明" },
                       new() { Kind = MediaContentKind.Video, Url = "https://example.com/video.mp4" },
                       new() { Kind = MediaContentKind.Text, Text = "最后一段" }],
            };
            var type = typeof(MyParser.Provider.Douyin.Parsing.DouyinParser).Assembly
                .GetType("Shirobot.Plugin.MyParser.MessageHandling.UnifiedMediaMessageHandler", true)!;
            var card = new ArticleCard();
            var handler = Activator.CreateInstance(type, [new ProviderMessageHandlerContext(bot,
                new PluginConfig { SendCoverImages = true, SendVideoSegment = videoEnabled }, new GalleryProvider(), services, card)])!;
            var source = new MessageEvent { Platform = scenario == "non-qq" ? "telegram" : "qq", InstanceId = "test", Channel = new Channel("group", ChannelType.Group),
                MessageId = "source", Sender = new User("tester"), Segments = [] };
            await ((Task)type.GetMethod("PresentAsync", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(handler, [source, media, false, CancellationToken.None])!).WaitAsync(TimeSpan.FromSeconds(5));
            var captured = (ArticleMessages)messages;
            if (scenario is "non-qq" or "no-raw" or "forward-rejected")
            {
                if (card.ArticleRenders != 1 || ((GalleryHost)services).ImagesSent != 1 || captured.Forward is not null)
                    throw new Exception("Unsupported forward did not fall back to the full article document.");
                if (captured.Sends != (scenario == "forward-rejected" ? 1 : 0)) throw new Exception("Unsupported platform received a flattened article.");
                continue;
            }
            if (captured.Sends != 1 || captured.Forward is null) throw new Exception("Article escaped the merged forward.");
            var expected = single ? new[] { "只有正文" } : new[] { "【标题】", "第一段", "base64://image", "图片说明",
                videoEnabled ? "file:///tmp/live.mp4" : "正文视频：https://example.com/video.mp4", "最后一段" };
            // Inspect node payloads rather than preview text, which also contains the article text.
            var nodes = (IEnumerable)captured.Forward.GetType().GetProperty("Messages")!.GetValue(captured.Forward)!;
            var payload = string.Join("\n", nodes.Cast<object>().SelectMany(node =>
                ((IEnumerable)node.GetType().GetProperty("Segments")!.GetValue(node)!).Cast<object>()).Select(segment =>
                System.Text.Json.JsonSerializer.Serialize(segment, segment.GetType(), new System.Text.Json.JsonSerializerOptions
                { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping })));
            var previous = -1;
            foreach (var value in expected)
            {
                var position = payload.IndexOf(value, StringComparison.Ordinal);
                if (position <= previous) throw new Exception("Article content lost or reordered: " + value);
                previous = position;
            }
            if (((GalleryHost)services).ImagesSent != 0) throw new Exception("Article image was sent outside the forward.");
            if (((GalleryHost)services).VideoBuilds != (videoEnabled ? 1 : 0)) throw new Exception("Article video delivery setting was ignored.");
        }
        Console.WriteLine("Heybox article text, images and video links remain ordered inside one merged forward, including text-only articles.");
    }
}

public class ArticleMessages : DispatchProxy
{
    public bool SupportsRaw { get; set; } = true;
    public bool RejectForward { get; set; }
    public int Sends { get; private set; }
    public object? Forward { get; private set; }
    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        if (method!.Name == "GetMessageCapabilities") return new MessageCapabilities { NativeFeatures = SupportsRaw ? MessageFeatures.Raw : MessageFeatures.Image };
        if (method!.Name != "SendMessageAsync") throw new Exception("Article sent ordinary replies: " + method.Name);
        Sends++;
        if (RejectForward) return Task.FromException<SentMessage>(new NotSupportedException("Forward is unsupported by this adapter."));
        var segments = (IReadOnlyList<MessageSegment>)args![1]!;
        if (segments.Count != 1 || segments[0] is not RawSegment raw || raw.Payload is not ShiroBot.Model.QQ.QOutgoingForward)
            throw new Exception("Article was not sent as a native QQ forward.");
        Forward = raw.Payload;
        return Task.FromResult(new SentMessage("article-forward"));
    }
}

internal sealed class ArticleCard : IProviderMediaCardRenderer
{
    public int ArticleRenders { get; private set; }
    public Task<string?> RenderAsync(ParsedMedia media, ProviderCardPurpose purpose, CancellationToken cancellationToken = default)
    {
        if (purpose == ProviderCardPurpose.Article) { ArticleRenders++; return Task.FromResult<string?>("base64://article"); }
        return Task.FromResult<string?>(null);
    }
}
