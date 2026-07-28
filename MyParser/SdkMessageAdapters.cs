using ShiroBot.SDK.Models;
using ShiroBot.Qq.Model;

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
        var messages = forward.Messages.Select(message => new QqForwardedMessage(
            message.SenderId,
            message.SenderName,
            message.Segments.Select(ToQqOutgoing).ToArray())).ToArray();
        var qqForward = new QqForwardOutgoing(messages)
        {
            Title = forward.Title,
            Preview = forward.Preview,
            Summary = forward.Summary,
            Prompt = forward.Prompt,
        };
        return new RawSegment("qq", "forward", qqForward);
    }

    private static QqOutgoingSegment ToQqOutgoing(MessageSegment segment) => segment switch
    {
        TextSegment text => new QqTextOutgoing(text.Text),
        MentionSegment mention when long.TryParse(mention.UserId, out var userId) => new QqMentionOutgoing(userId),
        MentionAllSegment => new QqMentionAllOutgoing(),
        QuoteSegment quote when long.TryParse(quote.MessageId, out var messageId) => new QqReplyOutgoing(messageId),
        EmojiSegment emoji => new QqFaceOutgoing(emoji.Id),
        ImageSegment image => new QqImageOutgoing(image.Uri) { Summary = image.Summary },
        AudioSegment audio => new QqRecordOutgoing(audio.Uri),
        VideoSegment video => new QqVideoOutgoing(video.Uri) { ThumbUri = video.ThumbnailUri },
        RawSegment { Payload: QqOutgoingSegment outgoing } => outgoing,
        _ => throw new NotSupportedException($"合并转发节点不支持 {segment.GetType().Name}。"),
    };
}

public static class MessageEventQqExtensions
{
    public static QqReplyIncoming? GetQqReply(this MessageEvent message) =>
        message.Raw is QqIncomingMessage qqMessage
            ? qqMessage.Segments.OfType<QqReplyIncoming>().FirstOrDefault()
            : message.Segments.OfType<RawSegment>()
                .Select(segment => segment.Payload)
                .OfType<QqReplyIncoming>()
                .FirstOrDefault();

    public static string GetPlainText(this QqReplyIncoming reply) =>
        string.Concat(reply.Segments.OfType<QqTextIncoming>().Select(segment => segment.Text));
}
}

namespace ShiroBot.SDK.Plugin
{

public static class MyParserMessageContextExtensions
{
    public static Task<SentMessage> ReplyAsync(
        this IMessageContext context,
        MessageEvent message,
        Shirobot.Plugin.MyParser.Sdk.ForwardOutgoingSegment forward) =>
        context.SendMessageAsync(
            message.Channel,
            [Shirobot.Plugin.MyParser.Sdk.ForwardMessageMapper.ToRawSegment(forward)]);
}
}
