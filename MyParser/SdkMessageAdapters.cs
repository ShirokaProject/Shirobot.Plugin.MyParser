using ShiroBot.SDK.Models;
using ShiroBot.Model.QQ;

namespace Shirobot.Plugin.MyParser.Sdk
{

public sealed record OutgoingForwardedMessage(
    long SenderId,
    string SenderName,
    IReadOnlyList<MessageSegment> Segments);

public sealed record ForwardOutgoingSegment(
    IReadOnlyList<OutgoingForwardedMessage> Messages,
    string? Title = null,
    IReadOnlyList<string>? Preview = null,
    string? Summary = null,
    string? Prompt = null);

internal static class ForwardMessageMapper
{
    public static RawSegment ToRawSegment(ForwardOutgoingSegment forward)
    {
        var messages = forward.Messages.Select(message => new QForwardedMessage(
            message.SenderId,
            message.SenderName,
            message.Segments.Select(ToQqOutgoing).ToArray())).ToArray();
        var qqForward = new QOutgoingForward(messages)
        {
            Title = forward.Title,
            Preview = forward.Preview,
            Summary = forward.Summary,
            Prompt = forward.Prompt,
        };
        return new RawSegment("qq", "forward", qqForward);
    }

    private static QOutgoingSegment ToQqOutgoing(MessageSegment segment) => segment switch
    {
        TextSegment text => new QOutgoingText(text.Text),
        MentionSegment mention when long.TryParse(mention.UserId, out var userId) => new QOutgoingMention(userId),
        MentionAllSegment => new QOutgoingMentionAll(),
        QuoteSegment quote when long.TryParse(quote.MessageId, out var messageId) => new QOutgoingReply(messageId),
        EmojiSegment emoji => new QOutgoingFace(emoji.Id),
        ImageSegment image => new QOutgoingImage(image.Uri) { Summary = image.Summary },
        AudioSegment audio => new QOutgoingRecord(audio.Uri),
        VideoSegment video => new QOutgoingVideo(video.Uri) { ThumbUri = video.ThumbnailUri },
        RawSegment { Payload: QOutgoingSegment outgoing } => outgoing,
        _ => throw new NotSupportedException($"合并转发节点不支持 {segment.GetType().Name}。"),
    };
}

public static class MessageEventQqExtensions
{
    public static QIncomingReply? GetQqReply(this MessageEvent message) =>
        message.Raw is QIncomingMessage qqMessage
            ? qqMessage.Segments.OfType<QIncomingReply>().FirstOrDefault()
            : message.Segments.OfType<RawSegment>()
                .Select(segment => segment.Payload)
                .OfType<QIncomingReply>()
                .FirstOrDefault();

    public static string GetPlainText(this QIncomingReply reply) =>
        string.Concat(reply.Segments.OfType<QIncomingText>().Select(segment => segment.Text));
}
}

namespace ShiroBot.SDK.Plugin
{

public static class MyParserMessageContextExtensions
{
    public static Task<SentMessage> ReplyAsync(
        this IMessageContext context,
        MessageEvent message,
        Shirobot.Plugin.MyParser.Sdk.ForwardOutgoingSegment forward)
    {
        if (string.Equals(message.Platform, "qq", StringComparison.OrdinalIgnoreCase))
        {
            return context.SendMessageAsync(
                message.Channel,
                [Shirobot.Plugin.MyParser.Sdk.ForwardMessageMapper.ToRawSegment(forward)]);
        }

        // Platforms without QQ's native forward format receive the same nodes as ordinary messages.
        var segments = new List<MessageSegment>();
        if (!string.IsNullOrWhiteSpace(forward.Title))
        {
            segments.Add(new TextSegment(forward.Title + "\n"));
        }

        foreach (var node in forward.Messages)
        {
            if (!string.IsNullOrWhiteSpace(node.SenderName))
            {
                segments.Add(new TextSegment($"[{node.SenderName}] "));
            }

            segments.AddRange(node.Segments);
            segments.Add(new TextSegment("\n"));
        }

        return context.SendMessageAsync(message.Channel, segments);
    }
}
}
