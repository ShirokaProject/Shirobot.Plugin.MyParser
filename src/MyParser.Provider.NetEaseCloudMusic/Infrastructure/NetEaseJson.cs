using System.Text.Json;

namespace MyParser.Provider.NetEaseCloudMusic.Infrastructure;

internal static class NetEaseJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };
}
