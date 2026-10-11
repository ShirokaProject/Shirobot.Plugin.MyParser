using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Authentication;
using ShiroBot.SDK.Models;
using ShiroBot.SDK.Plugin;
using Shirobot.Plugin.MyParser;
using Shirobot.Plugin.MyParser.Parsing;

foreach (var (url, allowed) in new[]
{
    ("https://2485d7fb.ydycdn.com:58001/video", true),
    ("https://2485d7fb.YDYCDN.COM:58001/video", true),
    ("https://example.com:58001/video", true),
    ("https://other-cdn.example:8443/video", true),
    ("http://other-cdn.example:8080/video", true),
    ("https://2485d7fb.ydycdn.com:58002/video", true),
    ("http://2485d7fb.ydycdn.com:58001/video", true),
    ("https://user:password@2485d7fb.ydycdn.com:58001/video", false),
    ("https://127.0.0.1:58001/video", true),
    ("file:///tmp/video.mp4", false),
    ("ftp://example.com/video", false),
    ("https://example.com/video", true),
})
    if (SafeHttpTransport.IsAllowedUri(new Uri(url)) != allowed)
        throw new Exception("HTTP target URI policy mismatch: " + url);
foreach (var target in new[] { "http://127.0.0.1:8080/", "https://10.0.0.1:58001/", "http://[::1]:8080/" })
{
    try
    {
        if (await SafeHttpTransport.IsPublicHttpTargetAsync(new Uri(target), CancellationToken.None))
            throw new Exception("Private target was accepted: " + target);
    }
    catch (HttpRequestException) { }
}
Console.WriteLine("HTTP/HTTPS targets accept CDN domains and nonstandard ports; private addresses, unsupported schemes and embedded credentials remain rejected.");

var redirectType = typeof(SafeHttpTransport).GetNestedType("SafeRedirectHandler", BindingFlags.NonPublic)!;
var redirectFixture = new CdnRedirectFixture();
using (var redirects = (HttpMessageHandler)Activator.CreateInstance(redirectType,
    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, [redirectFixture], null)!)
using (var client = new HttpClient(redirects))
using (var request = new HttpRequestMessage(HttpMethod.Get, "https://media.example/play"))
{
    request.Headers.TryAddWithoutValidation("Cookie", "sessionid=test");
    request.Headers.TryAddWithoutValidation("Authorization", "Bearer test");
    request.Headers.TryAddWithoutValidation("Range", "bytes=0-0");
    using var response = await client.SendAsync(request);
    if (response.StatusCode != HttpStatusCode.PartialContent || redirectFixture.Calls != 3)
        throw new Exception("HTTP/HTTPS CDN redirects were not followed across nonstandard ports.");
}
Console.WriteLine("HTTPS CDN redirect on port 58001 and subsequent HTTP redirect on port 8080 are followed; cross-origin credentials are stripped and Range is preserved.");

var source = new MessageEvent { Platform = "qq", InstanceId = "test", MessageId = "source", Sender = new User("tester"), Channel = new Channel("test", ChannelType.Group), Segments = [] };
foreach (var (retries, failures, expectedCalls, expectedFailures) in new[] { (2, 2, 3, 0), (2, 9, 3, 1), (0, 2, 1, 1) })
{
    var services = DispatchProxy.Create<IProviderHostServices, RetryHost>();
    var provider = new RetryProvider { Failures = failures };
    var handler = new RetryHandler(new ProviderMessageHandlerContext(DispatchProxy.Create<IBotContext, RetryBot>(),
        new PluginConfig { NetworkRetryCount = retries }, provider, services));
    await handler.ParseAndReplyAsync(source, "test");
    var host = (RetryHost)services;
    Check(provider.Calls == expectedCalls, "Incorrect parse retry count.");
    Check(host.Failures == expectedFailures, "Intermediate network failures were reported.");
    Check(host.Faces.SequenceEqual(expectedFailures == 0 ? new[] { "351" } : new[] { "351", "9" }), "Retry sent duplicate/error reactions prematurely.");
    Check(handler.Presentations == (expectedFailures == 0 ? 1 : 0), "Retry duplicated media presentation.");
}
{
    var services = DispatchProxy.Create<IProviderHostServices, RetryHost>();
    var provider = new RetryProvider();
    var handler = new RetryHandler(new ProviderMessageHandlerContext(DispatchProxy.Create<IBotContext, RetryBot>(),
        new PluginConfig { NetworkRetryCount = 2 }, provider, services)) { FailPresentation = true };
    await handler.ParseAndReplyAsync(source, "test");
    Check(provider.Calls == 1 && handler.Presentations == 1, "Network error during sending duplicated a message.");
}
foreach (var error in new Exception[]
{
    new HttpRequestException("403", null, HttpStatusCode.Forbidden),
    new HttpRequestException("bad certificate", new AuthenticationException("certificate validation failed")),
    new HttpRequestException("拒绝连接到非公网或特殊用途 IP 地址。"),
    new IOException("disk error"), new InvalidDataException("unsupported format"), new LoginRequired()
})
{
    var calls = 0;
    try { await NetworkRetry.ExecuteAsync<int>(_ => { calls++; return Task.FromException<int>(error); }, 2); }
    catch (Exception caught) when (ReferenceEquals(error, caught)) { }
    Check(calls == 1, "Non-network/authentication failure was retried.");
}
foreach (var error in new Exception[] { RetryProvider.Reset(), new HttpRequestException("503", null, HttpStatusCode.ServiceUnavailable), new TimeoutException(), new TaskCanceledException() })
{
    var calls = 0;
    await NetworkRetry.ExecuteAsync(_ => ++calls == 1 ? Task.FromException<int>(error) : Task.FromResult(1), 1);
    Check(calls == 2, "Transient failure was not retried.");
}
{
    using var cancellation = new CancellationTokenSource();
    var calls = 0;
    var operation = NetworkRetry.ExecuteAsync<int>(_ => { calls++; return Task.FromException<int>(RetryProvider.Reset()); }, 2, cancellation.Token);
    cancellation.Cancel();
    try { await operation; throw new Exception("Cancellation ignored."); }
    catch (OperationCanceledException) { }
    Check(calls == 1, "Cancelled backoff issued another request.");
}
Console.WriteLine("SSL reset, retry counts, exhaustion, timeout/status classification, auth/certificate exclusion, cancellation and no duplicate delivery verified.");

static void Check(bool condition, string text) { if (!condition) throw new Exception(text); }
sealed class LoginRequired : Exception, IProviderClassifiedException { public ProviderFailureKind FailureKind => ProviderFailureKind.AuthenticationRequired; }
sealed class RetryProvider : IParseProvider
{
    public int Calls;
    public int Failures;
    public string Id => "douyin";
    public string Name => "抖音";
    public bool CanHandle(string text) => true;
    public Task<ParsedMedia> ParseAsync(string text, CancellationToken cancellationToken = default)
    {
        Calls++;
        return Calls <= Failures ? Task.FromException<ParsedMedia>(Reset())
            : Task.FromResult(new ParsedMedia { ProviderId = Id, ProviderName = Name, MediaId = "test" });
    }
    public static Exception Reset() => new HttpRequestException("The SSL connection could not be established",
        new IOException("Connection reset by peer", new SocketException((int)SocketError.ConnectionReset)));
}
sealed class RetryHandler(ProviderMessageHandlerContext context) : ProviderMessageHandlerBase(context)
{
    public int Presentations;
    public bool FailPresentation;
    public override string ProviderId => "douyin";
    protected override Task PresentAsync(MessageEvent message, ParsedMedia media, bool silentProviderMismatch, CancellationToken cancellationToken)
    {
        Presentations++;
        return FailPresentation ? Task.FromException(RetryProvider.Reset()) : Task.CompletedTask;
    }
}
public class RetryHost : DispatchProxy
{
    public int Failures;
    public List<string> Faces = [];
    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        if (method!.Name == "ReactAsync") Faces.Add((string)args![1]!);
        else if (method.Name == "ReportFailureAsync") Failures++;
        else throw new Exception("Unexpected operation: " + method.Name);
        return Task.CompletedTask;
    }
}
public class RetryBot : DispatchProxy
{
    protected override object? Invoke(MethodInfo? method, object?[]? args) => throw new NotSupportedException();
}

internal sealed class CdnRedirectFixture : HttpMessageHandler
{
    public int Calls { get; private set; }
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var expected = Calls switch
        {
            0 => "https://media.example/play",
            1 => "https://other-cdn.example:58001/video",
            _ => "http://third-cdn.example:8080/video",
        };
        if (request.RequestUri!.AbsoluteUri != expected) throw new Exception("Unexpected redirect target.");
        if (Calls > 0 && (request.Headers.Contains("Cookie") || request.Headers.Contains("Authorization")))
            throw new Exception("Credentials leaked to another CDN origin.");
        if (request.Headers.Range?.ToString() != "bytes=0-0") throw new Exception("Redirect lost the byte range.");
        Calls++;
        var response = new HttpResponseMessage(Calls < 3 ? HttpStatusCode.Redirect : HttpStatusCode.PartialContent)
        { Content = new ByteArrayContent([1]), RequestMessage = request };
        if (Calls < 3) response.Headers.Location = new Uri(Calls == 1
            ? "https://other-cdn.example:58001/video" : "http://third-cdn.example:8080/video");
        return Task.FromResult(response);
    }
}
