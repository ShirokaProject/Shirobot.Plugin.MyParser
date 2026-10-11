using ShiroBot.SDK.Models;

namespace Shirobot.Plugin.MyParser.Parsing;

public interface IIncomingMessageParseProvider : IParseProvider
{
    string? ExtractParseText(MessageEvent message);
}
