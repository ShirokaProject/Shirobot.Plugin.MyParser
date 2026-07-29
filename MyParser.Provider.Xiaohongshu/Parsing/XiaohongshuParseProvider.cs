using Shirobot.Plugin.MyParser.Parsing;
using MyParser.Provider.Xiaohongshu.Models;
using MyParser.Provider.Xiaohongshu.Utilities;
using ShiroBot.SDK.Models;

namespace MyParser.Provider.Xiaohongshu.Parsing;

public sealed class XiaohongshuParseProvider(XiaohongshuParser parser) : IIncomingMessageParseProvider, IParseProviderWithParser, IProviderLoginStatusProvider, IQrLoginProvider, IProviderPriority, IDisposable
{
    public string Id => "xiaohongshu";
    public string Name => "小红书";
    public int Priority => 0;
    public XiaohongshuParser Parser { get; } = parser;
    public object ParserObject => Parser;

    public bool CanHandle(string text) => XiaohongshuParser.ContainsXiaohongshuUrl(text);

    public string? ExtractParseText(IncomingMessage message)
    {
        return XiaohongshuLightAppUrlExtractor.ExtractParseText(message);
    }

    public async Task<ProviderLoginStatus> CheckLoginStatusAsync(CancellationToken cancellationToken = default)
    {
        var status = await Parser.CheckLoginStatusAsync(cancellationToken);
        return new ProviderLoginStatus(status.IsLogin, status.UserName, status.UserId, status.Message, status.NeedVerify);
    }

    public async Task<QrLoginSession> GenerateQrLoginSessionAsync(CancellationToken cancellationToken = default)
    {
        var session = await Parser.GenerateQrLoginSessionAsync(cancellationToken);
        return new QrLoginSession(session.QrId, session.Url, session);
    }

    public async Task<QrLoginPollResult> PollQrLoginAsync(QrLoginSession session, CancellationToken cancellationToken = default)
    {
        var source = session.State as XiaohongshuQrLoginSession
                     ?? new XiaohongshuQrLoginSession(session.Id, string.Empty, session.Url, string.Empty, DateTimeOffset.UtcNow);
        var poll = await Parser.PollQrLoginAsync(source, cancellationToken);
        var next = source with { Cookie = poll.Cookie };
        return new QrLoginPollResult(
            poll.CodeStatus,
            poll.Message,
            poll.IsLogin,
            NeedVerify: poll.NeedVerify,
            UserName: poll.UserName,
            State: next);
    }

    public async Task<MediaParseResult> ParseAsync(string text, CancellationToken cancellationToken = default)
    {
        var result = await Parser.ParseAsync(text, cancellationToken);
        return new MediaParseResult
        {
            ProviderId = Id,
            ProviderName = Name,
            MediaId = result.NoteId,
            SourceUrl = result.SourceUrl,
            Title = result.Title,
            AuthorName = result.AuthorName,
            AuthorId = result.AuthorId,
            CoverUrl = result.CoverUrl,
            Tags = result.Tags,
            IsGallery = result.IsGallery,
            IsVideo = result.IsVideo,
            ProviderPayload = result,
        };
    }

    public void Dispose()
    {
        Parser.Dispose();
    }
}
