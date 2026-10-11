using MyParser.Provider.Douyin.Models;
using System.Text.Json;

namespace MyParser.Provider.Douyin.Abstractions;

public interface IDouyinWorkParser
{
    bool CanParse(JsonElement aweme);
    DouyinParseResult Parse(JsonElement aweme, string fallbackAwemeId, string sourceUrl);
}
