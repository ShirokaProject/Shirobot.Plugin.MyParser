using Shirobot.Plugin.MyParser.Parsing;
using MyParser.Provider.BiliBili.Models;
using MyParser.Provider.BiliBili.Utilities;
using ShiroBot.Model.Common;

namespace MyParser.Provider.BiliBili.Parsing;

public sealed class BilibiliParseProvider(BilibiliParser parser) : IIncomingMessageParseProvider, IProviderParseTextMatcher, IParseProviderWithParser, IProviderLoginStatusProvider, IQrLoginProvider, IProviderPriority, IDisposable
{
    public BilibiliParser Parser { get; } = parser;
    public object ParserObject => Parser;

    public string Id => "bilibili";
    public string Name => "Bilibili";
    public int Priority => 50;

    public bool CanHandle(string text)
    {
        return BilibiliUrlParser.ExtractBvid(text) is not null
               || BilibiliUrlParser.ExtractAid(text) is not null
               || BilibiliUrlParser.ExtractB23Url(text) is not null;
    }

    public string? TryNormalizeParseText(string text, ProviderParseTextContext context)
    {
        if (context.IsUrlLike)
        {
            return BilibiliUrlParser.ExtractStrictBilibiliUrl(text) ?? BilibiliUrlParser.ExtractB23Url(text);
        }

        return context.IsAutoParse
            ? BilibiliUrlParser.NormalizeStandaloneBvid(text)
            : BilibiliUrlParser.NormalizeStandaloneVideoId(text);
    }

    public string? ExtractParseText(IncomingMessage message)
    {
        var text = BilibiliLightAppUrlExtractor.ExtractParseText(message);
        return BilibiliUrlParser.ExtractStrictBilibiliUrl(text ?? string.Empty);
    }

    public async Task<ProviderLoginStatus> CheckLoginStatusAsync(CancellationToken cancellationToken = default)
    {
        var status = await Parser.CheckLoginStatusAsync(cancellationToken);
        return new ProviderLoginStatus(status.IsLogin, status.UserName, status.Mid <= 0 ? null : status.Mid.ToString(), status.Message);
    }

    public async Task<QrLoginSession> GenerateQrLoginSessionAsync(CancellationToken cancellationToken = default)
    {
        var session = await Parser.GenerateQrLoginSessionAsync(cancellationToken);
        return new QrLoginSession(session.QrcodeKey, session.Url, session);
    }

    public async Task<QrLoginPollResult> PollQrLoginAsync(QrLoginSession session, CancellationToken cancellationToken = default)
    {
        var poll = await Parser.PollQrLoginAsync(session.Id, cancellationToken);
        return new QrLoginPollResult(
            poll.Code,
            poll.Message,
            poll.IsLogin,
            IsExpired: poll.Code == 86038,
            IsWaitingConfirmation: poll.Code == 86090,
            UserName: poll.UserName);
    }

    public async Task<MediaParseResult> ParseAsync(string text, CancellationToken cancellationToken = default)
    {
        var result = await Parser.ParseMediaAsync(text, cancellationToken);
        return result switch
        {
            BilibiliMultiPageParseResult multi => new MediaParseResult
            {
                ProviderId = Id,
                ProviderName = Name,
                MediaId = multi.Bvid,
                SourceUrl = multi.SourceUrl,
                Title = multi.Title,
                AuthorName = multi.AuthorName,
                AuthorId = multi.AuthorId,
                CoverUrl = multi.CoverUrl,
                MusicUrl = null,
                Tags = [],
                IsGallery = true,
                IsVideo = false,
                ProviderPayload = multi,
            },
            BilibiliParseResult video => new MediaParseResult
            {
                ProviderId = Id,
                ProviderName = Name,
                MediaId = video.Bvid,
                SourceUrl = video.SourceUrl,
                Title = video.Title,
                AuthorName = video.AuthorName,
                AuthorId = video.AuthorId,
                CoverUrl = video.CoverUrl,
                MusicUrl = null,
                Tags = [],
                IsGallery = false,
                IsVideo = video.IsVideo,
                ProviderPayload = video,
            },
            _ => throw new BilibiliParseException("Bilibili 视频解析返回了未知结果类型。"),
        };
    }

    public void Dispose() => Parser.Dispose();
}
