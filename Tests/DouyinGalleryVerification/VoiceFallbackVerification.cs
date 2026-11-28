using System.Reflection;
using ShiroBot.SDK.Models;
using ShiroBot.SDK.Plugin;
using Shirobot.Plugin.MyParser;
using Shirobot.Plugin.MyParser.Parsing;

/// <summary>语音三级兜底：原音频直接发语音 → 需要的地方编码 Silk → 都不行上传原 MP3 文件。</summary>
internal static class VoiceFallbackVerification
{
    private static readonly Assembly Plugin = typeof(MyParser.Provider.Douyin.Parsing.DouyinParser).Assembly;

    public static async Task RunAsync()
    {
        // Telegram 支持语音：直接发原 MP3，不编码。
        var (level, host) = await SendAsync("telegram", MessageFeatures.Audio, silk: true);
        Check(level == "Original" && host.Records.Single() == "file:///tmp/song.mp3" && host.SilkBuilds == 0, "Voice-capable platform did not get the original audio.");

        // QQ 开启 Silk：直接发 Silk，不先试原音频。
        (level, host) = await SendAsync("qq", MessageFeatures.Audio, silk: true);
        Check(level == "Silk" && host.Records.Single() == "file:///tmp/song.silk" && host.SilkBuilds == 1, "QQ with Silk enabled did not send Silk.");

        // QQ 没开 Silk、原音频被拒：再编码 Silk 试一次。
        (level, host) = await SendAsync("qq", MessageFeatures.Audio, silk: false, rejectOriginal: true);
        Check(level == "Silk" && host.SilkBuilds == 1 && host.Uploads == 0, "Rejected original audio on QQ did not retry with Silk.");

        // 平台不支持语音：直接上传原 MP3 文件。
        (level, host) = await SendAsync("discord", MessageFeatures.Image | MessageFeatures.File, silk: true);
        Check(level == "File" && host.Records.Count == 0 && host.Uploads == 1 && host.SilkBuilds == 0, "Platform without voice did not get the MP3 file.");

        // 语音全部失败：上传原 MP3 文件。
        (level, host) = await SendAsync("qq", MessageFeatures.Audio, silk: true, rejectAll: true);
        Check(level == "File" && host.Uploads == 1, "Failed voice did not fall back to the MP3 file.");

        Console.WriteLine("Voice fallback: original voice, Silk where needed, Silk retry on QQ and MP3 file fallback verified.");
    }

    private static async Task<(string Level, VoiceHost Host)> SendAsync(string platform, MessageFeatures features, bool silk,
        bool rejectOriginal = false, bool rejectAll = false)
    {
        var host = DispatchProxy.Create<IProviderHostServices, VoiceHost>();
        ((VoiceHost)(object)host).RejectOriginal = rejectOriginal || rejectAll;
        ((VoiceHost)(object)host).RejectSilk = rejectAll;
        var messages = DispatchProxy.Create<IMessageContext, FallbackMessages>();
        ((FallbackMessages)(object)messages).Capabilities = new MessageCapabilities { NativeFeatures = features };
        var senderType = Plugin.GetType("Shirobot.Plugin.MyParser.MessageHandling.VoiceFallbackSender", true)!;
        var sender = Activator.CreateInstance(senderType, [messages, host, new PluginConfig { EnableSilkEncoding = silk }])!;
        var packageType = Plugin.GetType("Shirobot.Plugin.MyParser.MessageHandling.VoicePackage", true)!;
        var package = Activator.CreateInstance(packageType, ["neteasecloudmusic", "网易云音乐", "1", "/tmp/song.mp3", "song", null, null])!;
        var message = new MessageEvent
        {
            Platform = platform, InstanceId = "one", SelfId = "bot", MessageId = "source",
            Channel = new Channel("g", ChannelType.Group), Sender = new User("user"), Segments = [],
        };
        var task = (Task)senderType.GetMethod("SendAsync")!.Invoke(sender, [message, package, CancellationToken.None])!;
        await task;
        return (task.GetType().GetProperty("Result")!.GetValue(task)!.ToString()!, (VoiceHost)(object)host);
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}

public class VoiceHost : DispatchProxy
{
    public bool RejectOriginal { get; set; }
    public bool RejectSilk { get; set; }
    public List<string> Records { get; } = [];
    public int SilkBuilds { get; private set; }
    public int Uploads { get; private set; }
    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        switch (method!.Name)
        {
            case "BuildRecordUriAsync": return Task.FromResult("file://" + (string)args![0]!);
            case "BuildSilkRecordVariantsAsync":
                SilkBuilds++;
                return Task.FromResult<IReadOnlyList<ProviderRecordVariant>>([new ProviderRecordVariant("silk", "Silk", "test", "/tmp/song.silk", 24000)]);
            case "SendSegmentsAsync":
                var uri = ((AudioSegment)((IReadOnlyList<MessageSegment>)args![1]!)[0]).Uri;
                if (uri.EndsWith(".mp3") && RejectOriginal || uri.EndsWith(".silk") && RejectSilk)
                    return Task.FromException<SentMessage>(new InvalidOperationException("rejected"));
                Records.Add(uri);
                return Task.FromResult(new SentMessage("voice"));
            case "UploadLocalFileAsync": Uploads++; return Task.FromResult("uploaded");
            default: throw new InvalidOperationException("Unexpected host call: " + method.Name);
        }
    }
}
