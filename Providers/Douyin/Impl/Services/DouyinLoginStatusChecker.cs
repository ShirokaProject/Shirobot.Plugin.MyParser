using System.Text.Json;
using static Shirobot.Plugin.MyParser.Providers.Douyin.Infrastructure.DouyinRequestHeaders;
using static Shirobot.Plugin.MyParser.Providers.Douyin.Utilities.DouyinParseHelpers;

namespace Shirobot.Plugin.MyParser.Providers.Douyin.Impl.Services;

internal sealed class DouyinLoginStatusChecker(HttpClient http)
{
    public async Task<string> CheckLoginStatusAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(MyParserRuntime.DouyinCookie))
        {
            return "未配置 Cookie";
        }

        if (!MyParserRuntime.DouyinCookie.Contains("sessionid=", StringComparison.OrdinalIgnoreCase)
            || !MyParserRuntime.DouyinCookie.Contains("ttwid=", StringComparison.OrdinalIgnoreCase))
        {
            return "Cookie 格式可能无效：缺少 sessionid 或 ttwid";
        }

        var url = "https://www.douyin.com/webcast/user/me/?aid=1128&t=" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        ApplyDefaultHeaders(request, "https://www.douyin.com/");
        request.Headers.TryAddWithoutValidation("Cookie", MyParserRuntime.DouyinCookie);

        using var response = await http.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return $"HTTP {(int)response.StatusCode}; cookie_length={MyParserRuntime.DouyinCookie.Length}";
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var statusCode = GetInt(root, "status_code");
            var message = GetString(root, "message") ?? GetString(root, "status_msg") ?? string.Empty;
            var hasUser = TryGetProperty(root, "data", out var data) && data.ValueKind is JsonValueKind.Object or JsonValueKind.Array;
            return statusCode == 0
                ? $"有效/已登录; status_code=0; has_data={hasUser}; cookie_length={MyParserRuntime.DouyinCookie.Length}"
                : $"可能失效/未登录; status_code={statusCode}; message={message}; cookie_length={MyParserRuntime.DouyinCookie.Length}";
        }
        catch (JsonException)
        {
            var sample = body.Length > 120 ? body[..120] : body;
            return $"返回非 JSON; cookie_length={MyParserRuntime.DouyinCookie.Length}; sample={sample.ReplaceLineEndings(" ")}";
        }
    }
}
