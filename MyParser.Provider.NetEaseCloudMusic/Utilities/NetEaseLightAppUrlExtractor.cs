using System.Text.Json;
using System.Text.RegularExpressions;
using MyParser.Provider.NetEaseCloudMusic.Parsing;
using ShiroBot.Qq.Model;
using ShiroBot.SDK.Abstractions;
using ShiroBot.SDK.Models;
using ShiroBot.SDK.Plugin;

namespace MyParser.Provider.NetEaseCloudMusic.Utilities;

internal static partial class NetEaseLightAppUrlExtractor
{
    private const string MusicApp = "com.tencent.music.lua";
    private const string MusicAppId = "100495085";

    public static string? ExtractParseText(IncomingMessage message)
    {
        foreach (var app in GetLightAppSegments(message))
        {
            var candidate = ExtractParseText(app);
            if (!string.IsNullOrWhiteSpace(candidate))
            {
                BotLog.Info($"MyParser 网易云轻应用命中: source=qq-light-app, normalized={candidate}");
                return candidate;
            }
        }

        return null;
    }

    private static string? ExtractParseText(QqLightAppIncoming app)
    {
        if (string.IsNullOrWhiteSpace(app.JsonPayload)) return null;

        var preferred = new List<string>();
        var candidates = new List<string>();
        try
        {
            using var document = JsonDocument.Parse(app.JsonPayload);
            var isMusicApp = IsMusicApp(document.RootElement);
            CollectUrls(document.RootElement, null, preferred, candidates);
            return NormalizeFirstSongUrl(isMusicApp && preferred.Count > 0 ? preferred : candidates);
        }
        catch (JsonException)
        {
            return NormalizeFirstSongUrl(ExtractHttpUrls(app.JsonPayload));
        }
    }

    private static bool IsMusicApp(JsonElement root)
    {
        var app = FindStringProperty(root, "app");
        var appId = FindStringProperty(root, "appid");
        return string.Equals(app, MusicApp, StringComparison.OrdinalIgnoreCase)
               || string.Equals(appId, MusicAppId, StringComparison.OrdinalIgnoreCase);
    }

    private static void CollectUrls(JsonElement element, string? propertyName, ICollection<string> preferred, ICollection<string> candidates)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject()) CollectUrls(property.Value, property.Name, preferred, candidates);
                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray()) CollectUrls(item, propertyName, preferred, candidates);
                break;
            case JsonValueKind.String:
                var value = element.GetString();
                if (string.IsNullOrWhiteSpace(value)) break;
                var urls = ExtractHttpUrls(value).ToArray();
                foreach (var url in urls) candidates.Add(url);
                if (IsPreferredField(propertyName)) foreach (var url in urls) preferred.Add(url);

                var nested = value.TrimStart();
                if (nested.StartsWith('{') || nested.StartsWith('['))
                {
                    try
                    {
                        using var nestedDocument = JsonDocument.Parse(nested);
                        CollectUrls(nestedDocument.RootElement, propertyName, preferred, candidates);
                    }
                    catch (JsonException)
                    {
                    }
                }

                break;
        }
    }

    private static string? NormalizeFirstSongUrl(IEnumerable<string> urls)
    {
        foreach (var url in urls.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (NetEaseUrlParser.ContainsNetEaseSongUrl(url)) return NetEaseUrlParser.NormalizeParseText(url);
        }

        return null;
    }

    private static bool IsPreferredField(string? propertyName) =>
        propertyName is not null
        && (propertyName.Equals("musicUrl", StringComparison.OrdinalIgnoreCase)
            || propertyName.Equals("music_url", StringComparison.OrdinalIgnoreCase)
            || propertyName.Equals("jumpUrl", StringComparison.OrdinalIgnoreCase)
            || propertyName.Equals("jump_url", StringComparison.OrdinalIgnoreCase)
            || propertyName.Equals("qqdocurl", StringComparison.OrdinalIgnoreCase)
            || propertyName.Equals("docurl", StringComparison.OrdinalIgnoreCase));

    private static string? FindStringProperty(JsonElement element, string propertyName)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (property.Name.Equals(propertyName, StringComparison.OrdinalIgnoreCase)
                    && property.Value.ValueKind is JsonValueKind.String or JsonValueKind.Number)
                {
                    return property.Value.ToString();
                }

                var nested = FindStringProperty(property.Value, propertyName);
                if (!string.IsNullOrWhiteSpace(nested)) return nested;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                var nested = FindStringProperty(item, propertyName);
                if (!string.IsNullOrWhiteSpace(nested)) return nested;
            }
        }

        return null;
    }

    private static IEnumerable<string> ExtractHttpUrls(string value)
    {
        value = value.Replace("\\/", "/", StringComparison.Ordinal);
        foreach (Match match in HttpUrlRegex().Matches(value))
        {
            yield return match.Value.TrimEnd('\\', '"', '\'', ',', '，', ')', '）', ']', '】');
        }
    }

    private static IEnumerable<QqLightAppIncoming> GetLightAppSegments(IncomingMessage message) =>
        message.Raw is QqIncomingMessage qqMessage
            ? qqMessage.Segments.OfType<QqLightAppIncoming>()
            : message.Segments.OfType<RawSegment>().Select(segment => segment.Payload).OfType<QqLightAppIncoming>();

    [GeneratedRegex(@"https?://[^\s<>""']+", RegexOptions.IgnoreCase)]
    private static partial Regex HttpUrlRegex();
}
