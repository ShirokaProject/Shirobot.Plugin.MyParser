using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using MyParser.Provider.BiliBili.Parsing;
using MyParser.Provider.BiliBili;
using MyParser.Provider.BiliBili.Services;
using ShiroBot.SDK.Models;
using ShiroBot.SDK.Plugin;
using MyParser.Provider.BiliBili.Views;
using Shirobot.Plugin.MyParser;
using Shirobot.Plugin.MyParser.Parsing;

var cookieProperty = typeof(PluginConfig).Assembly.GetType("Shirobot.Plugin.MyParser.MyParserRuntime", true)!.GetProperty("BilibiliCookie")!;
var previousCookie = cookieProperty.GetValue(null);
cookieProperty.SetValue(null, "SESSDATA=verification; bili_jct=verification");
try
{
    using var handler = new MetadataHandler();
    using var http = new HttpClient(handler);
    using var provider = new BilibiliParseProvider(new BilibiliParser(new PluginConfig(), http));
    var media = await provider.ParseAsync("https://www.bilibili.com/video/BV1rxp86jEYH/?p=1");
    var model = CreateViewModel(media);
    Check(model.VideoMeta == "2026-10-10 发布 · 搞笑 · BV1rxp86jEYH", "Publish time/category/BVID did not reach the card.");
    Check(model.AuthorMeta == "IP属地：云南", "APP pub_location did not reach the card.");
    Check(model.Tags.SequenceEqual(new[] { "#搞笑", "#原创" }), "Actual tags were lost or replaced with stream quality.");
    Check(model.ViewCount == "14.8万" && model.LikeCount == "2.6万" && model.CoinCount == "7,305" && model.CollectCount == "9,142", "Statistics did not reach the card.");
    Check(model.AuthorName == "测试UP" && model.DurationText == "2:50", "Author/duration did not reach the card.");
    Check(handler.AppRequests == 1 && handler.TagRequests == 1, "Missing supplementary requests.");

    handler.MultiPage = true;
    var playRequests = handler.PlayRequests;
    var defaultPart = await provider.ParseAsync("https://www.bilibili.com/video/BV1rxp86jEYH/");
    var defaultModel = CreateViewModel(defaultPart);
    Check(defaultPart.Kind == ParsedMediaKind.Video && defaultPart.Assets.Any(asset => asset.Kind == MediaAssetKind.Video),
        "A default multi-P link must resolve a playable P1 video.");
    Check(handler.PlayRequests == playRequests + 1 && handler.LastPlayCid == "42606461174",
        "A default multi-P link must request P1 playback streams.");
    Check(defaultModel.DurationText == "2:50" && defaultModel.HasMultiPages && defaultModel.CurrentPageText == "当前解析 P1/2"
        && defaultModel.ViewCount == "14.8万" && defaultModel.AuthorMeta == "IP属地：云南" && defaultModel.Tags.Count == 2,
        "Default P1 lost its metadata or multi-P navigation.");
    foreach (var link in new[] {
        "【Rust重写的Minecraft 26.3 还支持光影？Horizon试玩】 https://www.bilibili.com/video/BV1afnW6kEfX/?share_source=copy_web&vd_source=f12f1973d4edd97492e05ac5acbe5d10",
        "https://www.bilibili.com/video/BV1rxp86jEYH/?p=1",
    })
    {
        var firstPart = await provider.ParseAsync(link);
        Check(firstPart.Kind == ParsedMediaKind.Video && firstPart.Attributes["requested_page"] == "1"
            && handler.LastPlayCid == "42606461174", "Default share parameters or explicit P1 failed to select P1.");
    }
    playRequests = handler.PlayRequests;
    var selectedPart = await provider.ParseAsync("https://www.bilibili.com/video/BV1rxp86jEYH/?p=2");
    Check(selectedPart.Kind == ParsedMediaKind.Video && handler.PlayRequests == playRequests + 1 && handler.LastPlayCid == "42606461175",
        "Explicit part selection must still resolve a playable video.");
    handler.SilentVideo = true;
    var silentVideo = await provider.ParseAsync("https://www.bilibili.com/video/BV1rxp86jEYH/?p=2");
    Check(silentVideo.Assets.Count == 1 && silentVideo.Assets[0].Kind == MediaAssetKind.Video,
        "A valid DASH video without an audio track was rejected.");
    handler.SilentVideo = false;
    handler.MissingVideo = true;
    try
    {
        await provider.ParseAsync("https://www.bilibili.com/video/BV1rxp86jEYH/?p=2");
        throw new InvalidOperationException("Missing video streams were accepted.");
    }
    catch (MyParser.Provider.BiliBili.Models.BilibiliParseException ex)
    {
        Check(ex.Message.Contains("page=2") && !ex.Message.Contains("Cookie"), "Missing DASH video was incorrectly blamed on Cookie.");
    }
    handler.MissingVideo = false;
    Check(CreateViewModel(selectedPart).HasMultiPages && CreateViewModel(selectedPart).CurrentPageText == "当前解析 P2/2",
        "Selected part did not retain the multi-P navigation bar.");
    var source = new MessageEvent { Platform="qq", InstanceId="test-bot", Channel=new Channel("group-a", ChannelType.Group),
        MessageId="source", Sender=new User("tester"), Segments=[new TextSegment("link")] };
    var fakeServices = DispatchProxy.Create<IProviderHostServices, RecordingHostServices>();
    var recorder = (RecordingHostServices)fakeServices;
    var handlerType = typeof(BiliCard).Assembly.GetType("Shirobot.Plugin.MyParser.MessageHandling.UnifiedMediaMessageHandler", true)!;
    var presenter = Activator.CreateInstance(handlerType,
        [new ProviderMessageHandlerContext(null!, new PluginConfig(), provider, fakeServices, new PreviewCardRenderer())])!;
    await (Task)handlerType.GetMethod("PresentAsync", BindingFlags.NonPublic|BindingFlags.Instance)!
        .Invoke(presenter, [source, defaultPart with { CoverUrl="https://example.com/cover.jpg" }, false, CancellationToken.None])!;
    Check(recorder.Messages.SequenceEqual(new[]{"download", "image", "video"}), "Default multi-P link did not send the P1 card and video.");
    await MediaConcurrencyVerification.RunAsync(handlerType, source, provider, selectedPart);
    var module = new BilibiliProviderModule();
    var numeric = source with { MessageId="pick", Segments=[new QuoteSegment("card-1"),new TextSegment("2")] };
    var selectedUrl = module.TryBuildParseText(numeric);
    Check(selectedUrl == "https://www.bilibili.com/video/BV1rxp86jEYH/?p=2", "Replying to the image did not resolve P2.");
    Check(module.TryBuildParseText(numeric with { Channel=new Channel("group-b", ChannelType.Group) }) is null,
        "Card reply leaked into a different group.");
    Check(module.TryBuildParseText(numeric with { InstanceId="other-bot" }) is null, "Card reply leaked into a different bot.");
    Check(module.TryBuildParseText(numeric with { Segments=[new QuoteSegment("card-1"),new TextSegment("3")] }) is null,
        "Out-of-range page number was accepted.");
    var selectedReplyPart = await provider.ParseAsync(selectedUrl!);
    Check(selectedReplyPart.Attributes["preview_only"] == "false" && selectedReplyPart.Assets.Any(asset => asset.Kind == MediaAssetKind.Video),
        "Native multi-P reply still produced only a preview.");
    recorder.Messages.Clear();
    await (Task)handlerType.GetMethod("PresentAsync", BindingFlags.NonPublic|BindingFlags.Instance)!
        .Invoke(presenter,[source,selectedReplyPart with { CoverUrl="https://example.com/cover.jpg" },false,CancellationToken.None])!;
    Check(recorder.Messages.SequenceEqual(new[]{"download", "image", "video"}), "Native multi-P reply did not send the chosen video.");
    playRequests = handler.PlayRequests;
    var preview = await provider.ParseAsync(selectedUrl! + "#myparser-preview");
    Check(preview.Attributes["preview_only"] == "true" && preview.Assets.Count == 0 && handler.PlayRequests == playRequests,
        "Numeric preview requested playback/download streams.");
    Check(CreateViewModel(preview).CurrentPageText == "当前解析 P2/2", "Preview displayed the wrong current part.");
    var messageApi = DispatchProxy.Create<IMessageContext, RecordingMessageContext>();
    var subscriptions = (RecordingMessageContext)messageApi;
    var bot = DispatchProxy.Create<IBotContext, RecordingBotContext>();
    ((RecordingBotContext)bot).Messages = messageApi;
    var previews = new List<string>();
    var subscribedPresenter = (IDisposable)Activator.CreateInstance(handlerType,
        [new ProviderMessageHandlerContext(bot, new PluginConfig(), provider, fakeServices, new PreviewCardRenderer(),
            (reply, url) => { previews.Add(url); return Task.CompletedTask; })])!;
    await (Task)handlerType.GetMethod("PresentAsync", BindingFlags.NonPublic|BindingFlags.Instance)!
        .Invoke(subscribedPresenter, [source, defaultPart with { CoverUrl="https://example.com/cover.jpg" }, false, CancellationToken.None])!;
    Check(subscriptions.Subscription!.Reference == new MessageReference("test-bot", source.Channel, "card-1")
        && subscriptions.Duration == TimeSpan.FromMinutes(10) && !subscriptions.DisposeOnReply,
        "Card did not register a reusable, scoped Bot API reply subscription.");
    foreach (var text in new[] { "1", "p1", "P1", "  p2  " })
        await subscriptions.Handler!(numeric with { Segments=[new QuoteSegment("card-1"),new TextSegment(text)] });
    Check(previews.Count == 4 && previews[0].EndsWith("?p=1") && previews[3].EndsWith("?p=2") && previews.All(url => !url.Contains("#myparser-preview")),
        "Number / p / P reply did not queue the expected preview.");
    var subscriptionSelectedPart = await provider.ParseAsync(previews[3]);
    Check(subscriptionSelectedPart.Attributes["preview_only"] == "false" && subscriptionSelectedPart.Assets.Any(asset => asset.Kind == MediaAssetKind.Video),
        "Subscribed multi-P reply still disabled video playback.");
    Check(module.TryBuildParseText(numeric) is null, "Subscribed reply would be dispatched twice.");
    foreach (var text in new[] { "p0", "3", "-1", "+1", "1.0", "p", "hello" })
        await subscriptions.Handler!(numeric with { Segments=[new QuoteSegment("card-1"),new TextSegment(text)] });
    await subscriptions.Handler!(numeric with { InstanceId="other-bot" });
    await subscriptions.Handler!(numeric with { Channel=new Channel("group-b", ChannelType.Group) });
    Check(previews.Count == 4, "Invalid or out-of-scope replies queued previews.");
    subscribedPresenter.Dispose();
    Check(subscriptions.Subscription.Disposed && module.TryBuildParseText(numeric) is null,
        "Plugin handler disposal did not clear reply subscriptions and cache.");
    var textPresenter = (IDisposable)Activator.CreateInstance(handlerType,
        [new ProviderMessageHandlerContext(bot, new PluginConfig { SendCoverImages=false }, provider, fakeServices,
            new PreviewCardRenderer(), (reply, url) => { previews.Add(url); return Task.CompletedTask; })])!;
    recorder.Messages.Clear();
    await (Task)handlerType.GetMethod("PresentAsync", BindingFlags.NonPublic|BindingFlags.Instance)!
        .Invoke(textPresenter, [source, media with { Attributes = new Dictionary<string, string> { ["pages"] = "1" } }, false, CancellationToken.None])!;
    Check(recorder.Messages.SequenceEqual(new[]{"download", "video"}), "Disabled single-video cover must not send title/link text.");
    recorder.Messages.Clear();
    await (Task)handlerType.GetMethod("PresentAsync", BindingFlags.NonPublic|BindingFlags.Instance)!
        .Invoke(textPresenter, [source, media with { Attributes = new Dictionary<string, string> { ["pages"] = "1", ["preview_only"] = "true" } }, false, CancellationToken.None])!;
    Check(recorder.Messages.Count == 0, "Disabled single-video preview must not send title/link text or download video.");
    foreach (var textMedia in new[] { defaultPart, preview })
    {
        recorder.Messages.Clear();
        await (Task)handlerType.GetMethod("PresentAsync", BindingFlags.NonPublic|BindingFlags.Instance)!
            .Invoke(textPresenter, [source, textMedia, false, CancellationToken.None])!;
        Check(recorder.Messages.SequenceEqual(textMedia.Kind == ParsedMediaKind.Video && textMedia.Attributes.GetValueOrDefault("preview_only") != "true"
            ? new[]{"download", "text", "video"} : new[]{"text"}), "Disabled covers lost P1 playback or duplicated navigation text.");
        Check(subscriptions.Subscription!.Reference.MessageId == "text-1", "Text reply did not register page selection.");
        var before = previews.Count;
        await subscriptions.Handler!(numeric with { Segments=[new QuoteSegment("text-1"),new TextSegment("p1")] });
        Check(previews.Count == before + 1, "Text overview/preview did not support reply page selection.");
    }
    textPresenter.Dispose();
    var bangumiProvider = new BilibiliBangumiParseProvider(new BilibiliBangumiParser(http, new PluginConfig()), new BilibiliParser(new PluginConfig(), http));
    var bangumiRegistry = new ParseProviderRegistry([bangumiProvider]);
    foreach (var id in new[]{ "ss5776", "SS5776", "ep100138", "md5776" })
    {
        var normalized = module.NormalizeParseText(source with { Segments=[new TextSegment(id)] });
        Check(normalized is not null && bangumiRegistry.FindProvider(normalized, true, out _) == bangumiProvider,
            "Incoming short bangumi ID did not reach auto parsing: " + id);
        Check(bangumiRegistry.FindProvider(id, true, out _) == bangumiProvider,
            "Auto registry rejected a short bangumi ID: " + id);
    }
    Check(module.NormalizeParseText(source with { Segments=[new TextSegment("5776")] }) is null,
        "Bare numbers must not trigger bangumi auto parsing.");
    var classificationRegistry = new ParseProviderRegistry([bangumiProvider, provider]);
    foreach (var bvid in new[]{ "BV1md2ep3ss4", "BV1SS2EP3MD4", "BV1ep1234567", "BV1ss1234567", "BV1md1234567" })
    {
        foreach (var input in new[]{ bvid, $"https://www.bilibili.com/video/{bvid}/?ep=100138&ss=5776&md=5776" })
        {
            Check(!bangumiProvider.CanHandle(input), "BV fragments were misclassified as bangumi: " + input);
            Check(classificationRegistry.FindProvider(input, true, out _) == provider,
                "Mixed-ID BV input did not select the video provider: " + input);
            var incoming = module.NormalizeParseText(source with { Segments=[new TextSegment(input)] });
            Check(incoming is not null && classificationRegistry.FindProvider(incoming, true, out _) == provider,
                "Incoming BV was hijacked by a bangumi ID fragment: " + input);
        }
    }
    foreach (var text in new[]{ "abc_ss5776_xyz", "prefixep100138", "randommd5776text", "数字 ss5776 混入文本" })
        Check(bangumiRegistry.FindProvider(text, true, out _) is null,
            "Bangumi IDs must match a whole standalone input: " + text);
    var season = await bangumiProvider.ParseAsync("https://www.bilibili.com/bangumi/play/ss5776");
    var episodePreview = await bangumiProvider.ParseAsync("https://www.bilibili.com/bangumi/play/ep100139#myparser-preview");
    var directEpisode = await bangumiProvider.ParseAsync("https://www.bilibili.com/bangumi/play/ep100138");
    var unrelatedFragmentEpisode = await bangumiProvider.ParseAsync("https://www.bilibili.com/bangumi/play/ep100138#myparser-preview-extra");
    Check(unrelatedFragmentEpisode.Attributes["preview_only"] == "false" && unrelatedFragmentEpisode.Assets.Any(asset => asset.Kind == MediaAssetKind.Video),
        "A partially matching fragment unexpectedly disabled episode video delivery.");
    Check(directEpisode.Kind == ParsedMediaKind.Video && directEpisode.Attributes["preview_only"] == "false"
        && directEpisode.Assets.Any(asset => asset.Kind == MediaAssetKind.Video), "Direct ep link was downgraded to a preview.");
    Check(season.Kind == ParsedMediaKind.Video && season.Attributes["pages"] == "2"
        && season.Attributes["requested_page"] == "1" && season.Attributes["preview_only"] == "false"
        && season.SourceUrl == "https://www.bilibili.com/bangumi/play/ep100138"
        && season.Assets.Any(asset => asset.Kind == MediaAssetKind.Video), "Season link did not default to first episode playback.");
    var shortSeason = await bangumiProvider.ParseAsync("ss5776");
    Check(shortSeason.Kind == ParsedMediaKind.Video && shortSeason.SourceUrl == season.SourceUrl
        && shortSeason.Attributes["requested_page"] == "1", "Short ss ID did not play the same first episode.");
    Check(episodePreview.Attributes["preview_only"] == "true" && episodePreview.Assets.Count == 0
        && episodePreview.Attributes["requested_page"] == "2", "Episode preview requested playback or lost episode index.");
    var rendererType = typeof(BiliCard).Assembly.GetType("MyParser.Provider.BiliBili.BilibiliMediaCardRenderer", true)!;
    var seasonModel = (BiliCardViewModel)rendererType.GetMethod("CreateBangumiViewModel", BindingFlags.Static|BindingFlags.NonPublic)!
        .Invoke(null, [episodePreview, null])!;
    Check(seasonModel.CurrentPageText == "当前解析 第2/2集" && seasonModel.Avatar is null, "Episode card model did not match selected episode.");
    var episodeUrls = new List<string>();
    var bangumiConfig = new PluginConfig();
    var bangumiPresenter = (IDisposable)Activator.CreateInstance(handlerType,
        [new ProviderMessageHandlerContext(bot,bangumiConfig,bangumiProvider,fakeServices,new PreviewCardRenderer(),
            (reply,url) => { episodeUrls.Add(url); return Task.CompletedTask; })])!;
    foreach (var item in new[] { season, episodePreview })
    {
        recorder.Messages.Clear();
        await (Task)handlerType.GetMethod("PresentAsync", BindingFlags.NonPublic|BindingFlags.Instance)!
            .Invoke(bangumiPresenter,[source,item with { CoverUrl="https://example.com/cover.jpg" },false,CancellationToken.None])!;
        var expectedMessages = item.Attributes.GetValueOrDefault("preview_only") == "true"
            ? new[]{"image"} : new[]{"download", "image", "video"};
        Check(recorder.Messages.SequenceEqual(expectedMessages), "Bangumi did not deliver the first episode or sent per-episode link messages.");
        await subscriptions.Handler!(numeric with { Segments=[new QuoteSegment("card-1"),new TextSegment("2")] });
    }
    Check(episodeUrls.SequenceEqual(Enumerable.Repeat("https://www.bilibili.com/bangumi/play/ep100139",2)),
        "Bangumi overview/preview replies did not resolve episode URLs.");
    var replyEpisode = await bangumiProvider.ParseAsync(episodeUrls[0]);
    Check(replyEpisode.Attributes["preview_only"] == "false" && replyEpisode.Assets.Any(asset => asset.Kind == MediaAssetKind.Video),
        "Replying to a bangumi card still produced a cover-only preview.");
    recorder.Messages.Clear();
    await (Task)handlerType.GetMethod("PresentAsync", BindingFlags.NonPublic|BindingFlags.Instance)!
        .Invoke(bangumiPresenter,[source,replyEpisode,false,CancellationToken.None])!;
    Check(recorder.Messages.SequenceEqual(new[]{"download", "image", "video"}), "Bangumi selection reply did not download and send the chosen episode.");
    recorder.Messages.Clear();
    await (Task)handlerType.GetMethod("PresentAsync", BindingFlags.NonPublic|BindingFlags.Instance)!
        .Invoke(bangumiPresenter,[source,directEpisode,false,CancellationToken.None])!;
    Check(recorder.Messages.SequenceEqual(new[]{"download", "image", "video"}), "Direct ep did not download and deliver video after its cover.");
    bangumiConfig.SendCoverImages = false;
    recorder.Messages.Clear();
    await (Task)handlerType.GetMethod("PresentAsync", BindingFlags.NonPublic|BindingFlags.Instance)!
        .Invoke(bangumiPresenter,[source,episodePreview,false,CancellationToken.None])!;
    Check(recorder.Messages.SequenceEqual(new[]{"text"}), "Disabled bangumi cover did not send one text preview.");
    await subscriptions.Handler!(numeric with { Segments=[new QuoteSegment("text-1"),new TextSegment("1")] });
    Check(episodeUrls[^1] == "https://www.bilibili.com/bangumi/play/ep100138", "Text episode preview lost reply subscription.");
    var cacheType = typeof(BiliCard).Assembly.GetType("MyParser.Provider.BiliBili.Parsing.BilibiliMultiPageReplyCache", true)!;
    cacheType.GetMethod("RegisterSelections")!.Invoke(null,
        [source, "native-bangumi-card", new[]{"https://www.bilibili.com/bangumi/play/ep100138", "https://www.bilibili.com/bangumi/play/ep100139"}, null, null]);
    Check(module.TryBuildParseText(numeric with { Segments=[new QuoteSegment("native-bangumi-card"),new TextSegment("2")] })
        == "https://www.bilibili.com/bangumi/play/ep100139", "Native bangumi reply fallback still added the preview-only marker.");
    bangumiPresenter.Dispose();
    handler.MultiPage = false;

    handler.FailSupplementary = true;
    media = await provider.ParseAsync("https://www.bilibili.com/video/BV1rxp86jEYH/?p=1");
    model = CreateViewModel(media);
    Check(media.Assets.Count == 2 && model.ViewCount == "14.8万", "Metadata failure broke video parsing.");
    Check(!model.HasTags && !model.HasAuthorMeta, "Unavailable metadata was filled with fabricated tags/location.");
    Check(model.VideoMeta == "2026-10-10 发布 · BV1rxp86jEYH", "Missing category left an empty separator.");

    handler.FailSupplementary = false;
    handler.CancelSupplementary = true;
    using var cancellation = new CancellationTokenSource();
    cancellation.CancelAfter(TimeSpan.FromMilliseconds(100));
    try
    {
        await provider.ParseAsync("https://www.bilibili.com/video/BV1rxp86jEYH/?p=1", cancellation.Token);
        throw new InvalidOperationException("Supplementary cancellation was swallowed.");
    }
    catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
    Console.WriteLine("Card data propagation, supplementary failure and cancellation checks passed.");

    if (args.Contains("--live") || args.Contains("--live-multi"))
    {
        using var liveHandler = new MetadataHandler { UseLiveMetadata = true };
        using var liveHttp = new HttpClient(liveHandler);
        using var liveProvider = new BilibiliParseProvider(new BilibiliParser(new PluginConfig(), liveHttp));
        var liveMedia = await liveProvider.ParseAsync(args.Contains("--live-multi")
            ? "https://www.bilibili.com/video/BV1Pg1SBfE7w/"
            : "https://www.bilibili.com/video/BV1rxp86jEYH/?p=1");
        if (args.Contains("--live-multi"))
            Check(liveMedia.Kind == ParsedMediaKind.Video && liveMedia.Attributes["requested_page"] == "1" && liveHandler.PlayRequests == 1,
                "Live default multi-P link did not request P1 playback streams.");
        var liveModel = CreateViewModel(liveMedia);
        Check(liveMedia.Tags.Count > 0, "Live tag endpoint returned no tags.");
        Check(!string.IsNullOrWhiteSpace(liveMedia.Attributes["category"]), "Live APP endpoint returned no category.");
        if (!args.Contains("--live-multi")) Check(liveModel.HasAuthorMeta, "Live APP endpoint returned no publication location.");
        File.WriteAllText(Path.Combine(Path.GetTempPath(), "myparser-bili-live-card.json"), JsonSerializer.Serialize(liveModel));
        File.WriteAllText(Path.Combine(Path.GetTempPath(), "myparser-bili-live-media.json"), JsonSerializer.Serialize(liveMedia));
        Console.WriteLine($"Live API → parser → provider → card: {liveModel.Title}; {liveModel.VideoMeta}; {liveModel.AuthorMeta}; {string.Join(' ', liveModel.Tags)}; 播放 {liveModel.ViewCount}, 点赞 {liveModel.LikeCount}, 投币 {liveModel.CoinCount}, 收藏 {liveModel.CollectCount}.");
    }
}
finally { cookieProperty.SetValue(null, previousCookie); }

static BiliCardViewModel CreateViewModel(ParsedMedia media)
{
    var renderer = typeof(BiliCardViewModel).Assembly.GetType("MyParser.Provider.BiliBili.BilibiliMediaCardRenderer", true)!;
    return (BiliCardViewModel)renderer.GetMethod("CreateVideoViewModel", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [media, null, null])!;
}

static void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

sealed class MetadataHandler : HttpMessageHandler
{
    private readonly HttpClient _live = new() { Timeout = TimeSpan.FromSeconds(20) };
    public bool UseLiveMetadata { get; init; }
    public bool FailSupplementary { get; set; }
    public bool CancelSupplementary { get; set; }
    public bool MultiPage { get; set; }
    public bool SilentVideo { get; set; }
    public bool MissingVideo { get; set; }
    public string? LastPlayCid;
    public int PlayRequests;
    public int AppRequests;
    public int TagRequests;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = request.RequestUri!.AbsolutePath;
        if (path == "/x/player/wbi/playurl")
        {
            Interlocked.Increment(ref PlayRequests);
            LastPlayCid = request.RequestUri.Query.TrimStart('?').Split('&')
                .Select(part => part.Split('=', 2)).FirstOrDefault(pair => pair[0] == "cid")?[1];
        }
        var supplementary = path is "/x/v2/view" or "/x/web-interface/view/detail/tag";
        if (path == "/x/v2/view") Interlocked.Increment(ref AppRequests);
        if (path == "/x/web-interface/view/detail/tag") Interlocked.Increment(ref TagRequests);
        if (supplementary && CancelSupplementary) await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        if (supplementary && FailSupplementary) return Json("{\"code\":-352,\"message\":\"test unavailable\"}");
        if (UseLiveMetadata && (supplementary || path == "/x/web-interface/view"))
        {
            using var actual = new HttpRequestMessage(HttpMethod.Get, request.RequestUri);
            foreach (var header in request.Headers.Where(h => h.Key != "Cookie")) actual.Headers.TryAddWithoutValidation(header.Key, header.Value);
            return await _live.SendAsync(actual, cancellationToken);
        }
        var content = path switch
        {
            "/pgc/player/web/v2/playurl" => """{"code":0,"result":{"video_info":{"dash":{"video":[{"id":80,"baseUrl":"https://example.com/video.m4s"}],"audio":[{"id":30280,"baseUrl":"https://example.com/audio.m4s"}]}}}}""",
            "/pgc/view/web/season" => """{"code":0,"result":{"season_id":5776,"media_id":5776,"title":"URARA迷路帖","cover":"https://example.com/cover.jpg","evaluate":"少女们追寻梦想。","styles":["日常"],"episodes":[{"id":100138,"cid":1,"title":"1","long_title":"第一集","duration":1440000},{"id":100139,"cid":2,"title":"2","long_title":"第二集","duration":1440000}]}}""",
            "/x/web-interface/view" => """
                {"code":0,"data":{"aid":117412796039538,"bvid":"BV1rxp86jEYH","title":"测试视频","pubdate":1791583200,"tname":"","tname_v2":"","desc":"真实简介","owner":{"mid":123,"name":"测试UP","face":"https://example.com/avatar.jpg"},"stat":{"view":148000,"like":26000,"coin":7305,"favorite":9142},"pages":[{"cid":42606461174,"part":"测试视频","duration":170}]}}
                """,
            "/x/v2/view" => """{"code":0,"data":{"tname":"搞笑","pub_location":"云南"}}""",
            "/x/web-interface/view/detail/tag" => """{"code":0,"data":[{"tag_name":"搞笑"},{"tag_name":"原创"},{"tag_name":"搞笑"}]}""",
            "/x/web-interface/nav" => """{"code":0,"data":{"wbi_img":{"img_url":"https://i0.hdslb.com/bfs/wbi/7cd084941338484aae1ad9425b84077c.png","sub_url":"https://i0.hdslb.com/bfs/wbi/4932caff0ff746eab6f01bf08b70ac45.png"}}}""",
            "/x/player/wbi/playurl" => """{"code":0,"data":{"dash":{"video":[{"id":80,"baseUrl":"https://example.com/video.m4s"}],"audio":[{"id":30280,"baseUrl":"https://example.com/audio.m4s"}]}}}""",
            _ => throw new InvalidOperationException("Unexpected endpoint " + path),
        };
        if (path == "/x/web-interface/view" && MultiPage)
        {
            var json = System.Text.Json.Nodes.JsonNode.Parse(content)!;
            var pages = json["data"]!["pages"]!.AsArray();
            pages.Add(System.Text.Json.Nodes.JsonNode.Parse("""{"cid":42606461175,"part":"第二 P","duration":30}"""));
            content = json.ToJsonString();
        }
        if (path == "/x/player/wbi/playurl" && SilentVideo)
            content = """{"code":0,"data":{"dash":{"video":[{"id":80,"baseUrl":"https://example.com/video.m4s"}],"audio":null}}}""";
        if (path == "/x/player/wbi/playurl" && MissingVideo)
            content = """{"code":0,"data":{"format":"mp4","dash":{"video":[],"audio":[]}}}""";
        return Json(content);
    }

    private static HttpResponseMessage Json(string content) => new(HttpStatusCode.OK) { Content = new StringContent(content, Encoding.UTF8, "application/json") };
    protected override void Dispose(bool disposing) { if (disposing) _live.Dispose(); base.Dispose(disposing); }
}

public class RecordingHostServices : DispatchProxy
{
    public List<string> Messages { get; } = [];
    public Func<CancellationToken, Task<(string, string)>>? Download { get; set; }
    public bool FailImage { get; set; }
    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        if (method!.Name == "SendImageAsync") { if (FailImage) throw new IOException("image-send-failed"); Messages.Add("image"); return Task.FromResult(new SentMessage("card-1")); }
        if (method.Name is "DownloadMuxedProviderVideoAsync" or "DownloadProviderAudioAsync") { Messages.Add("download"); return Download?.Invoke((CancellationToken)args![2]!) ?? Task.FromResult(("file:///verification.mp4", "/verification.mp4")); }
        if (method.Name == "BuildRecordUriAsync") return Task.FromResult("file:///verification.audio");
        if (method.Name == "SendSegmentsAsync" && ((IReadOnlyList<MessageSegment>)args![1]!).Any(segment => segment is AudioSegment)) { Messages.Add("audio"); return Task.FromResult(new SentMessage("audio-1")); }
        if (method.Name == "BuildLocalVideoSegmentAsync") return Task.FromResult(new ProviderLocalVideoSegmentResult(new VideoSegment("file:///verification.mp4"), "file", "file:///verification.mp4", 1, false));
        if (method.Name == "SendSegmentsAsync" && ((IReadOnlyList<MessageSegment>)args![1]!).Any(segment => segment is VideoSegment)) { Messages.Add("video"); return Task.FromResult(new SentMessage("video-1")); }
        if (method.Name is "ReplyTextAsync" or "SendSegmentsAsync") { Messages.Add("text"); return Task.FromResult(new SentMessage("text-1")); }
        if (method.ReturnType == typeof(Task)) return Task.CompletedTask;
        throw new InvalidOperationException("Unexpected host operation " + method.Name);
    }
}
sealed class PreviewCardRenderer : IProviderMediaCardRenderer
{
    public Task<string?> RenderAsync(ParsedMedia media, ProviderCardPurpose purpose, CancellationToken cancellationToken=default)
        => Task.FromResult<string?>("base64://preview");
}

public class RecordingBotContext : DispatchProxy
{
    public IMessageContext Messages { get; set; } = null!;
    protected override object? Invoke(MethodInfo? method, object?[]? args) => method!.Name == "get_Message"
        ? Messages : throw new InvalidOperationException("Unexpected bot operation " + method.Name);
}
public class RecordingMessageContext : DispatchProxy
{
    public ReplyMessageHandler? Handler { get; private set; }
    public TestReplySubscription? Subscription { get; private set; }
    public TimeSpan Duration { get; private set; }
    public bool DisposeOnReply { get; private set; }
    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        if (method!.Name != "SubscribeReply") throw new InvalidOperationException("Unexpected message operation " + method.Name);
        Duration = (TimeSpan)args![1]!;
        Handler = (ReplyMessageHandler)args[2]!;
        DisposeOnReply = (bool)args[3]!;
        return Subscription = new TestReplySubscription((MessageReference)args[0]!, DateTimeOffset.UtcNow.Add(Duration));
    }
}
public sealed class TestReplySubscription(MessageReference reference, DateTimeOffset expiresAt) : IReplySubscription
{
    public MessageReference Reference { get; } = reference;
    public DateTimeOffset? ExpiresAt { get; } = expiresAt;
    public bool Disposed { get; private set; }
    public void Dispose() => Disposed = true;
}
