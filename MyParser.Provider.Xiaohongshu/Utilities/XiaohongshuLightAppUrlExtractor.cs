using System.Text.Json;
using MyParser.Provider.Xiaohongshu.Parsing;
using ShiroBot.SDK.Models;
using ShiroBot.SDK.Abstractions;
using ShiroBot.SDK.Plugin;
using ShiroBot.Model.QQ;

namespace MyParser.Provider.Xiaohongshu.Utilities;

internal static partial class XiaohongshuLightAppUrlExtractor
{
    public static string? ExtractParseText(IncomingMessage message)
    {
        var parts = new List<string>();
        parts.AddRange(GetTextSegments(message));
        foreach (var app in GetLightAppSegments(message))
        {
            var urls = ExtractXiaohongshuUrls(app).ToArray();
            if (urls.Length == 0) continue;
            LogLightAppSummary(app);
            parts.AddRange(urls);
        }

        return parts.Count == 0 ? null : string.Join(' ', parts.Where(i => !string.IsNullOrWhiteSpace(i)));
    }

    private static IEnumerable<string> ExtractXiaohongshuUrls(QIncomingLightApp app)
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
            if (XiaohongshuParser.ContainsXiaohongshuUrl(url))
            {
                yield return url;
            }
        }
    }

    private static void LogLightAppSummary(QIncomingLightApp app)
    {
        try
        {
            var payload = app.JsonPayload ?? string.Empty;
            var urls = ExtractUrls(payload).Take(20).ToArray();
            if (string.IsNullOrWhiteSpace(payload))
            {
                BotLog.Info("MyParser Xiaohongshu 轻应用: empty payload");
                return;
            }

            using var json = JsonDocument.Parse(payload);
            var root = json.RootElement;
            var rootKeys = root.ValueKind == JsonValueKind.Object
                ? root.EnumerateObject().Select(i => i.Name).Take(30).ToArray()
                : [];
            var interesting = new List<string>();
            CollectInterestingFields(root, null, interesting, 40);
            BotLog.Info(
                "MyParser Xiaohongshu 轻应用信息: "
                + $"payload_bytes={payload.Length}, "
                + $"root_keys=[{string.Join(',', rootKeys)}], "
                + $"fields=[{string.Join("; ", interesting.Select(TrimLogValue))}], "
                + $"urls=[{string.Join("; ", urls.Select(TrimLogValue))}]");
        }
        catch (Exception ex)
        {
            BotLog.Info($"MyParser Xiaohongshu 轻应用信息: payload_bytes={app.JsonPayload?.Length ?? 0}, parse_error={ex.GetType().Name}: {ex.Message}, urls=[{string.Join("; ", ExtractUrls(app.JsonPayload ?? string.Empty).Take(20).Select(TrimLogValue))}]");
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

    private static void CollectInterestingFields(JsonElement element, string? propertyName, List<string> output, int limit)
    {
        if (output.Count >= limit)
        {
            return;
        }

        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    CollectInterestingFields(property.Value, property.Name, output, limit);
                    if (output.Count >= limit) break;
                }
                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray().Take(10))
                {
                    CollectInterestingFields(item, propertyName, output, limit);
                    if (output.Count >= limit) break;
                }
                break;
            case JsonValueKind.String:
                var value = element.GetString();
                if (!string.IsNullOrWhiteSpace(value) && IsInterestingField(propertyName))
                {
                    output.Add($"{propertyName}={value}");
                }
                break;
            case JsonValueKind.Number:
            case JsonValueKind.True:
            case JsonValueKind.False:
                if (IsInterestingField(propertyName))
                {
                    output.Add($"{propertyName}={element}");
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
            "url" => true,
            _ => false,
        };
    }

    private static bool IsInterestingField(string? propertyName)
    {
        if (string.IsNullOrWhiteSpace(propertyName)) return false;
        return propertyName.Contains("app", StringComparison.OrdinalIgnoreCase)
               || propertyName.Contains("appid", StringComparison.OrdinalIgnoreCase)
               || propertyName.Contains("title", StringComparison.OrdinalIgnoreCase)
               || propertyName.Contains("desc", StringComparison.OrdinalIgnoreCase)
               || propertyName.Contains("name", StringComparison.OrdinalIgnoreCase)
               || propertyName.Contains("url", StringComparison.OrdinalIgnoreCase)
               || propertyName.Contains("jump", StringComparison.OrdinalIgnoreCase)
               || propertyName.Contains("doc", StringComparison.OrdinalIgnoreCase)
               || propertyName.Contains("icon", StringComparison.OrdinalIgnoreCase)
               || propertyName.Contains("preview", StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> ExtractUrls(string value)
    {
        value = value.Replace("\\/", "/", StringComparison.Ordinal);
        foreach (var url in XiaohongshuUrlMatcher.ExtractHttpUrls(value))
        {
            yield return url.TrimEnd('\\', '"', '\'', ',', '，', ')', '）', ']', '】');
        }
    }

    private static string TrimLogValue(string value)
    {
        value = value.ReplaceLineEndings(" ").Trim();
        return value.Length <= 180 ? value : value[..180] + "...";
    }

    private static IEnumerable<string> GetTextSegments(IncomingMessage message) =>
        message.Segments.OfType<TextSegment>().Select(i => i.Text);

    private static IEnumerable<QIncomingLightApp> GetLightAppSegments(IncomingMessage message) =>
        message.Raw is QIncomingMessage qqMessage
            ? qqMessage.Segments.OfType<QIncomingLightApp>()
            : message.Segments.OfType<RawSegment>().Select(segment => segment.Payload).OfType<QIncomingLightApp>();

}
