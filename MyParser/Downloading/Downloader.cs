using Shirobot.Plugin.MyParser.Parsing;

namespace Shirobot.Plugin.MyParser.Downloading;

internal sealed class Downloader(DownloadProgressLogger progressLogger)
{
    private readonly DownloadProbeService _probeService = new();
    private readonly LightDlDownloadService _downloadService = new(progressLogger);

    public async Task<long> DownloadAsync(HttpRangeDownloadRequest request, CancellationToken cancellationToken = default)
    {
        var probe = await _probeService.ProbeAsync(request, cancellationToken).ConfigureAwait(false);
        if (request.MaxBytes != long.MaxValue && probe.ContentLength is > 0 && probe.ContentLength > request.MaxBytes)
            throw request.CreateTooLargeException(probe.ContentLength.Value);

        return await _downloadService.DownloadAsync(request, probe, cancellationToken).ConfigureAwait(false);
    }

    public Task<long> DownloadStreamAsync(HttpRangeDownloadRequest request, CancellationToken cancellationToken = default) =>
        DownloadAsync(request, cancellationToken);

    public Task<(long? ContentLength, bool AcceptRanges)> ProbeAsync(
        HttpRangeDownloadRequest request,
        CancellationToken cancellationToken) => _probeService.ProbeAsync(request, cancellationToken);
}
