using ShiroBot.SDK.Models;

namespace Shirobot.Plugin.MyParser.Parsing;

internal interface IIncomingMessageParseProvider : IParseProvider
{
    string? ExtractParseText(MessageEvent message);
}
