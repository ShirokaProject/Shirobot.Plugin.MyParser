using System.Net;
using System.Text.Json;
using MyParser.Provider.BiliBili.Models;

namespace MyParser.Provider.BiliBili.Infrastructure;

internal static class BilibiliCdnPolicy
{
    private static readonly string[] PcdnDomains =
    [
        "mcdn.bilivideo.cn", "mcdn.bilivideo.com", "mcdn.bilivideo.net",
        "szbdyd.com", "nexusedgeio.com", "ahdohpiechei.com", "mountaintoys.cn", "bvmlt.top",
    ];

    internal static bool IsPcdnUrl(string url)
    {
        if (!Uri.TryCreate(url.StartsWith("//", StringComparison.Ordinal) ? "https:" + url : url,
                UriKind.Absolute, out var uri)) return false;

        var host = uri.IdnHost.TrimEnd('.');
        if (PcdnDomains.Any(domain => host.Equals(domain, StringComparison.OrdinalIgnoreCase)
                || host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase))) return true;

        // Some UPOS hosts redirect to PCDN; even a mirror URL can carry os=mcdn.
        var isBiliVideo = host.Equals("bilivideo.com", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".bilivideo.com", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".bilivideo.cn", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".bilivideo.net", StringComparison.OrdinalIgnoreCase);
        if (isBiliVideo && (host.Split('.')[0].Contains("302", StringComparison.Ordinal)
                || host.Contains("-live-p2p", StringComparison.OrdinalIgnoreCase)
                || host.Contains("-live-pcdn", StringComparison.OrdinalIgnoreCase)
                || host.Equals("upos-sz-mirror14b.bilivideo.com", StringComparison.OrdinalIgnoreCase))) return true;

        if (IPAddress.TryParse(host.Trim('[', ']'), out _) && uri.AbsolutePath.StartsWith("/v1/resource", StringComparison.OrdinalIgnoreCase))
            return true;

        return uri.Query.TrimStart('?').Split('&').Any(part =>
        {
            var pair = part.Split('=', 2);
            return pair.Length == 2 && Uri.UnescapeDataString(pair[0]).Equals("os", StringComparison.OrdinalIgnoreCase)
                && Uri.UnescapeDataString(pair[1]).Equals("mcdn", StringComparison.OrdinalIgnoreCase);
        });
    }

    internal static async Task<(List<BilibiliMediaStream> Videos, List<BilibiliMediaStream> Audios)> ResolveDashStreamsAsync(
        Func<bool, CancellationToken, Task<(List<BilibiliMediaStream> Videos, List<BilibiliMediaStream> Audios)>> fetch,
        bool blockPcdn, CancellationToken cancellationToken)
    {
        var original = await fetch(false, cancellationToken);
        if (!blockPcdn) return original;

        var videos = original.Videos;
        var audios = original.Audios;
        if (videos.Concat(audios).Any(stream => stream.UrlCandidates.Any(IsPcdnUrl)
                && !stream.UrlCandidates.Any(url => !string.IsNullOrWhiteSpace(url) && !IsPcdnUrl(url))))
        {
            global::Shirobot.Plugin.MyParser.DiagnosticLog.Info("MyParser Bilibili 部分播放流仅有 PCDN 地址，重新请求一次播放接口。");
            try
            {
                var retry = await fetch(true, cancellationToken);
                videos = MergeStreams(videos, retry.Videos);
                audios = MergeStreams(audios, retry.Audios);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex) when (ex is HttpRequestException or JsonException or BilibiliParseException or OperationCanceledException)
            {
                global::Shirobot.Plugin.MyParser.DiagnosticLog.Warning($"MyParser Bilibili 重新请求 CDN 失败，保留原节点兜底: error={ex.GetType().Name}");
            }
        }

        return (PrioritizeStreams(videos), PrioritizeStreams(audios));
    }

    private static List<BilibiliMediaStream> PrioritizeStreams(List<BilibiliMediaStream> streams)
    {
        var result = new List<BilibiliMediaStream>();
        foreach (var stream in streams)
        {
            // Keep the complete signed URL from the API; changing hosts can invalidate signatures.
            var urls = stream.UrlCandidates.Where(url => !string.IsNullOrWhiteSpace(url))
                .Distinct(StringComparer.Ordinal).ToArray();
            var regular = urls.Where(url => !IsPcdnUrl(url)).ToArray();
            var fallback = urls.Where(IsPcdnUrl).ToArray();
            if (regular.Length > 0) result.Add(stream with { Url = regular[0], BackupUrls = regular.Skip(1).ToList() });
            // Separate fallback streams let the downloader exhaust all regular CDN qualities first.
            if (fallback.Length > 0) result.Add(stream with
            {
                StreamId = stream.StreamId + "_pcdn",
                Url = fallback[0], BackupUrls = fallback.Skip(1).ToList(),
            });
        }
        return result;
    }

    private static List<BilibiliMediaStream> MergeStreams(List<BilibiliMediaStream> first, List<BilibiliMediaStream> second)
        => first.Concat(second).GroupBy(stream => (stream.QualityId, stream.CodecId, stream.Codecs))
            .Select((group, index) => group.First() with
            {
                StreamId = $"{(group.First().IsAudio ? "a" : "v")}{index}",
                Url = group.First().Url,
                BackupUrls = group.SelectMany(stream => stream.UrlCandidates).Distinct(StringComparer.Ordinal)
                    .Where(url => !string.Equals(url, group.First().Url, StringComparison.Ordinal)).ToList(),
            }).ToList();
}
