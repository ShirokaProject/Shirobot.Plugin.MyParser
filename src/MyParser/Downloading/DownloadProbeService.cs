using System.Collections.Concurrent;
using System.Net;
using Shirobot.Plugin.MyParser.Parsing;
using Shirobot.Plugin.MyParser.Utility;

namespace Shirobot.Plugin.MyParser.Downloading;

internal sealed class DownloadProbeService
{
    private static readonly HttpClient DirectProbeHttp = new(SafeHttpTransport.CreateHandler(DecompressionMethods.None))
    {
        Timeout = Timeout.InfiniteTimeSpan,
    };

    private static readonly ConcurrentDictionary<string, HttpClient> ProxiedProbeHttp = new(StringComparer.Ordinal);

    public async Task<(long? ContentLength, bool AcceptRanges)> ProbeAsync(
        HttpRangeDownloadRequest request,
        CancellationToken cancellationToken)
    {
        var http = string.IsNullOrWhiteSpace(request.HttpProxy)
            ? DirectProbeHttp
            : ProxiedProbeHttp.GetOrAdd(request.HttpProxy, address =>
                new HttpClient(HttpProxySettings.CreateHandler(address, DecompressionMethods.None))
                {
                    Timeout = Timeout.InfiniteTimeSpan,
                });
        using var httpRequest = request.CreateRequest(HttpMethod.Get, "bytes=0-0");
        using var response = await http.SendAsync(
            httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            // Report the final redirect target without signed query strings or Cookies.
            var endpoint = response.RequestMessage?.RequestUri ?? httpRequest.RequestUri;
            throw new HttpRequestException(
                $"{request.CreateHttpException(response.StatusCode).Message}: endpoint={endpoint?.GetLeftPart(UriPartial.Path)}",
                null, response.StatusCode);
        }

        long? contentLength = response.Content.Headers.ContentRange?.Length ?? response.Content.Headers.ContentLength;
        var acceptRanges = response.StatusCode == HttpStatusCode.PartialContent
                           || response.Headers.AcceptRanges.Any(value =>
                               string.Equals(value, "bytes", StringComparison.OrdinalIgnoreCase))
                           || response.Content.Headers.ContentRange is not null;
        return (contentLength, acceptRanges);
    }
}
