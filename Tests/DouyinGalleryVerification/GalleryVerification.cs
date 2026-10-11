using System.Reflection;
using ShiroBot.SDK.Models;
using ShiroBot.SDK.Plugin;
using Shirobot.Plugin.MyParser;
using Shirobot.Plugin.MyParser.Parsing;

internal static class GalleryVerification
{
    internal static async Task RunAsync()
    {
        foreach (var scenario in new[] { "single-static", "single-no-music", "multiple-static", "live", "live-mux-failure", "live-without-thumbnail", "live-short", "live-no-music", "mixed", "disabled" })
        {
            var services = DispatchProxy.Create<IProviderHostServices, GalleryHost>();
            var host = (GalleryHost)services;
            host.FailMux = scenario == "live-mux-failure";
            host.MissingThumbnail = scenario == "live-without-thumbnail";
            var messages = DispatchProxy.Create<IMessageContext, GalleryMessages>();
            var bot = DispatchProxy.Create<IBotContext, GalleryBot>();
            ((GalleryBot)bot).Messages = messages;
            var imageCount = scenario is "multiple-static" or "mixed" ? 3 : 1;
            var live = scenario.StartsWith("live") || scenario is "mixed" or "disabled";
            var media = new ParsedMedia
            {
                ProviderId = "douyin", ProviderName = "抖音", MediaId = "gallery-test", Kind = ParsedMediaKind.Gallery, CoverUrl = "https://example.com/cover.jpg",
                Assets = Enumerable.Range(0, imageCount).Select(index => new MediaAsset { Kind = MediaAssetKind.Image, Url = $"https://example.com/{index}.jpg" })
                    .Concat(scenario is "live-no-music" or "single-no-music" ? Array.Empty<MediaAsset>() : [new MediaAsset { Kind = MediaAssetKind.Audio, Url = "https://example.com/music.mp3" }]).ToArray(),
                Attributes = live ? new Dictionary<string, string> { ["gallery_live_photo_0"] = "https://example.com/live.mp4", ["gallery_clip_type_0"] = scenario == "live-short" ? "4" : "1" } : new Dictionary<string, string>(),
            };
            var type = typeof(MyParser.Provider.Douyin.Parsing.DouyinParser).Assembly.GetType("Shirobot.Plugin.MyParser.MessageHandling.UnifiedMediaMessageHandler", true)!;
            var card = new GalleryCard();
            var handler = Activator.CreateInstance(type, [new ProviderMessageHandlerContext(bot,
                new PluginConfig { SendCoverImages = true, EnableSilkEncoding = false, SendVideoSegment = scenario != "disabled" },
                new GalleryProvider(), services, card)])!;
            var source = new MessageEvent { Platform = "qq", InstanceId = "test", Channel = new Channel("group", ChannelType.Group),
                MessageId = "source", Sender = new User("tester"), Segments = [] };
            await ((Task)type.GetMethod("PresentAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(handler, [source, media, false, CancellationToken.None])!).WaitAsync(TimeSpan.FromSeconds(5));
            if (host.StillConversions != 0) throw new Exception("Static images were converted to video.");
            if (live && scenario != "disabled")
            {
                if (host.LoopCount != (scenario == "live-short" ? 1 : 3)) throw new Exception("Incorrect Live Photo loop count: " + scenario);
                if (host.LiveDownloads != 1 || host.VideoBuilds != 1) throw new Exception("Live Photo lost its dynamic video.");
                if (scenario == "live-mux-failure" && host.BuiltVideoPath != "/tmp/live.mp4") throw new Exception("Mux failure discarded the dynamic video.");
            }
            else if (host.LiveDownloads != 0 || host.VideoBuilds != 0) throw new Exception("Static/disabled gallery downloaded a video.");
            var needsVoice = scenario is "single-static" or "multiple-static" or "live-mux-failure" or "mixed" or "disabled";
            if (host.Voices != (needsVoice ? 1 : 0)) throw new Exception("Incorrect separate gallery voice delivery: " + scenario);
            if (card.MainRenders != (scenario is "single-static" or "single-no-music" or "disabled" ? 0 : 1)) throw new Exception("Incorrect gallery cover rendering: " + scenario);
            if (scenario is "single-static" or "single-no-music" && host.ImagesSent != 1) throw new Exception("Single static picture was not sent as an image.");
            if (imageCount > 1 && ((GalleryMessages)messages).Forwards != 1) throw new Exception("Multiple pictures lost their gallery forwarding.");
        }
        Console.WriteLine("Static gallery image/voice, Live Photo, mux fallback, missing-thumbnail and disabled-video verification passed.");
    }
}

internal sealed class GalleryProvider : IParseProvider
{
    public string Id => "douyin";
    public string Name => "抖音";
    public bool CanHandle(string text) => true;
    public Task<ParsedMedia> ParseAsync(string text, CancellationToken cancellationToken = default) => throw new NotSupportedException();
}

public class GalleryHost : DispatchProxy
{
    public int LoopCount { get; private set; }
    public bool FailMux { get; set; }
    public bool MissingThumbnail { get; set; }
    public int StillConversions { get; private set; }
    public int LiveDownloads { get; private set; }
    public int VideoBuilds { get; private set; }
    public int Voices { get; private set; }
    public int ImagesSent { get; private set; }
    public string? BuiltVideoPath { get; private set; }
    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        switch (method!.Name)
        {
            case "BuildProviderImageAsync": return Task.FromResult(new ProviderImageBuildResult(MissingThumbnail ? "" : "base64://image", MissingThumbnail ? null : "/tmp/image.jpg"));
            case "DownloadProviderAudioAsync": return Task.FromResult(("file:///tmp/music.mp3", "/tmp/music.mp3"));
            case "DownloadProviderVideoAsync": LiveDownloads++; return Task.FromResult(("file:///tmp/live.mp4", "/tmp/live.mp4"));
            case "MuxLoopingVideoWithAudioAsync": LoopCount = (int)args![4]!; return FailMux ? Task.FromException<string>(new IOException("mux-failed")) : Task.FromResult("/tmp/merged.mp4");
            case "CreateStillImageVideoWithAudioAsync": StillConversions++; throw new Exception("Unexpected static video conversion.");
            case "BuildLocalVideoSegmentAsync":
                VideoBuilds++; BuiltVideoPath = ((ProviderLocalVideoSegmentRequest)args![1]!).LocalPath;
                return Task.FromResult(new ProviderLocalVideoSegmentResult(new VideoSegment("file:///tmp/live.mp4"), "file", "file:///tmp/live.mp4", 1, false));
            case "BuildRecordUriAsync": return Task.FromResult("file:///tmp/music.mp3");
            case "SendImageAsync": ImagesSent++; return Task.FromResult(new SentMessage("sent"));
            case "SendSegmentsAsync":
                var segments = (IReadOnlyList<MessageSegment>)args![1]!;
                Voices += segments.OfType<AudioSegment>().Count(); ImagesSent += segments.OfType<ImageSegment>().Count();
                return Task.FromResult(new SentMessage("sent"));
        }
        if (method.ReturnType == typeof(Task)) return Task.CompletedTask;
        throw new InvalidOperationException("Unexpected gallery operation: " + method.Name);
    }
}
public class GalleryBot : DispatchProxy
{
    public IMessageContext Messages { get; set; } = null!;
    protected override object? Invoke(MethodInfo? method, object?[]? args) => method!.Name == "get_Message" ? Messages : throw new NotSupportedException();
}
public class GalleryMessages : DispatchProxy
{
    public int Forwards { get; private set; }
    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        if (method!.ReturnType == typeof(Task<SentMessage>)) { Forwards++; return Task.FromResult(new SentMessage("forward")); }
        throw new InvalidOperationException("Unexpected message operation: " + method.Name);
    }
}

internal sealed class GalleryCard : IProviderMediaCardRenderer
{
    public int MainRenders { get; private set; }
    public Task<string?> RenderAsync(ParsedMedia media, ProviderCardPurpose purpose, CancellationToken cancellationToken = default)
    {
        if (purpose == ProviderCardPurpose.Main) MainRenders++;
        return Task.FromResult<string?>("base64://cover");
    }
}
