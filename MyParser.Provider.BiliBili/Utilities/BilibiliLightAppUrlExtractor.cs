using System.Text.Json;
using ShiroBot.SDK.Models;
using ShiroBot.Qq.Model;

namespace MyParser.Provider.BiliBili.Utilities;

internal static partial class BilibiliLightAppUrlExtractor
{
    public static string? ExtractParseText(IncomingMessage message)
    {
        var parts = new List<string>();
        parts.AddRange(GetTextSegments(message));
        foreach (var app in GetLightAppSegments(message))
        {
            parts.AddRange(ExtractBilibiliUrls(app));
        }

        return parts.Count == 0 ? null : string.Join(' ', parts.Where(i => !string.IsNullOrWhiteSpace(i)));
    }

    private static IEnumerable<string> ExtractBilibiliUrls(QqLightAppIncoming app)
    {
        if (string.IsNullOrWhiteSpace(app.JsonPayload))
        {
            yield break;
        }

        var preferred = new List<string>();
        var all = new List<string>();
        try
        {
            using var json = JsonDocument.Parse(app.JsonPayload);
            CollectUrls(json.RootElement, null, preferred, all);
        }
        catch (JsonException)
        {
            all.AddRange(ExtractUrls(app.JsonPayload));
        }

        var source = preferred.Count > 0 ? preferred : all;
        foreach (var url in source.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (BilibiliUrlParser.ContainsBilibiliUrl(url))
            {
                yield return url;
            }
        }
    }

    private static void CollectUrls(JsonElement element, string? propertyName, List<string> preferred, List<string> all)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    CollectUrls(property.Value, property.Name, preferred, all);
                }
                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    CollectUrls(item, propertyName, preferred, all);
                }
                break;
            case JsonValueKind.String:
                var value = element.GetString();
                if (string.IsNullOrWhiteSpace(value))
                {
                    break;
                }

                var urls = ExtractUrls(value).ToArray();
                all.AddRange(urls);
                if (IsPreferredUrlField(propertyName))
                {
                    preferred.AddRange(urls);
                }
                break;
        }
    }

    private static bool IsPreferredUrlField(string? propertyName)
    {
        return propertyName is not null && propertyName switch
        {
            "qqdocurl" => true,
            "docurl" => true,
            "jumpUrl" => true,
            "jump_url" => true,
            "webpageUrl" => true,
            "webpage_url" => true,
            "pageUrl" => true,
            "page_url" => true,
            "shareUrl" => true,
            "share_url" => true,
            "targetUrl" => true,
            "target_url" => true,
            _ => false,
        };
    }

    private static IEnumerable<string> ExtractUrls(string value)
    {
        value = value.Replace("\\/", "/", StringComparison.Ordinal);
        foreach (var url in BilibiliUrlMatcher.ExtractHttpUrls(value))
        {
            yield return url;
        }
    }

    private static IEnumerable<string> GetTextSegments(IncomingMessage message) =>
        message.Segments.OfType<TextSegment>().Select(i => i.Text);

    private static IEnumerable<QqLightAppIncoming> GetLightAppSegments(IncomingMessage message) =>
        message.Raw is QqIncomingMessage qqMessage
            ? qqMessage.Segments.OfType<QqLightAppIncoming>()
            : message.Segments.OfType<RawSegment>().Select(segment => segment.Payload).OfType<QqLightAppIncoming>();
}
