using System.Net;
using System.Text;

namespace MyParser.Provider.NetEaseCloudMusic.Infrastructure;

internal sealed class NetEaseHttp : IDisposable
{
    public const string UserAgent = "Mozilla/5.0 (Windows NT 10.0; WOW64) AppleWebKit/537.36 (KHTML, like Gecko) Safari/537.36 Chrome/91.0.4472.164 NeteaseMusicDesktop/2.10.2.200154";
    public const string Referer = "https://music.163.com/";

    public HttpClient Client { get; }

    public NetEaseHttp(TimeSpan timeout)
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            AutomaticDecompression = DecompressionMethods.All,
            UseCookies = false,
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            PooledConnectionIdleTimeout = TimeSpan.FromSeconds(30),
        };
        Client = new HttpClient(handler) { Timeout = timeout };
        Client.DefaultRequestVersion = HttpVersion.Version11;
        Client.DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower;
        Client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        Client.DefaultRequestHeaders.Referrer = new Uri(Referer);
        Client.DefaultRequestHeaders.Accept.ParseAdd("application/json, text/plain, */*");
    }

    public async Task<string> GetStringAsync(string url, string cookie, CancellationToken cancellationToken)
    {
        using var request = CreateRequest(HttpMethod.Get, url, cookie);
        using var response = await SendWithRetryAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<string> PostFormAsync(string url, IEnumerable<KeyValuePair<string, string>> form, string cookie, CancellationToken cancellationToken)
    {
        using var request = CreateRequest(HttpMethod.Post, url, cookie);
        request.Content = new FormUrlEncodedContent(form);
        using var response = await SendWithRetryAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<string> ResolveRedirectUrlAsync(string url, CancellationToken cancellationToken)
    {
        using var request = CreateRequest(HttpMethod.Get, url, string.Empty);
        using var response = await SendWithRetryAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return response.RequestMessage?.RequestUri?.ToString() ?? url;
    }

    private async Task<HttpResponseMessage> SendWithRetryAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var snapshot = await SnapshotAsync(request, cancellationToken).ConfigureAwait(false);
        Exception? lastError = null;
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            using var cloned = CreateRequestFromSnapshot(snapshot);
            try
            {
                return await Client.SendAsync(cloned, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException ex) when (attempt < 3)
            {
                lastError = ex;
                await Task.Delay(TimeSpan.FromMilliseconds(250 * attempt), cancellationToken).ConfigureAwait(false);
            }
            catch (IOException ex) when (attempt < 3)
            {
                lastError = ex;
                await Task.Delay(TimeSpan.FromMilliseconds(250 * attempt), cancellationToken).ConfigureAwait(false);
            }
        }

        throw lastError ?? new HttpRequestException("网易云请求失败。");
    }

    private sealed record RequestSnapshot(
        HttpMethod Method,
        Uri? RequestUri,
        Version Version,
        HttpVersionPolicy VersionPolicy,
        List<KeyValuePair<string, IEnumerable<string>>> Headers,
        byte[]? ContentBytes,
        List<KeyValuePair<string, IEnumerable<string>>> ContentHeaders);

    private static async Task<RequestSnapshot> SnapshotAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        byte[]? contentBytes = null;
        var contentHeaders = new List<KeyValuePair<string, IEnumerable<string>>>();
        if (request.Content is not null)
        {
            contentBytes = await request.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            contentHeaders = request.Content.Headers.Select(i => new KeyValuePair<string, IEnumerable<string>>(i.Key, i.Value)).ToList();
        }

        return new RequestSnapshot(
            request.Method,
            request.RequestUri,
            request.Version,
            request.VersionPolicy,
            request.Headers.Select(i => new KeyValuePair<string, IEnumerable<string>>(i.Key, i.Value)).ToList(),
            contentBytes,
            contentHeaders);
    }

    private static HttpRequestMessage CreateRequestFromSnapshot(RequestSnapshot snapshot)
    {
        var request = new HttpRequestMessage(snapshot.Method, snapshot.RequestUri)
        {
            Version = snapshot.Version,
            VersionPolicy = snapshot.VersionPolicy,
        };
        foreach (var header in snapshot.Headers)
        {
            request.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        if (snapshot.ContentBytes is not null)
        {
            request.Content = new ByteArrayContent(snapshot.ContentBytes);
            foreach (var header in snapshot.ContentHeaders)
            {
                request.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
        }

        return request;
    }

    public static HttpRequestMessage CreateAudioRequest(HttpMethod method, string url, string? range = null)
    {
        var request = new HttpRequestMessage(method, url)
        {
            Version = HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
        };
        request.Headers.UserAgent.ParseAdd(UserAgent);
        request.Headers.Referrer = new Uri(Referer);
        if (!string.IsNullOrWhiteSpace(range))
        {
            request.Headers.TryAddWithoutValidation("Range", range);
        }
        return request;
    }

    private static HttpRequestMessage CreateRequest(HttpMethod method, string url, string cookie)
    {
        var request = new HttpRequestMessage(method, url)
        {
            Version = HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
        };
        request.Headers.Referrer = new Uri(Referer);
        if (!string.IsNullOrWhiteSpace(cookie))
        {
            request.Headers.TryAddWithoutValidation("Cookie", cookie);
        }
        return request;
    }

    public void Dispose() => Client.Dispose();
}
