using System.Reflection;
using System.Text.Json;
using MyParser.Provider.Douyin.Parsing;
using Shirobot.Plugin.MyParser;
using Shirobot.Plugin.MyParser.Parsing;

if (args is ["--live-cookie-file", var cookiePath])
{
    var cookie = File.ReadAllText(cookiePath).Trim();
    if (string.IsNullOrWhiteSpace(cookie)) throw new InvalidOperationException("No configured Douyin Cookie.");
    typeof(PluginConfig).Assembly.GetType("Shirobot.Plugin.MyParser.MyParserRuntime", true)!
        .GetProperty("DouyinCookie")!.SetValue(null, cookie);
    // This diagnostic runner uses the environment's outbound proxy; production keeps SafeHttpTransport.
    using var capture = new DetailCapture { InnerHandler = new HttpClientHandler() };
    using var http = new HttpClient(capture) { Timeout = TimeSpan.FromSeconds(20) };
    using var parser = new DouyinParser(new PluginConfig { DouyinFetchComments = false }, http);
    foreach (var id in new[] { "7695273188389447161", "7694911989970482559", "7695289551716427237" })
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        try
        {
            var result = await parser.ParseAsync($"https://www.douyin.com/note/{id}", timeout.Token);
            Console.WriteLine($"{id}: images={result.Images.Count}, live_photos={result.Images.Count(image => !string.IsNullOrWhiteSpace(image.LivePhotoUrl))}, music={!string.IsNullOrWhiteSpace(result.MusicUrl)}");
        }
        catch (Exception ex) { Console.WriteLine($"{id}: {ex.GetType().Name}, cause={ex.GetBaseException().Message.Split('\n')[0]}"); }
    }
    return;
}

if (args is ["--mux"]) await LoopMuxVerification.RunAsync();
if (args is ["--mux-video", var sourceVideo]) await LoopMuxVerification.RunAsync(sourceVideo);
if (args is ["--mux-media", var videoPath, var audioPath]) await LoopMuxVerification.RunAsync(videoPath, audioPath);

await GalleryVerification.RunAsync();
await GalleryParsingVerification.RunAsync();
await ArticleForwardVerification.RunAsync();
await BilibiliPresentationVerification.RunAsync();

internal sealed class DetailCapture : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var response = await base.SendAsync(request, cancellationToken);
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        var match = System.Text.RegularExpressions.Regex.Match(request.RequestUri!.AbsolutePath, @"\d{15,}");
        if (match.Success && DouyinAwemeExtractor.TryExtractShareAweme(text, match.Value, out var shared))
        {
            using (shared) Capture(shared.RootElement);
        }
        try
        {
            using var doc = JsonDocument.Parse(text);
            Capture(doc.RootElement);
        }
        catch (JsonException) { }
        Console.WriteLine($"HTTP {response.StatusCode}: {request.RequestUri!.AbsolutePath}, bytes={text.Length}");
        return response;
    }

    private static void Capture(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            if (element.TryGetProperty("aweme_id", out var id) && element.TryGetProperty("images", out var images)
                && images.ValueKind == JsonValueKind.Array)
            {
                var safeId = new string(id.ToString().Where(char.IsAsciiDigit).ToArray());
                if (safeId is "7695273188389447161" or "7694911989970482559" or "7695289551716427237")
                {
                    var hasDynamic = images.EnumerateArray().Any(image => image.TryGetProperty("video", out var video) && video.ValueKind == JsonValueKind.Object);
                    File.WriteAllText(Path.Combine(Path.GetTempPath(), $"douyin-gallery-{safeId}{(hasDynamic ? "-dynamic" : "")}.json"), element.GetRawText());
                    Console.WriteLine($"Captured {safeId}: image_count={images.GetArrayLength()}, dynamic={hasDynamic}");
                }
            }
            foreach (var property in element.EnumerateObject()) Capture(property.Value);
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray()) Capture(item);
    }
}
