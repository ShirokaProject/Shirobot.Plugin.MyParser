using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using MyParser.Provider.Douyin.Infrastructure;
using MyParser.Provider.Douyin.Models;
using MyParser.Provider.Douyin.Parsing;
using MyParser.Provider.Douyin.Services;
using Shirobot.Plugin.MyParser;

internal static class GalleryParsingVerification
{
    internal static async Task RunAsync()
    {
        var assembly = typeof(DouyinParser).Assembly;
        var signature = assembly.GetType("MyParser.Provider.Douyin.Infrastructure.DouyinWebSignature", true)!
            .GetMethod("Apply", BindingFlags.NonPublic | BindingFlags.Static)!;
        const string unsigned = "https://www.douyin.com/aweme/v1/web/aweme/detail/?aweme_id=123&name=a+b&payload=%2B%2F%3D&a_bogus=abc%2B%2F%3D";
        var session = new DouyinGuestSession("UIFID=test-visitor");
        string Sign(string url, DouyinGuestSession cookies) => (string)signature.Invoke(null, [url, cookies, (long?)1700000000])!;
        var signed = Sign(unsigned, session);
        if (!signed.EndsWith("x-secsdk-web-signature=ade95c81c8468ebd728707023f63c456")) throw new Exception("Web signature/encoding changed.");
        if (Sign(signed, session) != signed) throw new Exception("Signing twice duplicated timestamp/signature.");
        if (Sign(unsigned, new DouyinGuestSession(null)) != unsigned) throw new Exception("Signed without a visitor identity.");
        var profileUrl = unsigned.Replace("/aweme/detail/", "/user/profile/other/");
        if (Sign(profileUrl, session) != profileUrl) throw new Exception("Changed an unprotected endpoint.");
        if (Sign(unsigned, new DouyinGuestSession("UIFID_TEMP=test-visitor")) != signed) throw new Exception("Temporary visitor Cookie was ignored.");

        using (var clip = JsonDocument.Parse("""{"aweme_id":"123","images":[{"url_list":["https://example.com/picture.jpg"],"clip_type":1,"video":{"play_addr_h264":{"uri":"clip-id","url_list":["https://cdn.example.com/blocked.mp4","https://www.douyin.com/aweme/v1/play/?file_id=h264-file"]}}}]}"""))
        {
            var parsed = new MyParser.Provider.Douyin.WorkParsers.DouyinGalleryWorkParser().Parse(clip.RootElement, "123", "https://www.douyin.com/note/123");
            if (parsed.Images[0].ClipType != 1) throw new Exception("Dynamic clip type was discarded.");
            if (parsed.Images[0].LivePhotoUrl != "https://aweme.snssdk.com/aweme/v1/play/?video_id=clip-id&file_id=h264-file")
                throw new Exception("Live Photo selected the blocked CDN or lost its H.264 file id.");
        }

        var runtimeCookie = typeof(PluginConfig).Assembly.GetType("Shirobot.Plugin.MyParser.MyParserRuntime", true)!.GetProperty("DouyinCookie")!;
        var previous = runtimeCookie.GetValue(null);
        runtimeCookie.SetValue(null, "UIFID=test-visitor; ttwid=test");
        try
        {
            using var handler = new GalleryDetailHandler();
            using var http = new HttpClient(handler);
            using var parser = new DouyinParser(new PluginConfig { DouyinFetchComments = false }, http);
            var result = await parser.ParseAsync("https://www.douyin.com/note/1234567890123456789");
            if (result.Images.Count != 1 || result.Images[0].LivePhotoUrl != "https://example.com/live.mp4")
                throw new Exception("Static share-page preview prevented dynamic detail enrichment.");
            if (handler.DetailRequests != 1) throw new Exception("Gallery detail was not enriched exactly once.");
            using var resetHandler = new GalleryDetailHandler { ResetFirstShare = true, ResetFirstDetail = true };
            using var resetHttp = new HttpClient(resetHandler);
            using var resetParser = new DouyinParser(new PluginConfig { DouyinFetchComments = false }, resetHttp);
            var recovered = await resetParser.ParseAsync("https://www.douyin.com/note/1234567890123456789");
            if (recovered.Images[0].LivePhotoUrl != "https://example.com/live.mp4" || resetHandler.DetailRequests != 2)
                throw new Exception("Network resets prevented alternate share/detail endpoints from recovering.");

            var simplified = result with { Images = [new DouyinImageInfo { Url = "https://other-cdn.example.com/picture~resize.jpeg" }] };
            var merge = typeof(DouyinParseService).GetMethod("MergeGalleryMediaDetail", BindingFlags.Static | BindingFlags.NonPublic)!;
            var merged = (DouyinParseResult)merge.Invoke(null, [result, simplified, "test"] )!;
            if (merged.Images[0].LivePhotoUrl != result.Images[0].LivePhotoUrl) throw new Exception("Simplified metadata erased a known dynamic resource.");
        }
        finally { runtimeCookie.SetValue(null, previous); }
        Console.WriteLine("Web signature, cookie aliases, dynamic detail enrichment and resource preservation verification passed.");
    }

    private sealed class GalleryDetailHandler : HttpMessageHandler
    {
        public int DetailRequests { get; private set; }
        public bool ResetFirstShare { get; set; }
        public bool ResetFirstDetail { get; set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            string content;
            if (path.Contains("/share/") && ResetFirstShare)
            {
                ResetFirstShare = false;
                return Task.FromException<HttpResponseMessage>(new HttpRequestException("Connection reset by peer", new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.ConnectionReset)));
            }
            if (path.Contains("/share/"))
                content = "<script>window._ROUTER_DATA=" + Aweme(false) + ";</script>";
            else if (path == "/aweme/v1/web/aweme/detail/")
            {
                DetailRequests++;
                if (ResetFirstDetail)
                {
                    ResetFirstDetail = false;
                    return Task.FromException<HttpResponseMessage>(new HttpRequestException("Connection reset by peer", new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.ConnectionReset)));
                }
                if (!request.RequestUri.Query.Contains("x-secsdk-web-signature=")) throw new Exception("Missing final signature on detail request.");
                content = "{\"aweme_detail\":" + Aweme(true) + "}";
            }
            else if (path == "/webid") content = "{\"web_id\":\"1234567890123456789\"}";
            else if (path.Contains("/web/common")) content = "{}";
            else throw new Exception("Unexpected parsing request: " + path);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(content, Encoding.UTF8) });
        }

        private static string Aweme(bool dynamic) => JsonSerializer.Serialize(new
        {
            aweme_id = "1234567890123456789",
            images = new[] { new { url_list = new[] { "https://example.com/picture~resize.jpeg" }, clip_type = dynamic ? 1 : 2,
                video = dynamic ? new { play_addr_h264 = new { url_list = new[] { "https://example.com/live.mp4" } } } : null } },
        });
    }
}
