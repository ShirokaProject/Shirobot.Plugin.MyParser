using System.Collections.Concurrent;
using System.Text.Json;

namespace MyParser.Provider.Douyin.Infrastructure;

/// <summary>Process-local visitor cookies. Cookies are kept explicitly because injected HttpClients may hide their CookieContainer.</summary>
public sealed class DouyinGuestSession
{
    private readonly ConcurrentDictionary<string, string> _cookies = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _registrationLock = new();
    private bool _registered;

    public DouyinGuestSession(string? configuredCookie)
    {
        CaptureCookieHeader(configuredCookie);
    }

    public string? Get(string name) => _cookies.TryGetValue(name, out var value) ? value : null;

    public void Set(string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(value))
        {
            _cookies[name] = value;
        }
    }

    public string BuildCookieHeader()
        => string.Join("; ", _cookies.Select(pair => pair.Key + "=" + pair.Value));

    public void Capture(HttpResponseMessage response)
    {
        if (response.Headers.TryGetValues("Set-Cookie", out var values))
        {
            foreach (var value in values) CaptureSetCookie(value);
        }
    }

    public void CaptureHtml(string html)
    {
        foreach (var name in new[] { "ttwid", "s_v_web_id", "verifyFp", "webid", "user_unique_id" })
        {
            var match = System.Text.RegularExpressions.Regex.Match(html, $"[\\\"']?{name}[\\\"']?\\s*[:=]\\s*[\\\"'](?<value>[^\\\"'&\\s]+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (match.Success) _cookies[name] = Uri.UnescapeDataString(match.Groups["value"].Value);
        }
    }

    public async Task EnsureRegisteredAsync(HttpClient http, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(Get("ttwid")) || _registered) return;
        lock (_registrationLock)
        {
            if (_registered) return;
            _registered = true;
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "https://ttwid.bytedance.com/ttwid/union/register/");
            request.Headers.TryAddWithoutValidation("User-Agent", DouyinConstants.UserAgent);
            request.Headers.TryAddWithoutValidation("Origin", "https://www.douyin.com");
            request.Content = new StringContent(JsonSerializer.Serialize(new { aid = 6383, service = "www.douyin.com", region = "cn", needFid = false }), System.Text.Encoding.UTF8, "application/json");
            using var response = await http.SendAsync(request, cancellationToken);
            Capture(response);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException)
        {
            // Visitor registration is best effort; share-page extraction does not depend on it.
            ShiroBot.SDK.Abstractions.BotLog.Warning($"MyParser 抖音游客 ttwid 注册失败: {ex.GetType().Name}");
        }
    }

    private void CaptureCookieHeader(string? header)
    {
        if (string.IsNullOrWhiteSpace(header)) return;
        foreach (var item in header.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) CaptureSetCookie(item);
    }

    private void CaptureSetCookie(string header)
    {
        var first = header.Split(';', 2)[0];
        var index = first.IndexOf('=');
        if (index > 0) _cookies[first[..index].Trim()] = first[(index + 1)..].Trim();
    }
}
