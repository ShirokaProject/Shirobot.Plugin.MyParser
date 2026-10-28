using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MyParser.Provider.BiliBili.Models;
using MyParser.Provider.BiliBili.Parsing;
using MyParser.Provider.BiliBili.Services;
using Shirobot.Plugin.MyParser;
using Shirobot.Plugin.MyParser.Parsing;
using ShiroBot.SDK.Plugin;

internal static class BilibiliCdnVerification
{
    private const string VideoUrl = "https://www.bilibili.com/video/BV1rxp86jEYH/?p=1";
    private const string Pcdn = "https://xy115x59x252x57xy.mcdn.bilivideo.cn:8082/upgcxcode/video.m4s?os=mcdn&upsig=original";
    private const string Cdn = "https://upos-sz-mirrorali.bilivideo.com/upgcxcode/video.m4s?os=alibv&upsig=a%2Bb&deadline=2000000000";
    private const string OtherCdn = "https://upos-sz-mirrorcos.bilivideo.com/upgcxcode/video.m4s?os=cosbv&upsig=second";
    private static readonly Func<string, bool> IsPcdn = typeof(BilibiliParser).Assembly
        .GetType("MyParser.Provider.BiliBili.Infrastructure.BilibiliCdnPolicy", true)!
        .GetMethod("IsPcdnUrl", BindingFlags.Static | BindingFlags.NonPublic)!.CreateDelegate<Func<string, bool>>();

    public static async Task RunAsync()
    {
        Check(new PluginConfig().BilibiliBlockPcdn, "PCDN preference is not enabled by default.");
        foreach (var url in new[]
        {
            Pcdn, "//XY1.MCDN.BILIVIDEO.CN:4483/upgcxcode/v.m4s",
            "https://abc.v1d.szbdyd.com/video", "https://upos-sz-302ppio.bilivideo.com/video",
            "https://a.nexusedgeio.com/video", "https://a.ahdohpiechei.com/video",
            "https://a.mountaintoys.cn/video", "https://a.bvmlt.top/video",
            Cdn.Replace("os=alibv", "os=mcdn"), "https://8.8.8.8:8082/v1/resource/video",
            "https://cn-live-p2p-01.bilivideo.com/live.m3u8",
        }) Check(IsPcdn(url), "Missed known PCDN: " + url);
        foreach (var url in new[]
        {
            Cdn, OtherCdn, "https://cn-sccd-cu-01-01.bilivideo.com/upgcxcode/v.m4s",
            "https://example.com/path/mcdn.bilivideo.cn?source=os%3Dmcdn",
            "https://notmcdn.bilivideo.cn/video", "https://mcdn.bilivideo.cn.example.com/video",
            "https://8.8.8.8/upgcxcode/video.m4s", "https://example.com/video?os=upos",
        }) Check(!IsPcdn(url), "Ordinary CDN incorrectly marked PCDN: " + url);

        using var handler = new CdnHandler();
        using var http = new HttpClient(handler);
        var config = new PluginConfig();
        using var provider = new BilibiliParseProvider(new BilibiliParser(config, http));

        handler.Play = _ => Dash(Pcdn, [Cdn, Pcdn, OtherCdn]);
        var media = await provider.ParseAsync(VideoUrl);
        Check(handler.PlayRequests == 1, "Signed regular backups should not need another request.");
        foreach (var kind in new[] { MediaAssetKind.Video, MediaAssetKind.Audio })
        {
            var assets = media.Assets.Where(asset => asset.Kind == kind).ToArray();
            Check(assets.Length == 2 && assets[0].Url == Cdn && assets[0].BackupUrls.SequenceEqual([OtherCdn]),
                "Regular signed backups were modified, lost or mixed with PCDN.");
            Check(assets[1].Url == Pcdn, "Original PCDN was not preserved for last fallback.");
        }
        await VerifyDownloadFallbackAsync(media, allRegularFail: false);
        await VerifyDownloadFallbackAsync(media, allRegularFail: true);

        handler.Reset();
        handler.Play = n => n == 1 ? Dash(Pcdn) : Dash(Cdn, [OtherCdn]);
        media = await provider.ParseAsync(VideoUrl);
        Check(handler.PlayRequests == 2 && handler.RefreshRequests == 1, "Missing regular CDN did not request a refresh exactly once.");
        Check(media.Assets.First(asset => asset.Kind == MediaAssetKind.Video).Url == Cdn
            && media.Assets.First(asset => asset.Kind == MediaAssetKind.Audio).Url == Cdn,
            "Refreshed regular CDN did not replace the PCDN preference.");

        handler.Reset();
        handler.Play = _ => Dash(Pcdn);
        media = await provider.ParseAsync(VideoUrl);
        Check(handler.PlayRequests == 2 && media.Assets.Count == 2 && media.Assets.All(asset => asset.Url == Pcdn),
            "PCDN-only results were rejected or refresh looped.");

        handler.Reset();
        handler.FailRefresh = true;
        media = await provider.ParseAsync(VideoUrl);
        Check(handler.PlayRequests == 2 && media.Assets.All(asset => asset.Url == Pcdn), "Refresh error discarded the original fallback.");
        handler.FailRefresh = false;

        handler.Reset();
        handler.Play = _ => MixedQualityDash();
        media = await provider.ParseAsync(VideoUrl);
        var videos = media.Assets.Where(asset => asset.Kind == MediaAssetKind.Video).ToArray();
        Check(videos.Select(asset => asset.QualityId).SequenceEqual([80, 120]) && videos[0].Url == Cdn,
            "A high-quality PCDN was selected before a lower-quality regular CDN.");

        config.BilibiliBlockPcdn = false;
        handler.Reset();
        handler.Play = _ => Dash(Pcdn, [Cdn, OtherCdn]);
        media = await provider.ParseAsync(VideoUrl);
        Check(handler.PlayRequests == 1 && media.Assets.Count == 2 && media.Assets.All(asset => asset.Url == Pcdn
            && asset.BackupUrls.SequenceEqual([Cdn, OtherCdn])), "Disabled preference changed original addresses or made extra requests.");
        config.BilibiliBlockPcdn = true;
        media = await provider.ParseAsync(VideoUrl);
        Check(media.Assets[0].Url == Cdn, "Changing the option on the current config instance did not take effect.");

        handler.Reset();
        handler.Play = _ => Dash(Cdn, silent: true);
        media = await provider.ParseAsync(VideoUrl);
        Check(media.Assets.Count == 1 && handler.PlayRequests == 1, "Silent videos gained an audio requirement.");

        handler.Play = _ => SpecialAudioDash();
        media = await provider.ParseAsync(VideoUrl);
        Check(media.Assets.Count(asset => asset.Kind == MediaAssetKind.Audio && !IsPcdn(asset.Url)) == 2,
            "FLAC or Dolby regular backup was not promoted.");
        Check(media.Assets.Where(asset => asset.Kind == MediaAssetKind.Audio).Last().Url == Pcdn,
            "Special audio lost its PCDN fallback.");

        handler.Reset();
        handler.Play = n => n == 1 ? Dash(Pcdn) : Dash(Cdn);
        var bangumi = new BilibiliBangumiParser(http, config);
        var episode = (BilibiliBangumiEpisodeVideoParseResult)await bangumi.ParseAsync("https://www.bilibili.com/bangumi/play/ep100138");
        Check(handler.PlayRequests == 2 && handler.RefreshRequests == 1 && episode.Video.SelectedVideo!.Url == Cdn,
            "Bangumi did not refresh and prefer regular CDN.");
        handler.Reset();
        handler.Play = _ => Dash(Pcdn);
        episode = (BilibiliBangumiEpisodeVideoParseResult)await bangumi.ParseAsync("https://www.bilibili.com/bangumi/play/ep100138");
        Check(episode.Video.SelectedVideo!.Url == Pcdn, "PCDN-only bangumi lost fallback.");

        handler.Reset();
        handler.CancelRefresh = true;
        using (var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100)))
        {
            try
            {
                await provider.ParseAsync(VideoUrl, cancellation.Token);
                throw new InvalidOperationException("Cancellation during CDN refresh was swallowed.");
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        }
        handler.CancelRefresh = false;

        var live = new BilibiliLiveParser(http, config);
        var room = await live.ParseAsync("https://live.bilibili.com/1");
        Check(room.Streams.Count == 2 && !IsPcdn(room.Streams[0].Url) && IsPcdn(room.Streams[1].Url),
            "Live did not retain PCDN after regular CDN.");
        await VerifyLiveFallbackAsync(config, new BilibiliLiveParseProvider(live), room);
        config.BilibiliBlockPcdn = false;
        room = await live.ParseAsync("https://live.bilibili.com/1");
        Check(IsPcdn(room.Streams[0].Url), "Disabled live preference did not preserve original order.");
        Console.WriteLine("Bilibili CDN: signed backups, refresh, PCDN fallback, quality order, config changes, silent/FLAC/Dolby, bangumi, live and cancellation passed.");
    }

    private static JsonObject Dash(string url, string[]? backups = null, bool silent = false) => new()
    {
        ["dash"] = new JsonObject
        {
            ["video"] = new JsonArray(Stream(80, url, backups)),
            ["audio"] = silent ? null : new JsonArray(Stream(30280, url, backups)),
        },
    };

    private static JsonObject Stream(int id, string url, string[]? backups = null) => new()
    {
        ["id"] = id, ["base_url"] = url, ["codecid"] = id < 1000 ? 7 : 0,
        ["backup_url"] = new JsonArray((backups ?? []).Select(value => (JsonNode?)JsonValue.Create(value)).ToArray()),
    };

    private static JsonObject MixedQualityDash()
    {
        var play = Dash(Cdn);
        play["dash"]!["video"] = new JsonArray(Stream(120, Pcdn), Stream(80, Cdn));
        return play;
    }

    private static JsonObject SpecialAudioDash()
    {
        var play = Dash(Cdn, silent: true);
        play["dash"]!["flac"] = new JsonObject { ["audio"] = Stream(30251, Pcdn, [Cdn]) };
        play["dash"]!["dolby"] = new JsonObject { ["audio"] = new JsonArray(Stream(30250, Pcdn, [OtherCdn])) };
        return play;
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static async Task VerifyDownloadFallbackAsync(ParsedMedia media, bool allRegularFail)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var server = Task.Run(async () =>
        {
            for (var index = 0; index < 2; index++)
            {
                using var client = await listener.AcceptTcpClientAsync(timeout.Token);
                await using var stream = client.GetStream();
                using var reader = new StreamReader(stream, leaveOpen: true);
                while (!string.IsNullOrEmpty(await reader.ReadLineAsync(timeout.Token))) { }
                await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 206 Partial Content\r\nContent-Length: 1\r\nContent-Range: bytes 0-0/100\r\nConnection: close\r\n\r\nx"), timeout.Token);
            }
        }, timeout.Token);
        try
        {
            var config = new PluginConfig
            {
                HttpProxy = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}",
                LogDownloadProgress = false,
            };
            var attempted = new List<string>();
            ProviderMuxedMediaStream Convert(MediaAsset asset) => new(asset.QualityId.ToString(), asset.Url, asset.BackupUrls,
                asset.QualityId, "fixture", asset.Width, asset.Height, asset.FrameRate, asset.Codec, asset.Kind == MediaAssetKind.Audio);
            var request = new ProviderMuxedVideoDownloadRequest("bilibili", "Bilibili", "fixture", "fixture", "fixture", "",
                media.Assets.Where(asset => asset.Kind == MediaAssetKind.Video).Select(Convert).ToArray(),
                media.Assets.Where(asset => asset.Kind == MediaAssetKind.Audio).Select(Convert).ToArray(),
                (method, url, range) =>
                {
                    attempted.Add(url);
                    if (url == Cdn || (allRegularFail && url == OtherCdn)) throw new HttpRequestException("fixture-unavailable");
                    // A local proxy serves a successful range response without contacting a real CDN.
                    var probe = new HttpRequestMessage(method, "http://8.8.8.8/fixture");
                    probe.Headers.TryAddWithoutValidation("Range", range);
                    return probe;
                });
            var serviceType = typeof(BilibiliParser).Assembly.GetType("Shirobot.Plugin.MyParser.Downloading.ProviderDownloadService", true)!;
            var service = Activator.CreateInstance(serviceType)!;
            await (Task)serviceType.GetMethod("SelectMuxedDownloadStreamsAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(service, [config, request, timeout.Token])!;
            await server;
            var expected = allRegularFail ? new[] { Cdn, OtherCdn, Pcdn } : [Cdn, OtherCdn];
            Check(attempted.SequenceEqual(expected.Concat(expected)),
                "The downloader did not exhaust regular backups before PCDN, or used PCDN despite a working regular backup.");
        }
        finally
        {
            timeout.Cancel();
            listener.Stop();
            try { await server; } catch (OperationCanceledException) { } catch (SocketException) { }
        }
    }

    private static async Task VerifyLiveFallbackAsync(PluginConfig config, IParseProvider provider, BilibiliLiveParseResult room)
    {
        var assembly = typeof(BilibiliParser).Assembly;
        var host = DispatchProxy.Create<IProviderHostServices, RecordingHostServices>();
        var presenterType = assembly.GetType("Shirobot.Plugin.MyParser.MessageHandling.UnifiedMediaMessageHandler", true)!;
        var presenter = Activator.CreateInstance(presenterType, [new ProviderMessageHandlerContext(null!, config, provider, host)])!;
        var rank = presenterType.GetMethod("GetLiveStreamRank", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var runtime = typeof(PluginConfig).Assembly.GetType("Shirobot.Plugin.MyParser.MyParserRuntime", true)!;
        var factory = runtime.GetProperty("TemporaryDirectoryFactory")!;
        var previousFactory = factory.GetValue(null);
        var root = Path.Combine(Path.GetTempPath(), "myparser-cdn-live-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            factory.SetValue(null, (Func<TemporaryDirectory>)(() => new TemporaryDirectory(
                Path.Combine(root, Guid.NewGuid().ToString("N")), DateTimeOffset.UtcNow.AddHours(1))));
            var attempted = new List<string>();
            var streams = room.Streams.Select(stream => new ProviderLiveReplayStream(stream.Protocol, stream.Format,
                stream.Codec, stream.CurrentQn, stream.CdnIndex, stream.Url)).ToArray();
            // Give PCDN the better codec rank to verify the downloader still prefers regular CDN.
            streams = [streams[1] with { Format = "fmp4" }, streams[0] with { Format = "ts" }];
            HttpRequestMessage Unavailable(HttpMethod method, string url, string? range)
            {
                attempted.Add(url);
                throw new HttpRequestException("fixture-live-unavailable");
            }
            var request = new ProviderLiveReplayClipDownloadRequest("bilibili-live", "Bilibili", "1", "", streams,
                Unavailable, Unavailable, stream => (int)rank.Invoke(presenter, [stream])!);
            var serviceType = assembly.GetType("Shirobot.Plugin.MyParser.Downloading.ProviderDownloadService", true)!;
            var service = Activator.CreateInstance(serviceType)!;
            try
            {
                await (Task)serviceType.GetMethod("DownloadLiveReplayClipAsync")!.Invoke(service, [config, request, CancellationToken.None])!;
                throw new InvalidOperationException("Unavailable live nodes unexpectedly succeeded.");
            }
            catch (InvalidOperationException ex) when (ex.InnerException is HttpRequestException) { }
            Check(attempted.Count == 2 && !IsPcdn(attempted[0]) && IsPcdn(attempted[1]),
                "Live downloader ignored CDN priority or stopped before trying PCDN fallback.");
        }
        finally
        {
            factory.SetValue(null, previousFactory);
            foreach (var path in Directory.GetDirectories(root)) runtime.GetMethod("ForgetTemporaryDirectory")!.Invoke(null, [path]);
            Directory.Delete(root, recursive: true);
            ((IDisposable)presenter).Dispose();
        }
    }

    private sealed class CdnHandler : HttpMessageHandler
    {
        public Func<int, JsonObject> Play { get; set; } = _ => Dash(Cdn);
        public int PlayRequests { get; private set; }
        public int RefreshRequests { get; private set; }
        public bool FailRefresh { get; set; }
        public bool CancelRefresh { get; set; }
        public void Reset() { PlayRequests = 0; RefreshRequests = 0; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path is "/x/player/wbi/playurl" or "/pgc/player/web/v2/playurl")
            {
                PlayRequests++;
                var query = request.RequestUri.Query.TrimStart('?').Split('&').Select(part => part.Split('=', 2))
                    .ToDictionary(pair => pair[0], pair => pair.Length == 2 ? Uri.UnescapeDataString(pair[1]) : "");
                if (query.TryGetValue("force_host", out var forceHost))
                {
                    RefreshRequests++;
                    Check(forceHost == "2" && query["qn"] == "127", "CDN refresh changed quality or host preference.");
                    if (CancelRefresh) await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                    if (FailRefresh) throw new HttpRequestException("fixture-refresh-failed");
                }
                if (path == "/x/player/wbi/playurl") Check(query.ContainsKey("w_rid"), "Refresh was not WBI signed.");
                var data = Play(PlayRequests);
                return Json(path == "/x/player/wbi/playurl"
                    ? new JsonObject { ["code"] = 0, ["data"] = data }.ToJsonString()
                    : new JsonObject { ["code"] = 0, ["result"] = new JsonObject { ["video_info"] = data } }.ToJsonString());
            }
            return Json(path switch
            {
                "/x/web-interface/view" => """{"code":0,"data":{"aid":1,"bvid":"BV1rxp86jEYH","title":"CDN test","pages":[{"cid":2,"duration":3}]}}""",
                "/x/web-interface/nav" => """{"code":0,"data":{"wbi_img":{"img_url":"https://i0.hdslb.com/bfs/wbi/7cd084941338484aae1ad9425b84077c.png","sub_url":"https://i0.hdslb.com/bfs/wbi/4932caff0ff746eab6f01bf08b70ac45.png"}}}""",
                "/x/v2/view" => """{"code":0,"data":{}}""",
                "/x/web-interface/view/detail/tag" => """{"code":0,"data":[]}""",
                "/pgc/view/web/season" => """{"code":0,"result":{"title":"CDN test","episodes":[{"id":100138,"cid":2,"title":"1"}]}}""",
                "/room/v1/Room/room_init" => """{"code":0,"data":{"room_id":1,"live_status":1}}""",
                "/xlive/web-room/v1/index/getInfoByRoom" => """{"code":0,"data":{}}""",
                "/xlive/web-room/v2/index/getRoomPlayInfo" => """{"code":0,"data":{"playurl_info":{"playurl":{"stream":[{"protocol_name":"http_hls","format":[{"format_name":"fmp4","codec":[{"codec_name":"avc","current_qn":10000,"base_url":"/live.m3u8","url_info":[{"host":"https://cn-live-p2p-01.bilivideo.com"},{"host":"https://cn-live-01.bilivideo.com"}]}]}]}]}}}}""",
                _ => throw new InvalidOperationException("Unexpected endpoint " + path),
            });
        }

        private static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(value, Encoding.UTF8, "application/json"),
        };
    }
}
