using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Authentication;
using ShiroBot.SDK.Models;
using ShiroBot.SDK.Plugin;
using Shirobot.Plugin.MyParser;
using Shirobot.Plugin.MyParser.Parsing;

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
