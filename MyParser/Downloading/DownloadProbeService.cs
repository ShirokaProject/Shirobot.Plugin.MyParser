using System.Net;
using Shirobot.Plugin.MyParser.Parsing;

namespace Shirobot.Plugin.MyParser.Downloading;

internal sealed class DownloadProbeService
{
    private static readonly HttpClient ProbeHttp = new(SafeHttpTransport.CreateHandler(DecompressionMethods.None))
    {
        Timeout = Timeout.InfiniteTimeSpan,
    };

    public async Task<(long? ContentLength, bool AcceptRanges)> ProbeAsync(
        HttpRangeDownloadRequest request,
        CancellationToken cancellationToken)
    {
        using var httpRequest = request.CreateRequest(HttpMethod.Get, "bytes=0-0");
        using var response = await ProbeHttp.SendAsync(
            httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw request.CreateHttpException(response.StatusCode);

        long? contentLength = response.Content.Headers.ContentRange?.Length ?? response.Content.Headers.ContentLength;
        var acceptRanges = response.StatusCode == HttpStatusCode.PartialContent
                           || response.Headers.AcceptRanges.Any(value =>
                               string.Equals(value, "bytes", StringComparison.OrdinalIgnoreCase))
                           || response.Content.Headers.ContentRange is not null;
        return (contentLength, acceptRanges);
    }
}
