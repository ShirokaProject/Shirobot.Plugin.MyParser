using System.Reflection;
using ShiroBot.SDK.Models;
using ShiroBot.SDK.Plugin;
using Shirobot.Plugin.MyParser.Parsing;

/// <summary>聊天记录四级兜底：合并转发 → 单条多媒体分批 → 长图 → 回复「原图」才发。</summary>
internal static class ForwardFallbackVerification
{
    private static readonly Assembly Plugin = typeof(MyParser.Provider.Douyin.Parsing.DouyinParser).Assembly;

    public static async Task RunAsync()
    {
        // 1. QQ + Raw：一条合并转发。
        var (level, host, messages) = await SendAsync("qq", new MessageCapabilities { NativeFeatures = MessageFeatures.Raw | MessageFeatures.Image }, images: 3);
        Check(level == "Forward" && messages.Sends == 1 && host.Segments.Count == 0, "QQ did not use the merged forward.");

        // 2. Telegram：一组最多 10 张，12 张图 + 正文分两条，正文只在第一条。
        (level, host, messages) = await SendAsync("telegram", new MessageCapabilities { NativeFeatures = MessageFeatures.Image | MessageFeatures.Text, MaxMediaSegments = 10 }, images: 12);
        Check(level == "MediaGroups" && host.Segments.Count == 2, "Media groups were not split by the platform limit.");
        Check(host.Segments[0].OfType<ImageSegment>().Count() == 10 && host.Segments[0][0] is TextSegment
              && host.Segments[1].OfType<ImageSegment>().Count() == 2 && host.Segments[1].OfType<TextSegment>().Count() == 0,
            "Media group batches lost images or repeated the text.");

        // 3. 只能一条一张图：发一张长图。
        (level, host, messages) = await SendAsync("discord", new MessageCapabilities { NativeFeatures = MessageFeatures.Image, MaxMediaSegments = 1 }, images: 3, longImage: true);
        Check(level == "LongImage" && host.ImagesSent == 1 && host.Segments.Count == 0 && messages.Subscriptions.Count == 0, "Single-media platform did not get one long image.");

        // 4. 长图也不行：发提示作为锚点，回复「原图」才逐条发，别的回复不触发。
        (level, host, messages) = await SendAsync("discord", new MessageCapabilities { NativeFeatures = MessageFeatures.Image, MaxMediaSegments = 1 }, images: 3, longImage: false);
        Check(level == "OnReply" && host.Segments.Count == 1 && host.Segments[0][0] is TextSegment && messages.Subscriptions.Count == 1,
            "Missing long image did not fall back to reply-on-demand.");
        await messages.Subscriptions[0](Reply("好看"));
        Check(host.Segments.Count == 1, "Unrelated reply triggered the content.");
        await messages.Subscriptions[0](Reply("原图"));
        Check(host.Segments.Count == 1 + 4, "Reply-on-demand did not send every node separately.");

        // 不要第 4 级（抖音图集）：长图也失败时直接逐条发，不发提示、不登记回复。
        (level, host, messages) = await SendAsync("discord", new MessageCapabilities { NativeFeatures = MessageFeatures.Image, MaxMediaSegments = 1 }, images: 3, longImage: false, replyOnDemand: false);
        Check(level == "Each" && host.Segments.Count == 4 && messages.Subscriptions.Count == 0, "Gallery without reply level did not send each node.");

        // 评论图片：卡片上登记「原图」，回复时才准备内容并按聊天记录发出（QQ 合并转发）。
        var replyHost = DispatchProxy.Create<IProviderHostServices, FallbackHost>();
        var replyMessages = DispatchProxy.Create<IMessageContext, FallbackMessages>();
        ((FallbackMessages)(object)replyMessages).Capabilities = new MessageCapabilities { NativeFeatures = MessageFeatures.Raw | MessageFeatures.Image };
        var subscriptions = Activator.CreateInstance(Plugin.GetType("Shirobot.Plugin.MyParser.MessageHandling.ReplyDownloadSubscriptions", true)!, true)!;
        var senderType = Plugin.GetType("Shirobot.Plugin.MyParser.MessageHandling.ForwardFallbackSender", true)!;
        var sender = Activator.CreateInstance(senderType, [replyMessages, replyHost, subscriptions])!;
        var prepared = 0;
        var packageType = Plugin.GetType("Shirobot.Plugin.MyParser.MessageHandling.ForwardPackage", true)!;
        var prepareType = typeof(Func<,,>).MakeGenericType(typeof(MessageEvent), typeof(CancellationToken), typeof(Task<>).MakeGenericType(packageType));
        var prepare = typeof(ForwardFallbackVerification).GetMethod(nameof(PrepareComments), BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [packageType, new Action(() => prepared++)])!;
        senderType.GetMethod("RegisterReplyFor")!.Invoke(sender, [Message("qq"), "card", prepare]);
        var replyMessagesImpl = (FallbackMessages)(object)replyMessages;
        Check(prepared == 0 && replyMessagesImpl.Subscriptions.Count == 1, "Comment images were prepared before anyone asked.");
        await replyMessagesImpl.Subscriptions[0](Reply("原图") with { Platform = "qq" });
        Check(prepared == 1 && replyMessagesImpl.Sends == 1, "Replying 原图 did not forward the comment images.");

        Console.WriteLine("Forward fallback: QQ forward, platform-limited media groups, long image, reply-on-demand, gallery without reply level and comment images on reply verified.");
    }

    private static async Task<(string Level, FallbackHost Host, FallbackMessages Messages)> SendAsync(
        string platform, MessageCapabilities capabilities, int images, bool longImage = false, bool replyOnDemand = true)
    {
        var host = DispatchProxy.Create<IProviderHostServices, FallbackHost>();
        var messages = DispatchProxy.Create<IMessageContext, FallbackMessages>();
        ((FallbackMessages)messages).Capabilities = capabilities;
        var subscriptions = Activator.CreateInstance(Plugin.GetType("Shirobot.Plugin.MyParser.MessageHandling.ReplyDownloadSubscriptions", true)!, true)!;
        var senderType = Plugin.GetType("Shirobot.Plugin.MyParser.MessageHandling.ForwardFallbackSender", true)!;
        var sender = Activator.CreateInstance(senderType, [messages, host, subscriptions])!;

        var nodeType = Plugin.GetType("Shirobot.Plugin.MyParser.Sdk.OutgoingForwardedMessage", true)!;
        var nodes = Array.CreateInstance(nodeType, images + 1);
        nodes.SetValue(Activator.CreateInstance(nodeType, ["u", "作者", (IReadOnlyList<MessageSegment>)[new TextSegment("正文")]]), 0);
        for (var i = 0; i < images; i++)
            nodes.SetValue(Activator.CreateInstance(nodeType, ["u", "作者", (IReadOnlyList<MessageSegment>)[new ImageSegment($"base64://{i}")]]), i + 1);
        var packageType = Plugin.GetType("Shirobot.Plugin.MyParser.MessageHandling.ForwardPackage", true)!;
        var package = Activator.CreateInstance(packageType, [nodes, "测试"])!;
        // 第 3 级用自定义长图；不给时测试宿主解码不了图片，通用长图为空，会继续走第 4 级。
        packageType.GetProperty("RenderLongImageAsync")!.SetValue(package, longImage
            ? new Func<CancellationToken, Task<string?>>(_ => Task.FromResult<string?>("base64://long"))
            : new Func<CancellationToken, Task<string?>>(_ => Task.FromResult<string?>(null)));
        packageType.GetProperty("ReplyOnDemand")!.SetValue(package, replyOnDemand);
        var task = (Task)senderType.GetMethod("SendAsync")!.Invoke(sender, [Message(platform), package, CancellationToken.None])!;
        await task;
        var level = task.GetType().GetProperty("Result")!.GetValue(task)!.ToString()!;
        return (level, (FallbackHost)(object)host, (FallbackMessages)(object)messages);
    }

    /// <summary>构造 Func&lt;MessageEvent, CancellationToken, Task&lt;ForwardPackage?&gt;&gt;（ForwardPackage 是插件内部类型）。</summary>
    private static object PrepareComments(Type packageType, Action onPrepare)
    {
        var method = typeof(ForwardFallbackVerification).GetMethod(nameof(PrepareTyped), BindingFlags.NonPublic | BindingFlags.Static)!
            .MakeGenericMethod(packageType);
        return method.Invoke(null, [onPrepare])!;
    }

    private static Func<MessageEvent, CancellationToken, Task<T>> PrepareTyped<T>(Action onPrepare) => (_, _) =>
    {
        onPrepare();
        var nodeType = Plugin.GetType("Shirobot.Plugin.MyParser.Sdk.OutgoingForwardedMessage", true)!;
        var nodes = Array.CreateInstance(nodeType, 2);
        for (var i = 0; i < 2; i++)
            nodes.SetValue(Activator.CreateInstance(nodeType, ["u", "评论者", (IReadOnlyList<MessageSegment>)[new TextSegment("评论"), new ImageSegment("base64://c")]]), i);
        return Task.FromResult((T)Activator.CreateInstance(typeof(T), [nodes, "评论区图片"])!);
    };

    private static MessageEvent Message(string platform) => new()
    {
        Platform = platform, InstanceId = "one", SelfId = "bot", MessageId = "source",
        Channel = new Channel("g", ChannelType.Group), Sender = new User("user"), Segments = [],
    };

    private static MessageEvent Reply(string text) => Message("discord") with { MessageId = "reply", Segments = [new TextSegment(text)] };

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}

public class FallbackHost : DispatchProxy
{
    public List<IReadOnlyList<MessageSegment>> Segments { get; } = [];
    public int ImagesSent { get; private set; }
    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        switch (method!.Name)
        {
            case "SendSegmentsAsync": Segments.Add((IReadOnlyList<MessageSegment>)args![1]!); return Task.FromResult(new SentMessage("m" + Segments.Count));
            case "SendImageAsync": ImagesSent++; return Task.FromResult(new SentMessage("long"));
            case "DecodeBase64ImageForRender" or "DecodeImageFileForRender": return null;
            default: throw new InvalidOperationException("Unexpected host call: " + method.Name);
        }
    }
}

public class FallbackMessages : DispatchProxy
{
    public MessageCapabilities Capabilities { get; set; } = new();
    public int Sends { get; private set; }
    public List<ReplyMessageHandler> Subscriptions { get; } = [];
    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        switch (method!.Name)
        {
            case "GetMessageCapabilities": return Capabilities;
            case "SendMessageAsync": Sends++; return Task.FromResult(new SentMessage("forward"));
            case "SubscribeReply":
                Subscriptions.Add((ReplyMessageHandler)args![2]!);
                return DispatchProxy.Create<IReplySubscription, NoopSubscription>();
            default: throw new InvalidOperationException("Unexpected message call: " + method.Name);
        }
    }
}

public class NoopSubscription : DispatchProxy
{
    protected override object? Invoke(MethodInfo? method, object?[]? args) => null;
}
