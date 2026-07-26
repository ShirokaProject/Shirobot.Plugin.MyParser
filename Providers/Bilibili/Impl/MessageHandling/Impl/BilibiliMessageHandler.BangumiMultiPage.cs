
using System.Text;
using Shirobot.Plugin.MyParser.Providers.Common.MessageHandling;
using Shirobot.Plugin.MyParser.Providers.Bilibili.Models;
using ShiroBot.Qq.Model;
using ShiroBot.SDK.Models;


namespace Shirobot.Plugin.MyParser.Providers.Bilibili.Impl.MessageHandling.Impl;

internal sealed partial class BilibiliMessageHandler
{
private async Task SendBangumiForwardAsync(MessageEvent message, BilibiliBangumiParseResult result, bool sendEpHint = true)
    {
        var senderId = GetBotOrSenderId(message);
        var senderName = "Bilibili 番剧";
        var forwarded = new List<QqForwardedMessage>();
        var headerSegments = new List<QqOutgoingSegment>();
        var coverTask = string.IsNullOrWhiteSpace(result.CoverUrl)
            ? Task.FromResult<(string Uri, string? LocalPath)>((string.Empty, null))
            : BuildRemoteImageAsync(result.CoverUrl, result.MediaUrl ?? result.SeasonUrl, $"bilibili_bangumi_cover_{result.MediaId ?? result.SeasonId ?? result.RequestedEpId ?? 0}");
        var cover = await coverTask;
        if (!string.IsNullOrWhiteSpace(cover.Uri))
        {
            headerSegments.Add(new QqImageOutgoing(cover.Uri));
        }

        headerSegments.Add(new QqTextOutgoing(BuildBangumiHeaderText(result)));
        forwarded.Add(new QqForwardedMessage(senderId, senderName, headerSegments));

        foreach (var chunk in result.Episodes.Chunk(10))
        {
            forwarded.Add(new QqForwardedMessage(senderId, senderName,
            [
                new QqTextOutgoing(BuildBangumiEpisodeChunkText(chunk, result.RequestedEpId))
            ]));
        }

        var title = string.IsNullOrWhiteSpace(result.Title) ? "Bilibili 番剧" : TrimLine(result.Title!, 48);
        var preview = new[]
        {
            result.MediaId is null ? $"Season ID {result.SeasonId}" : $"Media ID {result.MediaId}",
            result.PublishText ?? $"共 {result.Episodes.Count} 话",
            result.RatingText ?? "剧集列表",
        };
        var summary = $"番剧 · {result.Episodes.Count}话";
        var forward = MessageHandlerCommon.BuildForwardSegment(forwarded, title, preview, summary, "Bilibili 番剧");
        await context.Message.ReplyAsync(message, forward);

        if (sendEpHint && result.RequestedEpId is { } epId)
        {
            await ReplyAsync(message, $"解析 https://www.bilibili.com/bangumi/play/ep{epId} 即可。");
        }
    }

    private static string BuildBangumiHeaderText(BilibiliBangumiParseResult result)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Bilibili 番剧解析成功");
        if (!string.IsNullOrWhiteSpace(result.Title)) sb.AppendLine($"名称：{result.Title}");
        if (result.MediaId is not null) sb.AppendLine($"Media ID {result.MediaId}");
        if (result.SeasonId is not null) sb.AppendLine($"Season ID {result.SeasonId}");
        if (!string.IsNullOrWhiteSpace(result.PublishText)) sb.AppendLine(result.PublishText);
        if (!string.IsNullOrWhiteSpace(result.RatingText)) sb.AppendLine(result.RatingText);
        if (!string.IsNullOrWhiteSpace(result.PlayText)) sb.AppendLine(result.PlayText);
        if (!string.IsNullOrWhiteSpace(result.FollowText)) sb.AppendLine(result.FollowText);
        if (result.Styles.Count > 0) sb.AppendLine("类型：" + string.Join(" / ", result.Styles));
        if (!string.IsNullOrWhiteSpace(result.Evaluate)) sb.AppendLine("简介：" + TrimLine(result.Evaluate, 180));
        if (!string.IsNullOrWhiteSpace(result.MediaUrl)) sb.AppendLine("作品：" + result.MediaUrl);
        if (!string.IsNullOrWhiteSpace(result.SeasonUrl)) sb.AppendLine("季度：" + result.SeasonUrl);
        return sb.ToString().TrimEnd();
    }

    private static string BuildBangumiEpisodeChunkText(IEnumerable<BilibiliBangumiEpisodeInfo> episodes, long? requestedEpId)
    {
        var sb = new StringBuilder();
        foreach (var ep in episodes)
        {
            var selected = requestedEpId is not null && ep.EpId == requestedEpId ? " ← 当前链接" : string.Empty;
            var title = string.Join(" ", new[] { ep.Title, ep.LongTitle }.Where(i => !string.IsNullOrWhiteSpace(i)));
            sb.AppendLine($"第{ep.Index}话{selected}: {TrimLine(string.IsNullOrWhiteSpace(title) ? "未命名" : title, 100)}");
            if (ep.DurationMilliseconds > 0) sb.AppendLine($"时长：{FormatDurationText(ep.DurationMilliseconds / 1000)}");
            if (!string.IsNullOrWhiteSpace(ep.Url)) sb.AppendLine($"链接：{ep.Url}");
            sb.AppendLine();
        }

        return sb.ToString().TrimEnd();
    }

    private async Task SendMultiPageForwardAsync(MessageEvent message, BilibiliMultiPageParseResult result)
    {
        var senderId = GetBotOrSenderId(message);
        var senderName = string.IsNullOrWhiteSpace(result.AuthorName) ? "Bilibili 分P视频" : result.AuthorName!;
        var forwarded = new List<QqForwardedMessage>();
        var headerSegments = new List<QqOutgoingSegment>();
        var headerCoverTask = string.IsNullOrWhiteSpace(result.CoverUrl)
            ? Task.FromResult<(string Uri, string? LocalPath)>((string.Empty, null))
            : BuildRemoteImageAsync(result.CoverUrl, result.SourceUrl, $"bilibili_multipage_cover_{result.Bvid}");
        var headerCover = await headerCoverTask;
        if (!string.IsNullOrWhiteSpace(headerCover.Uri))
        {
            headerSegments.Add(new QqImageOutgoing(headerCover.Uri));
        }

        headerSegments.Add(new QqTextOutgoing(BuildMultiPageHeaderText(result)));
        forwarded.Add(new QqForwardedMessage(senderId, senderName, headerSegments));

        var coverImageLimit = Math.Max(0, config.BilibiliMultiPageCoverImageLimit);
        var pages = result.Pages.ToArray();
        var pageCoverInputs = pages
            .Where(page => page.Page <= coverImageLimit && !string.IsNullOrWhiteSpace(page.CoverUrl))
            .ToArray();
        var pageCovers = await MessageFetchConcurrency.SelectParallelOrderedAsync(
            pageCoverInputs,
            MessageFetchConcurrency.DefaultImageConcurrency,
            page => BuildRemoteImageAsync(page.CoverUrl, page.SourceUrl, $"bilibili_multipage_{result.Bvid}_p{page.Page:D3}"));
        var coverByPage = pageCoverInputs.Zip(pageCovers, (page, cover) => (page.Page, cover))
            .ToDictionary(i => i.Page, i => i.cover);

        foreach (var page in pages)
        {
            var segments = new List<QqOutgoingSegment>();
            if (coverByPage.TryGetValue(page.Page, out var pageCover) && !string.IsNullOrWhiteSpace(pageCover.Uri))
            {
                segments.Add(new QqImageOutgoing(pageCover.Uri));
            }

            segments.Add(new QqTextOutgoing(BuildMultiPagePageText(page)));
            forwarded.Add(new QqForwardedMessage(senderId, senderName, segments));
        }

        var title = string.IsNullOrWhiteSpace(result.Title) ? $"Bilibili 分P视频 {result.Bvid}" : TrimLine(result.Title!, 48);
        var preview = new[]
        {
            $"{result.PageCount} 个分P",
            string.IsNullOrWhiteSpace(result.AuthorName) ? result.Bvid : result.AuthorName!,
            "仅展示分P信息，不下载视频",
        };
        var summary = $"分P视频 · {result.PageCount}P";
        var forward = MessageHandlerCommon.BuildForwardSegment(forwarded, title, preview, summary, "Bilibili 分P");
        await context.Message.ReplyAsync(message, forward);

        var prompt = await SendReplyAsync(message, "已默认解析 P1；如需解析其它分P，请在10min内用数字回复此消息。");
        SubscribeBilibiliPageReply(result, prompt.MessageId);
        await ParseAndReplyAsync(message, $"https://www.bilibili.com/video/{result.Bvid}/?p=1");
    }

    private static string BuildMultiPageHeaderText(BilibiliMultiPageParseResult result)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Bilibili 分P视频解析成功");
        sb.AppendLine($"BV：{result.Bvid}");
        sb.AppendLine($"分P数量：{result.PageCount}");
        if (!string.IsNullOrWhiteSpace(result.Title)) sb.AppendLine($"标题：{TrimLine(result.Title, 120)}");
        if (!string.IsNullOrWhiteSpace(result.AuthorName)) sb.AppendLine($"UP：{result.AuthorName}");
        if (result.RequestedPage > 1) sb.AppendLine($"当前链接指定：P{result.RequestedPage}");
        if (!string.IsNullOrWhiteSpace(result.SourceUrl)) sb.AppendLine($"链接：{result.SourceUrl}");
        return sb.ToString().TrimEnd();
    }

    private static string BuildMultiPagePageText(BilibiliVideoPageInfo page)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"P{page.Page}: {TrimLine(string.IsNullOrWhiteSpace(page.PartTitle) ? "未命名" : page.PartTitle!, 80)}");
        if (page.DurationSeconds > 0) sb.AppendLine($"时长：{FormatDurationText(page.DurationSeconds)}");
        sb.AppendLine($"链接：{page.SourceUrl}");
        return sb.ToString().TrimEnd();
    }
}
