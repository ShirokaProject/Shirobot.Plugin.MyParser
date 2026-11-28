using Avalonia.Media.Imaging;
using Shirobot.Plugin.MyParser.CardRendering;
using ShiroBot.SDK.Models;
using ShiroBot.SDK.Plugin;

namespace Shirobot.Plugin.MyParser.MessageHandling;

/// <summary>查询会话的消息能力；老适配器或没有实例 ID 时返回 null，表示未知。</summary>
internal static class MessageCapabilityProbe
{
    public static MessageCapabilities? Get(IMessageContext? messages, MessageEvent message)
    {
        if (messages is null || string.IsNullOrWhiteSpace(message.InstanceId)) return null;
        // 能力查询只用来挑发送方式，适配器查不到或报错时按「未知」处理，不影响投递。
        try { return messages.GetMessageCapabilities(new ChannelReference(message.InstanceId, message.Channel)); }
        catch (Exception ex) when (ex is not OperationCanceledException) { return null; }
    }
}

/// <summary>一次「聊天记录」投递最终走的是哪一级。</summary>
internal enum ForwardDeliveryLevel
{
    /// <summary>没有可发的内容。</summary>
    Nothing,
    /// <summary>1. QQ 合并转发。</summary>
    Forward,
    /// <summary>2. 一条消息带多个媒体（Telegram 相册、Discord 多附件），按平台上限分批。</summary>
    MediaGroups,
    /// <summary>3. 拼成一张长图。</summary>
    LongImage,
    /// <summary>4. 不自动发，回复「原图」后逐条发。</summary>
    OnReply,
    /// <summary>没有第 4 级时的最后手段：逐条发出。</summary>
    Each,
}

/// <summary>要按聊天记录发出去的一组内容。</summary>
internal sealed record ForwardPackage(IReadOnlyList<OutgoingForwardedMessage> Nodes, string Title)
{
    /// <summary>合并转发外层预览（最多 4 行）、底部摘要、消息列表里的提示文字。</summary>
    public IReadOnlyList<string>? Preview { get; init; }
    public string? Summary { get; init; }
    public string? Prompt { get; init; }

    public string ProviderId { get; init; } = "myparser";
    public string MediaId { get; init; } = "forward";
    /// <summary>通用长图的配色；null 用中性灰。</summary>
    public MediaCardPlatform? Platform { get; init; }

    /// <summary>第 3 级自定义长图（例如文章长图），返回图片 URI；null 时用通用长图拼接全部节点。</summary>
    public Func<CancellationToken, Task<string?>>? RenderLongImageAsync { get; init; }

    /// <summary>第 4 级回复哪条消息触发（例如刚发出的卡片）；null 时先发一条提示文字作为锚点。</summary>
    public string? ReplyAnchorMessageId { get; init; }

    /// <summary>
    /// 是否有第 4 级「回复 原图 才发」。false 时（抖音图集、已经是回复触发的评论图片）只走前三级，
    /// 长图也失败时逐条发出，长图上也不提示回复获取。
    /// </summary>
    public bool ReplyOnDemand { get; init; } = true;
}

/// <summary>
/// 多平台聊天记录兜底发送。按顺序尝试：
/// 1. QQ 合并转发；2. 一条消息带多个媒体（按平台单条媒体上限分批）；3. 拼成一张长图；
/// 4. 以上都不行时不自动发，回复「原图」后再逐条发，避免刷屏。
/// </summary>
internal sealed class ForwardFallbackSender(
    IMessageContext? messages,
    IProviderHostServices host,
    ReplyDownloadSubscriptions replies)
{
    public const string ReplyKeyword = "原图";
    private static readonly TimeSpan ReplyLifetime = TimeSpan.FromMinutes(60);
    private const int MaxLongImageItems = 30;
    private const int MaxReplyNodes = 30;

    public async Task<ForwardDeliveryLevel> SendAsync(MessageEvent message, ForwardPackage package, CancellationToken cancellationToken)
    {
        var nodes = package.Nodes.Where(node => node.Segments.Count > 0).ToArray();
        if (nodes.Length == 0) return ForwardDeliveryLevel.Nothing;
        package = package with { Nodes = nodes };
        var capabilities = GetCapabilities(message);

        if (messages is not null && SupportsForward(message, capabilities) && await TrySendForwardAsync(messages, message, package).ConfigureAwait(false))
            return Log(package, ForwardDeliveryLevel.Forward);

        cancellationToken.ThrowIfCancellationRequested();
        if (capabilities is { MaxMediaSegments: > 1 } && capabilities.NativeFeatures.HasFlag(MessageFeatures.Image)
            && await TrySendMediaGroupsAsync(message, package, capabilities).ConfigureAwait(false))
            return Log(package, ForwardDeliveryLevel.MediaGroups);

        cancellationToken.ThrowIfCancellationRequested();
        if (capabilities is null || capabilities.NativeFeatures.HasFlag(MessageFeatures.Image))
        {
            var sent = await TrySendLongImageAsync(message, package, cancellationToken).ConfigureAwait(false);
            if (sent is not null)
            {
                // 长图画不了视频；有视频时在长图上挂「回复 原图」，需要的人再取完整内容。
                if (package.ReplyOnDemand && HasVideo(package)) RegisterOnReply(message, sent, package);
                return Log(package, ForwardDeliveryLevel.LongImage);
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (!package.ReplyOnDemand)
        {
            await SendEachAsync(message, package).ConfigureAwait(false);
            return Log(package, ForwardDeliveryLevel.Each);
        }

        var anchor = package.ReplyAnchorMessageId;
        if (string.IsNullOrWhiteSpace(anchor))
        {
            var hint = await host.SendSegmentsAsync(message,
                [new TextSegment($"{package.Title}：共 {nodes.Length} 项内容，回复本消息「{ReplyKeyword}」获取。")]).ConfigureAwait(false);
            anchor = hint.MessageId;
        }
        if (!string.IsNullOrWhiteSpace(anchor)) RegisterOnReply(message, anchor, package);
        return Log(package, ForwardDeliveryLevel.OnReply);
    }

    /// <summary>预判这个会话最高能走到哪一级（不发送）：用于决定要不要先发封面卡片。</summary>
    public ForwardDeliveryLevel PredictLevel(MessageEvent message)
    {
        var capabilities = GetCapabilities(message);
        if (messages is not null && SupportsForward(message, capabilities)) return ForwardDeliveryLevel.Forward;
        if (capabilities is { MaxMediaSegments: > 1 } && capabilities.NativeFeatures.HasFlag(MessageFeatures.Image))
            return ForwardDeliveryLevel.MediaGroups;
        return capabilities is null || capabilities.NativeFeatures.HasFlag(MessageFeatures.Image)
            ? ForwardDeliveryLevel.LongImage
            : ForwardDeliveryLevel.OnReply;
    }

    private MessageCapabilities? GetCapabilities(MessageEvent message) => MessageCapabilityProbe.Get(messages, message);

    /// <summary>QQ 合并转发走 Raw 段；能力未知（老适配器）时只要是 QQ 就先试一次。</summary>
    private static bool SupportsForward(MessageEvent message, MessageCapabilities? capabilities) =>
        string.Equals(message.Platform, "qq", StringComparison.OrdinalIgnoreCase)
        && (capabilities is null || capabilities.NativeFeatures.HasFlag(MessageFeatures.Raw));

    private static async Task<bool> TrySendForwardAsync(IMessageContext messages, MessageEvent message, ForwardPackage package)
    {
        try
        {
            var preview = package.Preview ?? package.Nodes.Take(4).Select(PreviewLine).ToArray();
            var sent = await messages.ReplyAsync(message, new ForwardOutgoingSegment(package.Nodes, package.Title, preview,
                package.Summary ?? $"共 {package.Nodes.Count} 条", package.Prompt ?? package.Title)).ConfigureAwait(false);
            return !string.IsNullOrWhiteSpace(sent.MessageId);
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException)
        {
            BotLog.Warning($"MyParser 合并转发失败，改用兜底: media_id={package.MediaId}, error={ex.Message}");
            return false;
        }
    }

    /// <summary>文字合成一段放在第一条，图片 / 视频按平台单条上限分批；第一批发不出去才往下兜底，避免重复。</summary>
    private async Task<bool> TrySendMediaGroupsAsync(MessageEvent message, ForwardPackage package, MessageCapabilities capabilities)
    {
        var allowVideo = capabilities.NativeFeatures.HasFlag(MessageFeatures.Video);
        var media = package.Nodes.SelectMany(node => node.Segments)
            .Where(segment => segment is ImageSegment || allowVideo && segment is VideoSegment).ToArray();
        var text = string.Join("\n", package.Nodes.SelectMany(node => node.Segments).OfType<TextSegment>()
            .Select(segment => segment.Text.Trim()).Where(value => value.Length > 0));
        if (capabilities.MaxTextLength is { } maxText && text.Length > maxText) text = text[..Math.Max(0, maxText - 1)] + "…";
        if (media.Length == 0 && text.Length == 0) return false;

        var batches = media.Chunk(capabilities.MaxMediaSegments!.Value).ToList();
        if (batches.Count == 0) batches.Add([]);
        for (var index = 0; index < batches.Count; index++)
        {
            var segments = new List<MessageSegment>();
            if (index == 0 && text.Length > 0) segments.Add(new TextSegment(text));
            segments.AddRange(batches[index]);
            try
            {
                var sent = await host.SendSegmentsAsync(message, segments).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(sent.MessageId)) throw new InvalidOperationException("没有返回消息 ID。");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                BotLog.Warning($"MyParser 多媒体消息发送失败: media_id={package.MediaId}, batch={index + 1}/{batches.Count}, error={ex.Message}");
                if (index == 0) return false;
            }
        }

        return true;
    }

    /// <summary>发送长图，返回消息 ID；渲染或发送失败返回 null。</summary>
    private async Task<string?> TrySendLongImageAsync(MessageEvent message, ForwardPackage package, CancellationToken cancellationToken)
    {
        try
        {
            var uri = package.RenderLongImageAsync is not null
                ? await package.RenderLongImageAsync(cancellationToken).ConfigureAwait(false)
                : await RenderGenericLongImageAsync(package, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(uri)) return null;
            var sent = await host.SendImageAsync(message, new ImageSegment(uri)).ConfigureAwait(false);
            return string.IsNullOrWhiteSpace(sent.MessageId) ? null : sent.MessageId;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            BotLog.Warning($"MyParser 聊天记录长图失败，改为回复获取: media_id={package.MediaId}, error={ex.Message}");
            return null;
        }
    }

    private async Task<string?> RenderGenericLongImageAsync(ForwardPackage package, CancellationToken cancellationToken)
    {
        var bitmaps = new List<Bitmap>();
        try
        {
            var items = new List<ForwardLongImageItem>();
            var images = 0;
            foreach (var segment in package.Nodes.SelectMany(node => node.Segments))
            {
                if (segment is TextSegment text && !string.IsNullOrWhiteSpace(text.Text))
                    items.Add(new ForwardLongImageItem { Text = text.Text.Trim() });
                else if (segment is ImageSegment image && images < MaxLongImageItems && DecodeImage(image.Uri) is { } bitmap)
                {
                    bitmaps.Add(bitmap);
                    items.Add(new ForwardLongImageItem { Image = bitmap });
                    images++;
                }
            }

            if (images == 0) return null;
            var totalImages = package.Nodes.SelectMany(node => node.Segments).Count(segment => segment is ImageSegment);
            var videos = package.Nodes.SelectMany(node => node.Segments).Count(segment => segment is VideoSegment);
            var subtitle = new List<string> { $"共 {totalImages} 张图片" };
            if (totalImages > images) subtitle.Add($"仅展示前 {images} 张");
            if (videos > 0) subtitle.Add(package.ReplyOnDemand ? $"含 {videos} 个视频，回复「{ReplyKeyword}」获取" : $"另有 {videos} 个视频");
            var model = new ForwardLongImageViewModel
            {
                Platform = package.Platform,
                Title = package.Title,
                Subtitle = string.Join(" · ", subtitle),
                Items = items,
            };
            return await host.RenderCardUriAsync<ForwardLongImage>(model, package.ProviderId, package.MediaId + "_forward", cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            foreach (var bitmap in bitmaps) bitmap.Dispose();
        }
    }

    private Bitmap? DecodeImage(string uri)
    {
        try
        {
            if (uri.StartsWith("base64://", StringComparison.OrdinalIgnoreCase)) return host.DecodeBase64ImageForRender(uri);
            if (Uri.TryCreate(uri, UriKind.Absolute, out var parsed) && parsed.IsFile) return host.DecodeImageFileForRender(parsed.LocalPath);
            return File.Exists(uri) ? host.DecodeImageFileForRender(uri) : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            BotLog.Warning($"MyParser 聊天记录长图解码图片失败: error={ex.Message}");
            return null;
        }
    }

    /// <summary>回复锚点消息「原图」后，逐条发出全部节点（最多 30 条）。</summary>
    private void RegisterOnReply(MessageEvent message, string anchorMessageId, ForwardPackage package)
    {
        if (messages is null) return;
        replies.Register(messages, message, anchorMessageId, ReplyLifetime, reply => SendEachAsync(reply, package),
            text => text.Trim() == ReplyKeyword);
    }

    /// <summary>逐条发出全部节点（最多 30 条），单条失败不影响后面。</summary>
    private async Task SendEachAsync(MessageEvent message, ForwardPackage package)
    {
        foreach (var node in package.Nodes.Take(MaxReplyNodes))
        {
            try { await host.SendSegmentsAsync(message, node.Segments).ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                BotLog.Warning($"MyParser 聊天记录逐条发送失败: media_id={package.MediaId}, error={ex.Message}");
            }
        }
        if (package.Nodes.Count > MaxReplyNodes)
            await host.SendSegmentsAsync(message, [new TextSegment($"共 {package.Nodes.Count} 项，只发送了前 {MaxReplyNodes} 项。")]).ConfigureAwait(false);
    }

    /// <summary>
    /// 登记「回复锚点消息 原图」：回复时才准备内容，并按前三级（合并转发 → 单条多媒体 → 长图，最后逐条）发出。
    /// 用于评论区图片：评论卡片照常发，原图只在有人要时才下载和发送。
    /// </summary>
    public void RegisterReplyFor(MessageEvent message, string anchorMessageId,
        Func<MessageEvent, CancellationToken, Task<ForwardPackage?>> prepare)
    {
        if (messages is null) return;
        replies.Register(messages, message, anchorMessageId, ReplyLifetime, async reply =>
        {
            try
            {
                var package = await prepare(reply, CancellationToken.None).ConfigureAwait(false);
                if (package is not null) await SendAsync(reply, package with { ReplyOnDemand = false }, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                BotLog.Warning($"MyParser 回复获取原图失败: anchor={anchorMessageId}, error={ex.Message}");
            }
        }, text => text.Trim() == ReplyKeyword);
    }

    private static bool HasVideo(ForwardPackage package) =>
        package.Nodes.SelectMany(node => node.Segments).Any(segment => segment is VideoSegment);

    private static string PreviewLine(OutgoingForwardedMessage node)
    {
        var text = node.Segments.OfType<TextSegment>().Select(segment => segment.Text.Trim()).FirstOrDefault(value => value.Length > 0);
        if (text is not null) return TextPreviewFormatter.TrimLine(text, 48);
        return node.Segments.Any(segment => segment is VideoSegment) ? "[视频]" : "[图片]";
    }

    private static ForwardDeliveryLevel Log(ForwardPackage package, ForwardDeliveryLevel level)
    {
        global::Shirobot.Plugin.MyParser.DiagnosticLog.Info($"MyParser 聊天记录投递: media_id={package.MediaId}, nodes={package.Nodes.Count}, level={level}");
        return level;
    }
}
