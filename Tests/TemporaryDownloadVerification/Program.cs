using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using ShiroBot.SDK.Plugin;
using Shirobot.Plugin.MyParser;
using Shirobot.Plugin.MyParser.Parsing;

var root = Path.Combine(Path.GetTempPath(), "myparser-temp-verification-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var pluginAssembly = typeof(MyParser.Provider.BiliBili.Views.BiliCard).Assembly;
var runtime = typeof(PluginConfig).Assembly.GetType("Shirobot.Plugin.MyParser.MyParserRuntime", true)!;
var bot = DispatchProxy.Create<IBotContext, TempBotContext>();
var context = (TempBotContext)bot;
context.Root = root;
var settings = Activator.CreateInstance(pluginAssembly.GetType("Shirobot.Plugin.MyParser.ProviderSettingsMonitor", true)!, bot, new PluginConfig())!;
settings.GetType().GetMethod("InitializeRuntimeDirectories")!.Invoke(settings, null);
var service = Activator.CreateInstance(pluginAssembly.GetType("Shirobot.Plugin.MyParser.Downloading.ProviderDownloadService", true)!)!;
var config = new PluginConfig { LogDownloadProgress=false, MaxVideoDownloadMegabytes=2 };
try
{
    var fixtures = (string)runtime.GetMethod("CreateTempDirectory")!.Invoke(null, null)!;
    var audioPath = Path.Combine(fixtures, "audio.mp3");
    var videoPath = Path.Combine(fixtures, "video.mp4");
    var aacPath = Path.Combine(fixtures, "audio.m4a");
    var imagePath = Path.Combine(fixtures, "image.png");
    await Ffmpeg("-f", "lavfi", "-i", "sine=frequency=440:duration=0.3", audioPath);
    await Ffmpeg("-f", "lavfi", "-i", "sine=frequency=440:duration=0.6", "-c:a", "aac", aacPath);
    await Ffmpeg("-f", "lavfi", "-i", "color=c=blue:s=32x32:d=0.3", "-c:v", "libx264", "-pix_fmt", "yuv420p", videoPath);
    await Ffmpeg("-f", "lavfi", "-i", "color=c=blue:s=32x32", "-frames:v", "1", imagePath);
    using var server = new FixtureServer(await File.ReadAllBytesAsync(audioPath), await File.ReadAllBytesAsync(videoPath), await File.ReadAllBytesAsync(imagePath));
    config.HttpProxy = server.Url; // Use the proxy transport for local HTTP fixtures.
    Func<HttpMethod,string,string?,HttpRequestMessage> requests = (method,url,range) => new(method,url);
    var audioRequest = new ProviderAudioDownloadRequest("neteasecloudmusic", "网易云音乐", "test", "audio-test", server.Url + "audio", "/ignored/legacy/path", "test", "mp3", requests);
    var first = await InvokeDownload("DownloadProviderAudioAsync", audioRequest);
    Check(first.LocalPath.StartsWith(root + Path.DirectorySeparatorChar) && File.Exists(first.LocalPath), "Audio escaped host temporary storage.");
    var downloadedAudio = await File.ReadAllBytesAsync(first.LocalPath);
    var originalAudio = await File.ReadAllBytesAsync(audioPath);
    Check(downloadedAudio.SequenceEqual(originalAudio), "Downloaded audio changed.");
    var count = context.Allocated.Count;
    Check((await InvokeDownload("DownloadProviderAudioAsync", audioRequest)).LocalPath == first.LocalPath && context.Allocated.Count == count, "Cached downloads were not reused.");
    Directory.Delete(Path.GetDirectoryName(first.LocalPath)!, true); // Simulate the host expiring a directory.
    var renewed = await InvokeDownload("DownloadProviderAudioAsync", audioRequest);
    Check(renewed.LocalPath != first.LocalPath && File.Exists(renewed.LocalPath), "Expired cache did not allocate a fresh host directory.");
    var videoRequest = new ProviderVideoDownloadRequest("bilibili", "Bilibili", "test", "video-test", [server.Url + "bad", server.Url + "video"], "/ignored/legacy/path", "test", "mp4", requests);
    var video = await InvokeDownload("DownloadProviderVideoAsync", videoRequest);
    Check(File.Exists(video.LocalPath) && video.LocalPath.StartsWith(root + Path.DirectorySeparatorChar), "Video fallback did not use host temporary storage.");
    var imageService = pluginAssembly.GetType("Shirobot.Plugin.MyParser.MessageHandling.RemoteImageFetchService", true)!;
    var image = await (Task<(string Uri,string? LocalPath)>)imageService.GetMethod("BuildRemoteImageAsync")!.Invoke(null,
        ["test",server.Url + "image",null,"test-image",null,10*1024L*1024L,true,server.Url,CancellationToken.None,true])!;
    Check(image.LocalPath is not null && File.Exists(image.LocalPath) && image.LocalPath.StartsWith(root + Path.DirectorySeparatorChar), "Image escaped host temporary storage.");
    var records = await (Task<IReadOnlyList<ProviderRecordVariant>>)service.GetType().GetMethod("BuildSilkRecordVariantsAsync")!.Invoke(service,
        [config,new ProviderRecordBuildRequest("neteasecloudmusic","网易云音乐","test",renewed.LocalPath,"test"),CancellationToken.None])!;
    Check(records.Count == 1 && records[0].SilkRate == 35000 && records.All(record => File.Exists(record.Path) && record.Path.StartsWith(root + Path.DirectorySeparatorChar)), "Silk escaped host temporary storage.");
    var aacRecords = await (Task<IReadOnlyList<ProviderRecordVariant>>)service.GetType().GetMethod("BuildSilkRecordVariantsAsync")!.Invoke(service,
        [config,new ProviderRecordBuildRequest("douyin","抖音","aac-test",aacPath,"aac-test"),CancellationToken.None])!;
    Check(aacRecords.Count == 1 && (await File.ReadAllBytesAsync(aacRecords[0].Path)).AsSpan().StartsWith(new byte[] { 2, (byte)'#', (byte)'!' }), "AAC did not produce Tencent Silk.");
    Check(Directory.GetFiles(root,"*.pcm",SearchOption.AllDirectories).Length == 0, "Intermediate PCM was retained after AAC encoding.");
    var invalidAudio = Path.Combine(fixtures,"invalid.mp3");
    await File.WriteAllBytesAsync(invalidAudio,new byte[] { 1, 2, 3 });
    try
    {
        await (Task)service.GetType().GetMethod("EncodeSilkAsync",BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(service,
            [new ProviderRecordBuildRequest("test","test","invalid",invalidAudio,"invalid"),Path.Combine(fixtures,"invalid.silk"),35000,CancellationToken.None])!;
        throw new InvalidOperationException("Invalid audio unexpectedly encoded.");
    }
    catch (Exception ex) when (ex is not InvalidOperationException || ex.Message != "Invalid audio unexpectedly encoded.") { }
    Check(!File.Exists(Path.Combine(fixtures,"invalid.silk")) && Directory.GetFiles(root,"*.pcm",SearchOption.AllDirectories).Length == 0,
        "Failed encoding retained output or intermediate PCM.");
    // A request queued behind another encoder must honor cancellation and
    // must not allocate temporary output before it acquires the slot.
    var encodingGate = (SemaphoreSlim)service.GetType().GetField("AudioEncodingGate",BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
    await encodingGate.WaitAsync();
    try
    {
        using var queuedCancellation = new CancellationTokenSource();
        var beforeQueued = context.Allocated.Count;
        var queuedOutput = Path.Combine(fixtures,"queued.silk");
        var queued = (Task)service.GetType().GetMethod("EncodeSilkAsync",BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(service,
            [new ProviderRecordBuildRequest("test","test","queued",renewed.LocalPath,"queued"),queuedOutput,35000,queuedCancellation.Token])!;
        Check(!queued.IsCompleted, "Queued encoding ignored the concurrency slot.");
        queuedCancellation.Cancel();
        try { await queued; throw new InvalidOperationException("Queued encoding ignored cancellation."); }
        catch(OperationCanceledException) when(queuedCancellation.IsCancellationRequested) { }
        Check(context.Allocated.Count == beforeQueued && !File.Exists(queuedOutput), "Canceled queued encoding allocated output.");
    }
    finally { encodingGate.Release(); }
    var combined = await (Task<string>)service.GetType().GetMethod("CreateStillImageVideoWithAudioAsync")!.Invoke(service,
        [config,image.LocalPath,renewed.LocalPath,CancellationToken.None])!;
    Check(File.Exists(combined) && combined.StartsWith(root + Path.DirectorySeparatorChar), "Gallery synthesis escaped host temporary storage.");
    var looping = await (Task<string>)service.GetType().GetMethod("MuxLoopingVideoWithAudioAsync")!.Invoke(service,
        [config,video.LocalPath,aacPath,CancellationToken.None,3])!;
    Check(File.Exists(looping) && Directory.GetFiles(root,"mp4-storage.bin",SearchOption.AllDirectories).Length > 0,
        "SharpMP4 did not use host temporary storage for intermediate samples.");
    using var canceled = new CancellationTokenSource();
    canceled.Cancel();
    try
    {
        await (Task<(string FileUri,string LocalPath)>)service.GetType().GetMethod("DownloadProviderAudioAsync")!.Invoke(service,
            [config,audioRequest with { CacheKey="canceled-audio" },canceled.Token])!;
        throw new InvalidOperationException("Download cancellation was swallowed.");
    }
    catch (OperationCanceledException) when (canceled.IsCancellationRequested) { }
    var owned = runtime.GetMethod("IsTemporaryFile")!;
    Check((bool)owned.Invoke(null,[combined])! && !(bool)owned.Invoke(null,[root + "-outside/file.mp4"])!, "Temporary upload allowlist has an invalid boundary.");
    Check(context.Allocated.All(entry => entry.Retention == TimeSpan.FromHours(6)), "Unexpected host retention.");
    runtime.GetMethod("BeginUnload")!.Invoke(null,null);
    Check(File.Exists(video.LocalPath) && File.Exists(renewed.LocalPath) && File.Exists(combined), "Plugin unload deleted host-owned media.");
    Console.WriteLine("Host API retention, audio/video/image/Silk/gallery/SharpMP4 storage, fallback, cancellation, cache expiry and unload checks passed.");
}
finally { runtime.GetMethod("BeginUnload")!.Invoke(null,null); Directory.Delete(root,true); }

async Task<(string FileUri,string LocalPath)> InvokeDownload(string name,object request) =>
    await (Task<(string FileUri,string LocalPath)>)service.GetType().GetMethod(name)!.Invoke(service,[config,request,CancellationToken.None])!;
static void Check(bool valid,string message) { if(!valid) throw new InvalidOperationException(message); }
static async Task Ffmpeg(params string[] args)
{
    var info = new ProcessStartInfo("ffmpeg") { RedirectStandardError=true, UseShellExecute=false };
    foreach(var arg in new[]{"-hide_banner","-loglevel","error","-y"}.Concat(args)) info.ArgumentList.Add(arg);
    using var process = Process.Start(info)!;
    var error = process.StandardError.ReadToEndAsync();
    await process.WaitForExitAsync();
    if(process.ExitCode != 0) throw new InvalidOperationException(await error);
}
public class TempBotContext : DispatchProxy
{
    public string Root { get; set; } = "";
    public List<(string Path,TimeSpan Retention)> Allocated { get; } = [];
    protected override object? Invoke(MethodInfo? method,object?[]? args)
    {
        if(method!.Name == "get_PluginDirectory") return Root;
        if(method.Name != "CreateTempDirectory") throw new InvalidOperationException(method.Name);
        var retention = (TimeSpan)args![0]!;
        var path = Path.Combine(Root,Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        Allocated.Add((path,retention));
        return new TemporaryDirectory(path,DateTimeOffset.UtcNow.Add(retention));
    }
}
sealed class FixtureServer : IDisposable
{
    private readonly HttpListener listener = new();
    private readonly Task loop;
    public string Url { get; }
    public FixtureServer(byte[] audio,byte[] video,byte[] image)
    {
        var socket = new TcpListener(IPAddress.Loopback,0);socket.Start();
        var port = ((IPEndPoint)socket.LocalEndpoint).Port;socket.Stop();
        Url = $"http://127.0.0.1:{port}/";listener.Prefixes.Add(Url);listener.Start();
        loop = Task.Run(async () =>
        {
            while(listener.IsListening)
            {
                HttpListenerContext request;
                try { request = await listener.GetContextAsync(); } catch { break; }
                try
                {
                    var route = request.Request.Url!.AbsolutePath;
                    if(route == "/bad") {request.Response.StatusCode=500;request.Response.Close();continue;}
                    var bytes = route == "/video" ? video : route == "/image" ? image : audio;
                    request.Response.ContentType = route == "/image" ? "image/png" : "application/octet-stream";
                    request.Response.ContentLength64 = bytes.Length;
                    if(request.Request.HttpMethod != "HEAD") await request.Response.OutputStream.WriteAsync(bytes);
                    request.Response.Close();
                } catch { request.Response.Abort(); }
            }
        });
    }
    public void Dispose() {listener.Close();loop.GetAwaiter().GetResult();}
}
