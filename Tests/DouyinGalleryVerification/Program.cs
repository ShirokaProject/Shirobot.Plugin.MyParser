using System.Reflection;
using System.Text.Json;
using MyParser.Provider.Douyin.Parsing;
using Shirobot.Plugin.MyParser;
using Shirobot.Plugin.MyParser.Parsing;

if (args is ["--download-cookie-file" or "--download-fallback-cookie-file", var downloadCookiePath, var downloadLink])
{
    var assembly = typeof(PluginConfig).Assembly;
    var runtime = assembly.GetType("Shirobot.Plugin.MyParser.MyParserRuntime", true)!;
    var cookie = File.ReadAllText(downloadCookiePath).Trim();
    runtime.GetProperty("DouyinCookie")!.SetValue(null, cookie);
    runtime.GetProperty("TemporaryDirectoryFactory")!.SetValue(null,
        (Func<ShiroBot.SDK.Plugin.TemporaryDirectory>)(() => {
            var path = Path.Combine(Path.GetTempPath(), "myparser-download-check", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return new ShiroBot.SDK.Plugin.TemporaryDirectory(path, DateTimeOffset.UtcNow.AddHours(1));
        }));
    var config = new PluginConfig { DouyinFetchComments = false, LogDownloadProgress = false };
    using var http = new HttpClient(new NetworkProbe { InnerHandler = SafeHttpTransport.CreateHandler() }) { Timeout = TimeSpan.FromSeconds(20) };
    using var parser = new DouyinParser(config, http);
    using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
    var result = await parser.ParseAsync(downloadLink, timeout.Token);
    Console.WriteLine($"parsed={result.AwemeId}, candidates={result.Qualities.Count}");
    var type = typeof(DouyinParser).Assembly.GetType("Shirobot.Plugin.MyParser.Downloading.ProviderDownloadService", true)!;
    var service = Activator.CreateInstance(type)!;
    foreach (var quality in result.Qualities)
    {
        var uri = new Uri(quality.Url);
        Console.WriteLine($"candidate={uri.Scheme}://{uri.Authority}{uri.AbsolutePath}");
    }
    var candidates = result.Qualities.Select(candidate => candidate.Url).ToList();
    var forceFallback = args[0] == "--download-fallback-cookie-file";
    const string missingVideo = "https://aweme.snssdk.com/aweme/v1/play/?video_id=myparser-nonexistent-video";
    if (forceFallback) candidates.Insert(0, missingVideo);
    var attemptedFallback = false;
    var request = new ProviderVideoDownloadRequest("douyin", "抖音", result.AwemeId, Guid.NewGuid().ToString("N"),
        candidates, "", "douyin", "mp4", (method, url, range) => {
            if (forceFallback && url != missingVideo) attemptedFallback = true;
            var message = new HttpRequestMessage(method, url);
            message.Headers.TryAddWithoutValidation("User-Agent", MyParser.Provider.Douyin.Infrastructure.DouyinConstants.UserAgent);
            message.Headers.TryAddWithoutValidation("Accept", "application/json, text/plain, */*");
            message.Headers.TryAddWithoutValidation("Referer", result.SourceUrl);
            message.Headers.TryAddWithoutValidation("Cookie", cookie);
            if (range is not null) message.Headers.TryAddWithoutValidation("Range", range);
            return message;
        });
    var task = (Task<(string FileUri, string LocalPath)>)type.GetMethod("DownloadProviderVideoAsync")!.Invoke(service, [config, request, timeout.Token])!;
    var downloaded = await task;
    Console.WriteLine($"download_complete={new FileInfo(downloaded.LocalPath).Length}, path={downloaded.LocalPath}");
    if (forceFallback && !attemptedFallback) throw new Exception("The missing video did not exercise candidate failover.");
    if (forceFallback) Console.WriteLine("Unavailable primary route recovered through a complete alternate download.");
    return;
}

if (args is ["--live-network-cookie-file", var networkCookiePath])
{
    typeof(PluginConfig).Assembly.GetType("Shirobot.Plugin.MyParser.MyParserRuntime", true)!
        .GetProperty("DouyinCookie")!.SetValue(null, File.ReadAllText(networkCookiePath).Trim());
    using var http = new HttpClient(new NetworkProbe { InnerHandler = Shirobot.Plugin.MyParser.Parsing.SafeHttpTransport.CreateHandler() })
        { Timeout = TimeSpan.FromSeconds(20) };
    using var parser = new DouyinParser(new PluginConfig { DouyinFetchComments = false }, http);
    foreach (var link in new[] { "https://v.douyin.com/9y08g87Ec_g/", "https://v.douyin.com/LkJBc45Qqeg/" })
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var result = await NetworkRetry.ExecuteAsync(token => parser.ParseAsync(link, token), 2, timeout.Token);
        Console.WriteLine($"parsed={result.AwemeId}, gallery={result.IsGallery}, images={result.Images.Count}, video_qualities={result.Qualities.Count}, music={!string.IsNullOrWhiteSpace(result.MusicUrl)}");
        var mediaUrl = result.IsGallery ? result.Images.FirstOrDefault()?.LivePhotoUrl : result.Qualities.FirstOrDefault()?.Url;
        if (!string.IsNullOrWhiteSpace(mediaUrl))
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, mediaUrl);
            request.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0");
            request.Headers.TryAddWithoutValidation("Referer", "https://www.douyin.com/");
            request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 0);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            response.EnsureSuccessStatusCode();
            Console.WriteLine($"media_probe={(int)response.StatusCode}, content_type={response.Content.Headers.ContentType?.MediaType}");
        }
    }
    return;
}

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

if (args is ["--mix"]) { await AudioMixVerification.RunAsync(); return; }

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

internal sealed class NetworkProbe : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var uri = request.RequestUri!;
        try
        {
            var response = await base.SendAsync(request, cancellationToken);
            Console.WriteLine($"HTTP {(int)response.StatusCode}: {uri.Host}{uri.AbsolutePath}");
            return response;
        }
        catch (HttpRequestException ex)
        {
            Console.WriteLine($"network_failure: {ex.Message}");
            throw;
        }
    }
}
