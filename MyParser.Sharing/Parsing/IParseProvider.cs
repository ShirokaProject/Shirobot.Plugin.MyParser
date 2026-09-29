namespace Shirobot.Plugin.MyParser.Parsing;

public interface IParseProvider
{
    string Id { get; }
    string Name { get; }
    bool CanHandle(string text);
    Task<ParsedMedia> ParseAsync(string text, CancellationToken cancellationToken = default);
}
