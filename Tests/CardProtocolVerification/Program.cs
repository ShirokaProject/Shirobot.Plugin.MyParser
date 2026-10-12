using System.Reflection;
using System.Net;
using System.Net.Sockets;
using ShiroBot.AvaloniaSdk;
using ShiroBot.SDK.Models;
using ShiroBot.SDK.Plugin;
using Shirobot.Plugin.MyParser;
using Shirobot.Plugin.MyParser.Parsing;

var directory = Path.Combine(Path.GetTempPath(), "myparser-card-protocol-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(directory);
var runtime = typeof(PluginConfig).Assembly.GetType("Shirobot.Plugin.MyParser.MyParserRuntime", true)!;
var factory = runtime.GetProperty("TemporaryDirectoryFactory")!;
var previousFactory = factory.GetValue(null);
factory.SetValue(null, (Func<TemporaryDirectory>)(() => new TemporaryDirectory(directory, DateTimeOffset.UtcNow.AddHours(1))));
var assembly = typeof(MyParser.Provider.Douyin.Parsing.DouyinParser).Assembly;
var renderer = DispatchProxy.Create<IAvaloniaRenderContext, CardRender>();
var messages = DispatchProxy.Create<IMessageContext, CardMessages>();
var bot = DispatchProxy.Create<IBotContext, CardBot>();
((CardBot)bot).Renderer = renderer;
((CardBot)bot).Messages = messages;
using var http = new HttpClient();
try
{
    using var imageFixture = new CardImageFixture();
    foreach (var protocol in new[] { 0, 1, 2 })
    {
        var config = new PluginConfig { FileProtocol = protocol };
        using var lifetime = (IDisposable)Activator.CreateInstance(assembly.GetType("Shirobot.Plugin.MyParser.ProviderHostServices", true)!, [bot, config])!;
        var services = (IProviderHostServices)lifetime;
        foreach (var (name, id, kind, purpose) in new[]
        {
            ("MyParser.Provider.BiliBili.BilibiliMediaCardRenderer", "bilibili", ParsedMediaKind.Video, ProviderCardPurpose.Main),
            ("MyParser.Provider.BiliBili.BilibiliMediaCardRenderer", "bilibili-bangumi", ParsedMediaKind.Video, ProviderCardPurpose.Main),
            ("MyParser.Provider.BiliBili.BilibiliMediaCardRenderer", "bilibili", ParsedMediaKind.Live, ProviderCardPurpose.Main),
            ("MyParser.Provider.BiliBili.BilibiliMediaCardRenderer", "bilibili", ParsedMediaKind.Article, ProviderCardPurpose.Article),
            ("MyParser.Provider.Douyin.DouyinMediaCardRenderer", "douyin", ParsedMediaKind.Gallery, ProviderCardPurpose.Main),
            ("MyParser.Provider.Douyin.DouyinMediaCardRenderer", "douyin", ParsedMediaKind.Video, ProviderCardPurpose.Comments),
            ("MyParser.Provider.NetEaseCloudMusic.NetEaseMediaCardRenderer", "neteasecloudmusic", ParsedMediaKind.Track, ProviderCardPurpose.Main),
            ("MyParser.Provider.Heybox.HeyboxMediaCardRenderer", "heybox", ParsedMediaKind.Article, ProviderCardPurpose.Main),
            ("MyParser.Provider.Heybox.HeyboxMediaCardRenderer", "heybox", ParsedMediaKind.Article, ProviderCardPurpose.Article),
            ("MyParser.Provider.X.XMediaCardRenderer", "x", ParsedMediaKind.Article, ProviderCardPurpose.Main),
            ("MyParser.Provider.WeixinChannels.WeixinChannelsMediaCardRenderer", "weixinchannels", ParsedMediaKind.Video, ProviderCardPurpose.Main),
            ("Shirobot.Plugin.MyParser.MessageHandling.ProviderMediaCardRenderer", "other", ParsedMediaKind.Other, ProviderCardPurpose.Main),
        })
        {
            var beforeArrays = ((CardRender)renderer).ArrayRenders;
            var beforeFiles = ((CardRender)renderer).FileRenders;
            var media = new ParsedMedia { ProviderId = id, ProviderName = id, MediaId = "test", Kind = kind, Title = "test",
                Content = [new MediaContentBlock { Kind = MediaContentKind.Text, Text = "test comment", Attributes = new Dictionary<string, string> { ["category"] = "comment" } }] };
            var type = assembly.GetType(name, true)!;
            string? uri;
            if (id == "other")
            {
                var card = Activator.CreateInstance(type, [bot, services])!;
                uri = await (Task<string?>)type.GetMethod("RenderAsync")!.Invoke(card, [media, CancellationToken.None])!;
            }
            else
            {
                var card = (IProviderMediaCardRenderer)Activator.CreateInstance(type, [new ProviderCardRenderContext(bot, config, services)])!;
                uri = await card.RenderAsync(media, purpose);
            }
            if (string.IsNullOrWhiteSpace(uri)) throw new Exception($"Card unexpectedly failed: {id}/{kind}/{purpose}");
            byte[] actual;
            if (protocol == 0)
            {
                if (!uri.StartsWith("base64://")) throw new Exception("Base64 selection ignored.");
                actual = Convert.FromBase64String(uri[9..]);
            }
            else if (protocol == 1)
            {
                var fileUri = new Uri(uri);
                if (!fileUri.IsFile) throw new Exception("File selection ignored: " + id);
                actual = await File.ReadAllBytesAsync(fileUri.LocalPath);
                if (!(bool)runtime.GetMethod("IsTemporaryFile")!.Invoke(null, [fileUri.LocalPath])!) throw new Exception("Card was not saved in host temporary storage.");
            }
            else
            {
                using var response = await http.GetAsync(uri);
                response.EnsureSuccessStatusCode();
                if (response.Content.Headers.ContentType?.MediaType != "image/png") throw new Exception("HTTP card has incorrect MIME type.");
                actual = await response.Content.ReadAsByteArrayAsync();
            }
            if (!actual.SequenceEqual(CardRender.Png)) throw new Exception("Card bytes changed.");
            var renderProbe = (CardRender)renderer;
            if (protocol == 0 && renderProbe.FileRenders != beforeFiles || protocol != 0 && renderProbe.ArrayRenders != beforeArrays)
                throw new Exception("Card used the buffered renderer for File/HTTP transport.");
            if (renderProbe.RenderedFiles.Any(File.Exists)) throw new Exception("Renderer temporary file was not consumed.");
            await services.SendImageAsync(new MessageEvent { Platform = "qq", InstanceId = "test", MessageId = "source", Sender = new User("tester"), Channel = new Channel("test", ChannelType.Group), Segments = [] }, new ImageSegment(uri));
            if (((CardMessages)messages).LastUri != uri) throw new Exception("Sender changed the selected protocol.");
        }
        if (protocol != 0)
        {
            using var lateCancellation = new CancellationTokenSource();
            CardRender.CancelAfterFileRender = lateCancellation;
            try
            {
                await services.RenderCardUriAsync<Avalonia.Controls.Border>(null, "test", "late-cancel", lateCancellation.Token);
                throw new Exception("Post-render cancellation ignored.");
            }
            catch (OperationCanceledException) when (lateCancellation.IsCancellationRequested) { }
            finally { CardRender.CancelAfterFileRender = null; }
            if (((CardRender)renderer).RenderedFiles.Any(File.Exists)) throw new Exception("Canceled file rendering left output.");
        }
        if (protocol == 2)
        {
            // Concurrent cards must share one HTTP listener.
            var concurrent = await Task.WhenAll(Enumerable.Range(0, 8).Select(index => services.BuildRenderedCardUriAsync(CardRender.Png, "test", index.ToString())));
            foreach (var uri in concurrent)
                if (!(await http.GetByteArrayAsync(uri)).SequenceEqual(CardRender.Png)) throw new Exception("Concurrent HTTP card failed.");
        }
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        try { await services.BuildRenderedCardUriAsync(CardRender.Png, "test", "cancel", cancelled.Token); throw new Exception("Cancelled card unexpectedly succeeded."); }
        catch (OperationCanceledException) { }
        Console.WriteLine($"12 card branches and outgoing images verified: protocol={protocol}");
    }
    foreach (var protocol in new[] { 1, 0, 1, 2, 0 })
    {
        using var lifetime = (IDisposable)Activator.CreateInstance(assembly.GetType("Shirobot.Plugin.MyParser.ProviderHostServices", true)!,
            [bot, new PluginConfig { FileProtocol = protocol, HttpProxy = imageFixture.Url }])!;
        var services = (IProviderHostServices)lifetime;
        var image = await services.BuildProviderImageAsync(new ProviderImageBuildRequest("test", imageFixture.Url + "image", null, "picture", PersistLocalFile: false));
        var mode = services.GetUriMode(image.Uri);
        if (mode != (protocol == 0 ? "base64" : protocol == 1 ? "file" : "http")) throw new Exception($"Image cache ignored protocol switch: protocol={protocol}, mode={mode}, requests={imageFixture.Requests}.");
        if (protocol == 1 && (image.LocalPath is null || !File.Exists(image.LocalPath))) throw new Exception("File image was not persisted.");
    }
    if (imageFixture.Requests != 2) throw new Exception("Image cache did not reuse files across protocol changes.");

    using (var lifetime = (IDisposable)Activator.CreateInstance(assembly.GetType("Shirobot.Plugin.MyParser.ProviderHostServices", true)!,
        [bot, new PluginConfig { FileProtocol = 1, HttpProxy = imageFixture.Url, NetworkRetryCount = 2 }])!)
    {
        var image = await ((IProviderHostServices)lifetime).BuildProviderImageAsync(new ProviderImageBuildRequest("test", imageFixture.Url + "retry", null, "retry-image"));
        if (!new Uri(image.Uri).IsFile || imageFixture.RetryRequests != 3) throw new Exception("Remote image did not retry transient HTTP failures.");
    }
    using (var lifetime = (IDisposable)Activator.CreateInstance(assembly.GetType("Shirobot.Plugin.MyParser.ProviderHostServices", true)!, [bot, new PluginConfig { FileProtocol = 1, HttpProxy = imageFixture.Url }])!)
    {
        var services = (IProviderHostServices)lifetime;
        CardRender.FailRender = true;
        var fallback = Activator.CreateInstance(assembly.GetType("Shirobot.Plugin.MyParser.MessageHandling.ProviderMediaCardRenderer", true)!, [bot, services])!;
        var media = new ParsedMedia { ProviderId = "test", ProviderName = "test", MediaId = "fallback", CoverUrl = imageFixture.Url + "image" };
        var uri = await (Task<string?>)fallback.GetType().GetMethod("RenderAsync")!.Invoke(fallback, [media, CancellationToken.None])!;
        if (uri is null || !new Uri(uri).IsFile) throw new Exception("Card fallback ignored File selection.");
        CardRender.FailRender = false;
        factory.SetValue(null, (Func<TemporaryDirectory>)(() => throw new IOException("storage-failed")));
        try { await services.BuildRenderedCardUriAsync(CardRender.Png, "test", "fail"); throw new Exception("Failed File card silently changed protocol."); }
        catch (IOException) { }
    }
    Console.WriteLine("Remote image persistence/cache switches, HTTP concurrency, card fallback and storage failure verification passed.");

}
finally
{
    factory.SetValue(null, previousFactory);
    Directory.Delete(directory, true);
}

public class CardRender : DispatchProxy
{
    public static bool FailRender;
    public static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+j4iUAAAAASUVORK5CYII=");
    public static CancellationTokenSource? CancelAfterFileRender { get; set; }
    public int ArrayRenders { get; private set; }
    public int FileRenders { get; private set; }
    public List<string> RenderedFiles { get; } = [];
    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        if (method!.Name == "RenderControlPngAsync")
        {
            ArrayRenders++;
            return FailRender ? Task.FromException<byte[]>(new IOException("render-failed")) : Task.FromResult(Png);
        }
        if (method.Name == "RenderControlPngToFileUriAsync")
        {
            FileRenders++;
            if (FailRender) return Task.FromException<string>(new IOException("render-failed"));
            var path = Path.Combine(Path.GetTempPath(), "myparser-card-probe-" + Guid.NewGuid().ToString("N") + ".png");
            File.WriteAllBytes(path, Png);
            RenderedFiles.Add(path);
            CancelAfterFileRender?.Cancel();
            return Task.FromResult(new Uri(path).AbsoluteUri);
        }
        throw new NotSupportedException(method.Name);
    }
}
public class CardBot : DispatchProxy
{
    public IRenderContext Renderer { get; set; } = null!;
    public IMessageContext Messages { get; set; } = null!;
    protected override object? Invoke(MethodInfo? method, object?[]? args) => method!.Name switch
    {
        "get_Render" => Renderer, "get_Message" => Messages, "GetAdapterExtension" => null,
        _ => throw new NotSupportedException(method.Name)
    };
}
public class CardMessages : DispatchProxy
{
    public string? LastUri { get; private set; }
    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        foreach (var value in args ?? [])
            if (value is IEnumerable<MessageSegment> segments) LastUri = segments.OfType<ImageSegment>().Single().Uri;
        if (method!.ReturnType == typeof(Task<SentMessage>)) return Task.FromResult(new SentMessage("sent"));
        throw new NotSupportedException(method.Name);
    }
}

sealed class CardImageFixture : IDisposable
{
    private readonly HttpListener listener = new();
    private readonly Task loop;
    private int requests;
    public int Requests => Volatile.Read(ref requests);
    private int retryRequests;
    public int RetryRequests => Volatile.Read(ref retryRequests);
    public string Url { get; }
    public CardImageFixture()
    {
        var socket = new TcpListener(IPAddress.Loopback, 0);
        socket.Start();
        var port = ((IPEndPoint)socket.LocalEndpoint).Port;
        socket.Stop();
        Url = $"http://127.0.0.1:{port}/";
        listener.Prefixes.Add(Url);
        listener.Start();
        loop = Task.Run(async () =>
        {
            while (listener.IsListening)
            {
                HttpListenerContext request;
                try { request = await listener.GetContextAsync(); } catch { break; }
                Interlocked.Increment(ref requests);
                if (request.Request.Url!.AbsolutePath == "/retry" && Interlocked.Increment(ref retryRequests) <= 2)
                {
                    request.Response.StatusCode = 503;
                    request.Response.Close();
                    continue;
                }
                request.Response.ContentType = "image/png";
                request.Response.ContentLength64 = CardRender.Png.Length;
                await request.Response.OutputStream.WriteAsync(CardRender.Png);
                request.Response.Close();
            }
        });
    }
    public void Dispose() { listener.Close(); loop.GetAwaiter().GetResult(); }
}
