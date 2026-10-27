using System.Reflection;
using Shirobot.Plugin.MyParser;
using Shirobot.Plugin.MyParser.Parsing;
using ShiroBot.SDK.Abstractions;
using ShiroBot.SDK.Models;
using ShiroBot.SDK.Plugin;

var callbacks = new List<ReplyMessageHandler>();
var subscriptionsDisposed = 0;
var messages = DispatchProxy.Create<IMessageContext, TestProxy>();
((TestProxy)(object)messages).Call = (method, args) =>
{
    Check(method.Name == "SubscribeReply", "Must register a reply subscription.");
    Check(((MessageReference)args![0]!).MessageId == "voice", "Must associate the sent voice message.");
    Check(!(bool)args[3]!, "Unrelated replies must not consume the subscription.");
    callbacks.Add((ReplyMessageHandler)args[2]!);
    var subscription = DispatchProxy.Create<IReplySubscription, TestProxy>();
    ((TestProxy)(object)subscription).Call = (_, _) => { subscriptionsDisposed++; return null; };
    return subscription;
};
var bot = DispatchProxy.Create<IBotContext, TestProxy>();
((TestProxy)(object)bot).Call = (method, _) => method.Name == "get_Message" ? messages : throw new Exception(method.Name);
var provider = DispatchProxy.Create<IParseProvider, TestProxy>();
((TestProxy)(object)provider).Call = (method, _) => method.Name == "get_Id" ? "neteasecloudmusic" : "网易云音乐";
var uploads = new List<string>();
var notices = new List<string>();
var failUpload = false;
var services = DispatchProxy.Create<IProviderHostServices, TestProxy>();
((TestProxy)(object)services).Call = (method, args) => method.Name switch
{
    "SendSegmentsAsync" => Task.FromResult(new SentMessage("voice")),
    "UploadLocalFileAsync" => Upload(args!),
    "ReplyTextAsync" => Notice(args!),
    _ => throw new Exception(method.Name)
};
Task<string> Upload(object?[] args)
{
    if (failUpload) throw new IOException("cache removed");
    uploads.Add((string)args[2]!);
    return Task.FromResult("uploaded");
}
Task<SentMessage> Notice(object?[] args)
{
    notices.Add((string)args[2]!);
    return Task.FromResult(new SentMessage("notice"));
}
var context = new ProviderMessageHandlerContext(bot, new PluginConfig { EnableSilkEncoding = true }, provider, services);
var type = typeof(MyParserPlugin).Assembly.GetType("Shirobot.Plugin.MyParser.MessageHandling.UnifiedMediaMessageHandler", true)!;
using var handler = (IDisposable)Activator.CreateInstance(type, context)!;
var sendTrack = type.GetMethod("SendTrackAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
var source = Message("original");
var media = new ParsedMedia
{
    ProviderId = "neteasecloudmusic", ProviderName = "网易云音乐", MediaId = "123", Kind = ParsedMediaKind.Track,
    Assets = [new MediaAsset { Kind = MediaAssetKind.Audio, Url = "https://example.test/music", FileExtension = "mp3" }]
};
var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".mp3");
var records = Task.FromResult<IReadOnlyList<AudioSegment>>([new AudioSegment("file:///encoded.silk")]);
var succeeded = await (Task<bool>)sendTrack.Invoke(handler, [source, media, CancellationToken.None, path, null, records])!;
Check(succeeded && callbacks.Count == 1, "Voice delivery must register its download callback.");
await callbacks[0](Message("hello"));
await callbacks[0](Message("dl", "other"));
await callbacks[0](Message("dl", instance: "two"));
Check(uploads.Count == 0, "Unrelated replies, groups and instances must not upload.");
await callbacks[0](Message(" DL "));
await callbacks[0](Message("dl"));
Check(uploads.Count == 2 && uploads.All(p => p == path), "Repeated dl replies must upload the original MP3 instead of Silk.");
failUpload = true;
await callbacks[0](Message("dl"));
Check(notices.Last().Contains("过期"), "Missing audio cache must provide recovery instructions.");
handler.Dispose();
Check(subscriptionsDisposed == 1, "Unload must dispose subscriptions.");
await callbacks[0](Message("dl"));
Check(notices.Count == 1, "Disposed subscriptions must not upload.");
Console.WriteLine("PASS: voice reply registration, original MP3 upload with Silk enabled, repeat dl, scene isolation, missing cache and unload.");

static MessageEvent Message(string text, string group = "g", string instance = "one") => new()
{
    Platform = "qq", InstanceId = instance, SelfId = "bot", MessageId = "incoming",
    Channel = Channel.Group(group), Sender = new User("user"), Segments = [new TextSegment(text)]
};
static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
public class TestProxy : DispatchProxy
{
    public Func<MethodInfo, object?[]?, object?> Call { get; set; } = null!;
    protected override object? Invoke(MethodInfo? method, object?[]? args) => Call(method!, args);
}
