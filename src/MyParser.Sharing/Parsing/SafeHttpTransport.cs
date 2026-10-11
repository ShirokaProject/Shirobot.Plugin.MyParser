using System.Net;
using System.Net.Sockets;

namespace Shirobot.Plugin.MyParser.Parsing;

/// <summary>HTTP transport that pins connections to validated public IP addresses.</summary>
public static class SafeHttpTransport
{
    public const int MaximumBufferedRedirectBodyBytes = 8 * 1024 * 1024;

    public static HttpMessageHandler CreateHandler(
        DecompressionMethods automaticDecompression = DecompressionMethods.All,
        CookieContainer? cookieContainer = null)
    {
        return CreateRedirectHandler(CreateSocketsHandler(automaticDecompression, cookieContainer));
    }

    public static SocketsHttpHandler CreateSocketsHandler(
        DecompressionMethods automaticDecompression = DecompressionMethods.All,
        CookieContainer? cookieContainer = null)
    {
        return new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            MaxAutomaticRedirections = UrlRedirectResolver.MaximumRedirects,
            AutomaticDecompression = automaticDecompression,
            UseProxy = false,
            UseCookies = cookieContainer is not null,
            CookieContainer = cookieContainer ?? new CookieContainer(),
            ConnectCallback = ConnectToValidatedPublicAddressAsync,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            PooledConnectionIdleTimeout = TimeSpan.FromSeconds(30),
        };
    }

    public static HttpMessageHandler CreateRedirectHandler(SocketsHttpHandler innerHandler) =>
        new SafeRedirectHandler(innerHandler);

    public static bool IsAllowedUri(Uri? uri)
    {
        if (uri is null || !uri.IsAbsoluteUri || uri.Scheme is not ("http" or "https")
            || !string.IsNullOrEmpty(uri.UserInfo))
        {
            return false;
        }

        return uri.Scheme == Uri.UriSchemeHttp
            ? uri.Port == 80
            : uri.Port == 443;
    }

    public static async Task<bool> IsPublicHttpTargetAsync(Uri uri, CancellationToken cancellationToken)
    {
        if (!IsAllowedUri(uri)) return false;
        var addresses = await ResolvePublicAddressesAsync(uri.DnsSafeHost, cancellationToken).ConfigureAwait(false);
        return addresses.Length > 0;
    }

    private static async ValueTask<Stream> ConnectToValidatedPublicAddressAsync(
        SocketsHttpConnectionContext context,
        CancellationToken cancellationToken)
    {
        var requestUri = context.InitialRequestMessage.RequestUri;
        var endpoint = context.DnsEndPoint;
        if (!IsAllowedUri(requestUri) || requestUri!.Port != endpoint.Port
            || !SameHost(requestUri.DnsSafeHost, endpoint.Host))
        {
            throw new HttpRequestException("出站 HTTP 请求的目标地址无效或使用了不允许的端口。");
        }

        var addresses = await ResolvePublicAddressesAsync(endpoint.Host, cancellationToken).ConfigureAwait(false);
        SocketException? lastError = null;
        foreach (var address in addresses)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
            {
                NoDelay = true,
            };
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, endpoint.Port), cancellationToken).ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (SocketException ex)
            {
                lastError = ex;
                socket.Dispose();
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }

        throw new HttpRequestException("无法连接到经过校验的公网地址。", lastError);
    }

    private static async Task<IPAddress[]> ResolvePublicAddressesAsync(string host, CancellationToken cancellationToken)
    {
        IPAddress[] addresses;
        if (IPAddress.TryParse(host, out var literal))
        {
            addresses = [literal];
        }
        else
        {
            addresses = await Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false);
        }

        if (addresses.Length == 0 || addresses.Any(address => !IsPublicAddress(address)))
        {
            throw new HttpRequestException("拒绝连接到非公网或特殊用途 IP 地址。");
        }

        return addresses;
    }

    private static bool SameHost(string requestHost, string endpointHost)
    {
        if (IPAddress.TryParse(requestHost, out var requestAddress)
            && IPAddress.TryParse(endpointHost, out var endpointAddress))
        {
            return NormalizeAddress(requestAddress).Equals(NormalizeAddress(endpointAddress));
        }

        return string.Equals(requestHost.TrimEnd('.'), endpointHost.TrimEnd('.'), StringComparison.OrdinalIgnoreCase);
    }

    private static IPAddress NormalizeAddress(IPAddress address) =>
        address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

    private static bool IsPublicAddress(IPAddress address)
    {
        address = NormalizeAddress(address);
        if (IPAddress.IsLoopback(address)) return false;

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var bytes = address.GetAddressBytes();
            var isGlobalUnicast = (bytes[0] & 0xE0) == 0x20; // 2000::/3
            var isSixToFour = bytes[0] == 0x20 && bytes[1] == 0x02;
            var isTeredo = bytes[0] == 0x20 && bytes[1] == 0x01 && bytes[2] == 0 && bytes[3] == 0;
            var isOrchid = bytes[0] == 0x20 && bytes[1] == 0x01 && bytes[2] == 0 && (bytes[3] & 0xF0) == 0x20;
            var isDocumentation = bytes[0] == 0x20 && bytes[1] == 0x01
                                  && bytes[2] == 0x0D && bytes[3] == 0xB8;
            return isGlobalUnicast && !isSixToFour && !isTeredo && !isOrchid && !isDocumentation
                   && !address.IsIPv6LinkLocal
                   && !address.IsIPv6SiteLocal
                   && !address.IsIPv6UniqueLocal
                   && !address.IsIPv6Multicast
                   && !address.Equals(IPAddress.IPv6Any);
        }

        if (address.AddressFamily != AddressFamily.InterNetwork) return false;
        var ipv4 = address.GetAddressBytes();
        var a = ipv4[0];
        var b = ipv4[1];
        var c = ipv4[2];
        return a != 0 && a != 10 && a != 127 && a < 224
               && !(a == 169 && b == 254)
               && !(a == 172 && b is >= 16 and <= 31)
               && !(a == 192 && b == 168)
               && !(a == 100 && b is >= 64 and <= 127)
               && !(a == 192 && b == 0 && c == 0)
               && !(a == 192 && b == 0 && c == 2)
               && !(a == 192 && b == 88 && c == 99)
               && !(a == 198 && b is 18 or 19)
               && !(a == 198 && b == 51 && c == 100)
               && !(a == 203 && b == 0 && c == 113);
    }

    private sealed class SafeRedirectHandler(HttpMessageHandler innerHandler) : DelegatingHandler(innerHandler)
    {
        private static readonly HashSet<HttpStatusCode> RedirectStatusCodes =
        [
            HttpStatusCode.MultipleChoices,
            HttpStatusCode.MovedPermanently,
            HttpStatusCode.Redirect,
            HttpStatusCode.SeeOther,
            HttpStatusCode.TemporaryRedirect,
            HttpStatusCode.PermanentRedirect,
        ];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (!IsAllowedUri(request.RequestUri))
            {
                throw new HttpRequestException("拒绝发送到无效、非 HTTP(S) 或非标准端口的 URL。");
            }

            var originalContent = await BufferContentAsync(request.Content, cancellationToken).ConfigureAwait(false);
            var contentHeaders = GetContentHeaders(request.Content);
            var currentHasContent = request.Content is not null;
            var currentRequest = request;
            var ownsCurrentRequest = false;
            var redirectCount = 0;
            HttpResponseMessage? response = null;
            try
            {
                while (true)
                {
                    response = await base.SendAsync(currentRequest, cancellationToken).ConfigureAwait(false);
                    if (!RedirectStatusCodes.Contains(response.StatusCode) || response.Headers.Location is null)
                    {
                        return response;
                    }

                    if (redirectCount >= UrlRedirectResolver.MaximumRedirects)
                    {
                        throw new HttpRequestException($"HTTP 重定向超过 {UrlRedirectResolver.MaximumRedirects} 跳限制。");
                    }

                    var currentUri = currentRequest.RequestUri!;
                    var nextUri = response.Headers.Location.IsAbsoluteUri
                        ? response.Headers.Location
                        : new Uri(currentUri, response.Headers.Location);
                    if (!IsAllowedUri(nextUri)
                        || (currentUri.Scheme == Uri.UriSchemeHttps && nextUri.Scheme != Uri.UriSchemeHttps))
                    {
                        throw new HttpRequestException("拒绝无效重定向或 HTTPS 降级重定向。");
                    }

                    var statusCode = response.StatusCode;
                    var nextMethod = GetRedirectMethod(currentRequest.Method, statusCode);
                    var dropContent = nextMethod == HttpMethod.Get && nextMethod != currentRequest.Method;
                    var preserveContent = currentHasContent && !dropContent;
                    var crossOrigin = !IsSameOrigin(currentUri, nextUri);
                    var nextRequest = CloneRequest(
                        currentRequest,
                        nextUri,
                        nextMethod,
                        preserveContent ? originalContent : null,
                        preserveContent ? contentHeaders : [],
                        crossOrigin);

                    response.Dispose();
                    response = null;
                    if (ownsCurrentRequest) currentRequest.Dispose();
                    currentRequest = nextRequest;
                    ownsCurrentRequest = true;
                    currentHasContent = preserveContent;
                    redirectCount++;
                }
            }
            catch
            {
                response?.Dispose();
                if (ownsCurrentRequest) currentRequest.Dispose();
                throw;
            }
        }

        private static async Task<byte[]?> BufferContentAsync(HttpContent? content, CancellationToken cancellationToken)
        {
            if (content is null) return null;
            if (content.Headers.ContentLength is > MaximumBufferedRedirectBodyBytes)
            {
                throw new HttpRequestException("拒绝重定向超大 HTTP 请求体。");
            }

            var bytes = await content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            if (bytes.Length > MaximumBufferedRedirectBodyBytes)
            {
                throw new HttpRequestException("拒绝重定向超大 HTTP 请求体。");
            }

            return bytes;
        }

        private static Dictionary<string, string[]> GetContentHeaders(HttpContent? content) => content is null
            ? []
            : content.Headers
                .Where(pair => !pair.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)
                               && !pair.Key.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase))
                .ToDictionary(pair => pair.Key, pair => pair.Value.ToArray(), StringComparer.OrdinalIgnoreCase);

        private static HttpRequestMessage CloneRequest(
            HttpRequestMessage source,
            Uri uri,
            HttpMethod method,
            byte[]? contentBytes,
            IReadOnlyDictionary<string, string[]> contentHeaders,
            bool crossOrigin)
        {
            var clone = new HttpRequestMessage(method, uri)
            {
                Version = source.Version,
                VersionPolicy = source.VersionPolicy,
            };
            foreach (var header in source.Headers)
            {
                if (IsHopByHopHeader(header.Key)) continue;
                if (crossOrigin && IsSensitiveHeader(header.Key)) continue;
                clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            foreach (var option in source.Options)
            {
                clone.Options.Set(new HttpRequestOptionsKey<object?>(option.Key), option.Value);
            }

            if (contentBytes is not null)
            {
                clone.Content = new ByteArrayContent(contentBytes);
                foreach (var header in contentHeaders)
                    clone.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            return clone;
        }

        private static bool IsSensitiveHeader(string name) =>
            name.Equals("Authorization", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Proxy-Authorization", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Cookie", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Origin", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Referer", StringComparison.OrdinalIgnoreCase);

        private static bool IsHopByHopHeader(string name) =>
            name.Equals("Host", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Connection", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Keep-Alive", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Proxy-Connection", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Upgrade", StringComparison.OrdinalIgnoreCase)
            || name.Equals("TE", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Trailer", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Expect", StringComparison.OrdinalIgnoreCase);

        private static bool IsSameOrigin(Uri left, Uri right) =>
            string.Equals(left.Scheme, right.Scheme, StringComparison.OrdinalIgnoreCase)
            && left.Port == right.Port
            && string.Equals(left.Host, right.Host, StringComparison.OrdinalIgnoreCase);

        private static HttpMethod GetRedirectMethod(HttpMethod method, HttpStatusCode statusCode)
        {
            if (statusCode == HttpStatusCode.SeeOther && method != HttpMethod.Head) return HttpMethod.Get;
            if ((statusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Redirect) && method == HttpMethod.Post)
                return HttpMethod.Get;
            return method;
        }
    }
}
