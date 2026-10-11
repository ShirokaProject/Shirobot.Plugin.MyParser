using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ShiroBot.SDK.Abstractions;

namespace MyParser.Provider.Douyin.Infrastructure;

/// <summary>Obtains short-lived msTokens from the public web SDK endpoint, keyed by ttwid.</summary>
public sealed class DouyinMsTokenProvider(HttpClient http)
{
    private static readonly ConcurrentDictionary<string, CacheEntry> Cache = new(StringComparer.Ordinal);
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(10);
    private const string Alphabet = "Dkdpgh4ZKsQB80/Mfvw36XI1R25+WUAlEi7NLboqYTOPuzmFjJnryx9HVGcaStCe";

    public async Task<string?> GetAsync(string? ttwid, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(ttwid)) return null;
        if (Cache.TryGetValue(ttwid, out var cached) && cached.ExpiresAt > DateTimeOffset.UtcNow) return cached.Token;

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "https://mssdk.bytedance.com/web/common?ms_appid=6383");
            request.Headers.TryAddWithoutValidation("User-Agent", DouyinConstants.UserAgent);
            request.Headers.TryAddWithoutValidation("Accept", "*/*");
            request.Headers.TryAddWithoutValidation("Accept-Language", "zh-CN,zh;q=0.9");
            request.Headers.TryAddWithoutValidation("Origin", "https://www.douyin.com");
            request.Headers.TryAddWithoutValidation("Referer", DouyinConstants.HomeUrl);
            request.Headers.TryAddWithoutValidation("Cookie", "ttwid=" + ttwid);
            request.Content = new StringContent(BuildReportBody(), Encoding.UTF8, "text/plain");
            using var response = await http.SendAsync(request, cancellationToken);
            var token = TryGetHeader(response, "x-ms-token") ?? TryGetSetCookieToken(response);
            if (!string.IsNullOrWhiteSpace(token))
            {
                Cache[ttwid] = new CacheEntry(token, DateTimeOffset.UtcNow.Add(CacheLifetime));
                return token;
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException)
        {
            BotLog.Warning($"MyParser 抖音动态 msToken 获取失败: {ex.GetType().Name}");
        }

        return null;
    }

    private static string BuildReportBody()
    {
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var fingerprint = JsonSerializer.Serialize(new
        {
            navigator = new { appName = "Netscape", platform = "Win32", vendor = "Google Inc.", deviceMemory = "8", language = DouyinConstants.BrowserLanguage, hardwareConcurrency = 12, cookieEnabled = 1, webdriver = "false" },
            screen = new { innerWidth = 1920, innerHeight = 937, outerWidth = 1920, outerHeight = 1040, availWidth = 1920, availHeight = 1040, sizeWidth = 1920, sizeHeight = 1080 },
            wID = new { timestamp = timestamp.ToString(), aid = 0, index = 1 },
            window = new { location = DouyinConstants.HomeUrl },
        });
        var nonce = RandomNumberGenerator.GetInt32(256);
        var data = Encoding.UTF8.GetBytes(fingerprint);
        var encrypted = Rc4((byte)nonce, data);
        var raw = new byte[encrypted.Length + 2]; raw[0] = 0x41; raw[1] = (byte)nonce; encrypted.CopyTo(raw, 2);
        return JsonSerializer.Serialize(new { magic = 538969122, version = 1, dataType = 8, strData = Encode(raw), tspFromClient = timestamp, ulr = 0 });
    }

    private static byte[] Rc4(byte key, byte[] data)
    {
        var state = Enumerable.Range(0, 256).Select(i => (byte)i).ToArray(); var j = 0;
        for (var i = 0; i < state.Length; i++) { j = (j + state[i] + key) & 255; (state[i], state[j]) = (state[j], state[i]); }
        var output = new byte[data.Length]; var x = 0; j = 0;
        for (var i = 0; i < data.Length; i++) { x = (x + 1) & 255; j = (j + state[x]) & 255; (state[x], state[j]) = (state[j], state[x]); output[i] = (byte)(data[i] ^ state[(state[x] + state[j]) & 255]); }
        return output;
    }

    private static string Encode(byte[] data)
    {
        var output = new StringBuilder((data.Length + 2) / 3 * 4);
        for (var i = 0; i < data.Length; i += 3)
        {
            var remaining = data.Length - i; var value = data[i] << 16 | (remaining > 1 ? data[i + 1] << 8 : 0) | (remaining > 2 ? data[i + 2] : 0);
            output.Append(Alphabet[(value >> 18) & 63]).Append(Alphabet[(value >> 12) & 63]);
            output.Append(remaining > 1 ? Alphabet[(value >> 6) & 63] : '='); output.Append(remaining > 2 ? Alphabet[value & 63] : '=');
        }
        return output.ToString();
    }

    private static string? TryGetHeader(HttpResponseMessage response, string name) => response.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;
    private static string? TryGetSetCookieToken(HttpResponseMessage response) => response.Headers.TryGetValues("Set-Cookie", out var values) ? values.SelectMany(v => v.Split(';')).Select(v => v.Trim()).FirstOrDefault(v => v.StartsWith("msToken=", StringComparison.OrdinalIgnoreCase))?[8..] : null;
    private sealed record CacheEntry(string Token, DateTimeOffset ExpiresAt);
}
