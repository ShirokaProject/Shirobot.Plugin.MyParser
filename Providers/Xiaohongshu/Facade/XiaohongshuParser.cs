using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Shirobot.Plugin.MyParser.Providers.Xiaohongshu.Impl.Services;
using Shirobot.Plugin.MyParser.Providers.Xiaohongshu.Infrastructure;
using Shirobot.Plugin.MyParser.Providers.Xiaohongshu.Models;
using Shirobot.Plugin.MyParser.Providers.Xiaohongshu.Utilities;
using ShiroBot.SDK.Abstractions;

namespace Shirobot.Plugin.MyParser.Providers.Xiaohongshu.Facade;

internal sealed partial class XiaohongshuParser : IDisposable
{
    private readonly HttpClient _http;
    private readonly bool _ownsHttpClient;
    private readonly PluginConfig _config;
    private readonly XiaohongshuSignClient _signClient;
    private readonly XiaohongshuVideoDownloader _videoDownloader;

    public XiaohongshuParser(PluginConfig config, HttpClient? httpClient = null)
    {
        _config = config;
        _ownsHttpClient = httpClient is null;
        _http = httpClient ?? XiaohongshuHttpClientFactory.Create(config);
        _signClient = new XiaohongshuSignClient(config, _http);
        _videoDownloader = new XiaohongshuVideoDownloader(config, _http);
    }

    public async Task<XiaohongshuParseResult> ParseAsync(string text, CancellationToken cancellationToken = default)
    {
        var originalUrl = XiaohongshuUrlParser.ExtractXiaohongshuUrl(text) ?? throw new XiaohongshuParseException("没有识别到小红书链接。");
        var resolvedUrl = await ResolveUrlAsync(originalUrl, cancellationToken);
        var html = await FetchHtmlAsync(resolvedUrl, cancellationToken);
        var noteId = XiaohongshuUrlParser.ExtractNoteId(resolvedUrl);
        var xsecToken = XiaohongshuUrlParser.ExtractXsecToken(resolvedUrl);
        var xsecSource = XiaohongshuUrlParser.ExtractXsecSource(resolvedUrl);
        using var state = ExtractInitialState(html);
        var note = GetNoteFromState(state.RootElement, noteId).Clone();
        var resolvedNoteId = note.GetStringOrDefault("noteId") ?? note.GetStringOrDefault("note_id") ?? note.GetStringOrDefault("id") ?? noteId;
        if (string.IsNullOrWhiteSpace(resolvedNoteId))
        {
            throw new XiaohongshuParseException("没有从页面中提取到 note_id。");
        }

        var title = note.GetStringOrDefault("title") ?? MetaContent(html, "og:title") ?? "小红书笔记";
        var description = note.GetStringOrDefault("desc") ?? note.GetStringOrDefault("description") ?? MetaContent(html, "description");
        var user = note.GetPropertyOrDefault("user") ?? note.GetPropertyOrDefault("userInfo") ?? note.GetPropertyOrDefault("user_info");
        var authorId = user?.GetStringOrDefault("userId") ?? user?.GetStringOrDefault("user_id") ?? XiaohongshuUrlParser.ExtractUserIdFromUrl(resolvedUrl) ?? FindUserIdInObject(note);
        var tags = ExtractTags(note).ToList();
        if (string.IsNullOrWhiteSpace(xsecToken) && !string.IsNullOrWhiteSpace(MyParserRuntime.XiaohongshuCookie) && MyParserRuntime.XiaohongshuCookie.Contains("a1=", StringComparison.OrdinalIgnoreCase))
        {
            var recovered = await RecoverXsecTokenAsync(resolvedNoteId, authorId, title, description, tags, cancellationToken);
            if (recovered is not null)
            {
                xsecToken = recovered.Value.Token;
                xsecSource = recovered.Value.Source;
                resolvedUrl = $"{XiaohongshuConstants.Origin}/explore/{resolvedNoteId}?xsec_token={Uri.EscapeDataString(xsecToken)}&xsec_source={Uri.EscapeDataString(xsecSource)}";
            }
        }

        var formats = ExtractVideoFormats(note, html).OrderByDescending(GetVideoScore).ToList();
        var images = ExtractImages(note, html).ToList();
        var coverUrl = images.LastOrDefault()?.Url ?? MetaContent(html, "og:image");
        var comments = new List<XiaohongshuComment>();
        if (_config.XiaohongshuFetchComments && !string.IsNullOrWhiteSpace(xsecToken) && LooksLikeLoginCookie(MyParserRuntime.XiaohongshuCookie))
        {
            try
            {
                comments = await FetchCommentsAsync(resolvedNoteId, xsecToken, xsecSource, Math.Clamp(_config.XiaohongshuCommentCount, 1, 20), cancellationToken);
            }
            catch (Exception ex)
            {
                BotLog.Warning($"MyParser 小红书评论获取失败: note_id={resolvedNoteId}, error={ex.Message}");
            }
        }

        var interact = note.GetPropertyOrDefault("interactInfo") ?? note.GetPropertyOrDefault("interact_info");
        return new XiaohongshuParseResult
        {
            NoteId = resolvedNoteId,
            SourceUrl = resolvedUrl,
            OriginalUrl = originalUrl,
            Title = title,
            Description = description,
            AuthorName = user?.GetStringOrDefault("nickname") ?? user?.GetStringOrDefault("nickName"),
            AuthorId = authorId,
            AuthorAvatarUrl = user?.GetStringOrDefault("avatar") ?? user?.GetStringOrDefault("image"),
            CoverUrl = coverUrl,
            XsecToken = xsecToken,
            XsecSource = xsecSource,
            LikeCount = interact?.GetLongLoose("likedCount", "liked_count", "likeCount", "like_count") ?? 0,
            CollectCount = interact?.GetLongLoose("collectedCount", "collected_count", "collectCount", "collect_count") ?? 0,
            CommentCount = interact?.GetLongLoose("commentCount", "comment_count", "commentsCount", "comments_count") ?? 0,
            ShareCount = interact?.GetLongLoose("shareCount", "share_count") ?? 0,
            DurationSeconds = formats.FirstOrDefault(i => i.DurationSeconds is > 0)?.DurationSeconds,
            Tags = tags,
            Images = images,
            VideoFormats = formats,
            Comments = comments,
        };
    }

    public Task<(string FileUri, string LocalPath)> DownloadVideoAsync(XiaohongshuParseResult result, CancellationToken cancellationToken = default)
    {
        return _videoDownloader.DownloadVideoAsync(result, cancellationToken);
    }

    public async Task<XiaohongshuLoginStatus> CheckLoginStatusAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(MyParserRuntime.XiaohongshuCookie))
        {
            return new XiaohongshuLoginStatus(false, null, null, "未配置 XiaohongshuCookie");
        }

        if (!MyParserRuntime.XiaohongshuCookie.Contains("web_session=", StringComparison.OrdinalIgnoreCase))
        {
            return new XiaohongshuLoginStatus(false, null, null, "Cookie 缺少 web_session");
        }

        if (!MyParserRuntime.XiaohongshuCookie.Contains("a1=", StringComparison.OrdinalIgnoreCase))
        {
            return new XiaohongshuLoginStatus(false, null, null, "Cookie 缺少 a1，无法稳定生成 xhshow 签名");
        }

        foreach (var uri in new[] { "/api/sns/web/v2/user/me", "/api/sns/web/v1/user/selfinfo" })
        {
            try
            {
                using var request = await CreateSignedRequestAsync(HttpMethod.Get, uri, MyParserRuntime.XiaohongshuCookie, null, null, XiaohongshuConstants.Origin + "/", cancellationToken);
                using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                {
                    continue;
                }

                if ((int)response.StatusCode is 461 or 471)
                {
                    return new XiaohongshuLoginStatus(false, null, null, "Cookie 请求触发小红书安全验证，请在浏览器完成验证后重新读取 Cookie。", true);
                }

                response.EnsureSuccessStatusCode();
                using var json = await response.Content.ReadFromJsonAsync<JsonDocument>(cancellationToken: cancellationToken) ?? throw new XiaohongshuParseException("空响应");
                if (json.RootElement.GetBoolLoose("success") == false || !json.RootElement.CodeOk())
                {
                    continue;
                }

                var data = json.RootElement.GetPropertyOrDefault("data") ?? json.RootElement;
                var user = data.GetPropertyOrDefault("user") ?? data.GetPropertyOrDefault("userInfo") ?? data.GetPropertyOrDefault("user_info") ?? data;
                var basic = data.GetPropertyOrDefault("basic_info") ?? data.GetPropertyOrDefault("basicInfo");
                var userId = user.GetStringOrDefault("user_id")
                             ?? user.GetStringOrDefault("userId")
                             ?? user.GetStringOrDefault("id")
                             ?? data.GetStringOrDefault("user_id")
                             ?? data.GetStringOrDefault("userId")
                             ?? data.GetStringOrDefault("id")
                             ?? basic?.GetStringOrDefault("user_id")
                             ?? basic?.GetStringOrDefault("userId")
                             ?? basic?.GetStringOrDefault("red_id");
                var nickname = user.GetStringOrDefault("nickname")
                               ?? user.GetStringOrDefault("nickName")
                               ?? user.GetStringOrDefault("nick_name")
                               ?? data.GetStringOrDefault("nickname")
                               ?? data.GetStringOrDefault("nickName")
                               ?? data.GetStringOrDefault("nick_name")
                               ?? basic?.GetStringOrDefault("nickname")
                               ?? basic?.GetStringOrDefault("nickName")
                               ?? basic?.GetStringOrDefault("nick_name");
                return new XiaohongshuLoginStatus(true, nickname, userId, string.IsNullOrWhiteSpace(nickname) ? "Cookie 有效" : $"已登录：{nickname}");
            }
            catch (Exception ex)
            {
                BotLog.Warning($"MyParser 小红书登录态检查失败: uri={uri}, error={ex.Message}");
            }
        }

        return new XiaohongshuLoginStatus(false, null, null, "Cookie 无效或检测失败");
    }

    public async Task<XiaohongshuQrLoginSession> GenerateQrLoginSessionAsync(CancellationToken cancellationToken = default)
    {
        var cookie = string.IsNullOrWhiteSpace(MyParserRuntime.XiaohongshuCookie) ? MakeGuestCookie() : EnsureGuestCookieParts(MyParserRuntime.XiaohongshuCookie);
        const string uri = "/api/sns/web/v1/login/qrcode/create";
        var payload = new Dictionary<string, object?>();
        using var request = await CreateSignedRequestAsync(HttpMethod.Post, uri, cookie, payload, null, XiaohongshuConstants.Origin + "/", cancellationToken);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        cookie = MergeSetCookie(cookie, response.Headers.TryGetValues("Set-Cookie", out var values) ? values : []);
        response.EnsureSuccessStatusCode();
        using var json = await response.Content.ReadFromJsonAsync<JsonDocument>(cancellationToken: cancellationToken) ?? throw new XiaohongshuParseException("二维码接口返回空响应。");
        if (!json.RootElement.CodeOk())
        {
            throw new XiaohongshuParseException(json.RootElement.GetStringOrDefault("msg") ?? "二维码生成失败。");
        }

        var data = json.RootElement.GetPropertyOrDefault("data") ?? throw new XiaohongshuParseException("二维码接口未返回 data。");
        var qrId = data.GetStringOrDefault("qr_id");
        var code = data.GetStringOrDefault("code");
        var url = data.GetStringOrDefault("url");
        if (string.IsNullOrWhiteSpace(qrId) || string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(url))
        {
            throw new XiaohongshuParseException("二维码接口未返回 qr_id/code/url。");
        }

        return new XiaohongshuQrLoginSession(qrId, code, url, cookie, DateTimeOffset.UtcNow);
    }

    public async Task<XiaohongshuQrPollResult> PollQrLoginAsync(XiaohongshuQrLoginSession session, CancellationToken cancellationToken = default)
    {
        const string uri = "/api/sns/web/v1/login/qrcode/status";
        var parameters = new Dictionary<string, object?> { ["qr_id"] = session.QrId, ["code"] = session.Code };
        using var request = await CreateSignedRequestAsync(HttpMethod.Get, uri, session.Cookie, null, parameters, XiaohongshuConstants.Origin + "/", cancellationToken);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var cookie = MergeSetCookie(session.Cookie, response.Headers.TryGetValues("Set-Cookie", out var values) ? values : []);
        if ((int)response.StatusCode is 461 or 471)
        {
            return new XiaohongshuQrPollResult((int)response.StatusCode, "触发小红书安全验证，请用浏览器登录后复制 Cookie。", false, true, cookie);
        }

        response.EnsureSuccessStatusCode();
        using var json = await response.Content.ReadFromJsonAsync<JsonDocument>(cancellationToken: cancellationToken) ?? throw new XiaohongshuParseException("二维码状态接口返回空响应。");
        if (!json.RootElement.CodeOk())
        {
            throw new XiaohongshuParseException(json.RootElement.GetStringOrDefault("msg") ?? "二维码状态接口异常。");
        }

        var info = json.RootElement.GetPropertyOrDefault("data") ?? json.RootElement;
        var loginInfo = info.GetPropertyOrDefault("login_info") ?? info.GetPropertyOrDefault("loginInfo");
        var sessionValue = loginInfo?.GetStringOrDefault("session");
        var secureSession = loginInfo?.GetStringOrDefault("secure_session") ?? loginInfo?.GetStringOrDefault("secureSession");
        if (!string.IsNullOrWhiteSpace(sessionValue))
        {
            var cookies = ParseCookieHeader(cookie);
            cookies["web_session"] = sessionValue;
            if (!string.IsNullOrWhiteSpace(secureSession))
            {
                cookies["secure_session"] = secureSession;
            }

            cookies.TryAdd("xsecappid", "xhs-pc-web");
            cookie = CookieDictToHeader(cookies);
            MyParserRuntime.XiaohongshuCookie = cookie;
            var status = await CheckLoginStatusAsync(cancellationToken);
            return new XiaohongshuQrPollResult(0, status.Message, status.IsLogin, status.NeedVerify, cookie, status.UserName);
        }

        var codeStatus = info.GetIntLoose("code_status", "codeStatus", "status") ?? -1;
        var message = info.GetStringOrDefault("msg") ?? info.GetStringOrDefault("message") ?? "等待扫码确认";
        return new XiaohongshuQrPollResult(codeStatus, message, false, false, cookie);
    }

    public static bool ContainsXiaohongshuUrl(string text) => XiaohongshuUrlParser.ContainsXiaohongshuUrl(text);

    public static bool LooksLikeLoginCookie(string cookie)
    {
        return cookie.Contains("web_session=", StringComparison.OrdinalIgnoreCase)
               && cookie.Contains("a1=", StringComparison.OrdinalIgnoreCase)
               && cookie.Contains(';');
    }

    private async Task<string> ResolveUrlAsync(string url, CancellationToken cancellationToken)
    {
        if (!XiaohongshuUrlParser.IsShortUrl(url))
        {
            return url;
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        ApplyPageHeaders(request, XiaohongshuConstants.Origin + "/");
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        return response.RequestMessage?.RequestUri?.ToString() ?? url;
    }

    private async Task<string> FetchHtmlAsync(string url, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        ApplyPageHeaders(request, XiaohongshuConstants.Origin + "/");
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(cancellationToken);
    }

    private async Task<HttpRequestMessage> CreateSignedRequestAsync(HttpMethod method, string uri, string cookie, IReadOnlyDictionary<string, object?>? payload, IReadOnlyDictionary<string, object?>? parameters, string referer, CancellationToken cancellationToken)
    {
        var signed = await _signClient.SignAsync(method.Method, uri, cookie, payload, parameters, cancellationToken);
        var requestUrl = XiaohongshuConstants.ApiOrigin + uri;
        if (method == HttpMethod.Get && parameters is { Count: > 0 })
        {
            requestUrl += "?" + BuildQuery(parameters);
        }

        var request = new HttpRequestMessage(method, requestUrl);
        ApplyJsonHeaders(request, cookie, referer);
        foreach (var (key, value) in signed)
        {
            request.Headers.TryAddWithoutValidation(key, value);
        }

        if (method == HttpMethod.Post)
        {
            request.Content = JsonContent.Create(payload ?? new Dictionary<string, object?>(), options: JsonOptions);
        }

        return request;
    }

    private void ApplyPageHeaders(HttpRequestMessage request, string referer)
    {
        request.Headers.TryAddWithoutValidation("User-Agent", XiaohongshuConstants.UserAgent);
        request.Headers.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,*/*;q=0.8");
        request.Headers.TryAddWithoutValidation("Accept-Language", "zh-CN,zh;q=0.9,en;q=0.8");
        request.Headers.TryAddWithoutValidation("Referer", referer);
        if (!string.IsNullOrWhiteSpace(MyParserRuntime.XiaohongshuCookie))
        {
            request.Headers.TryAddWithoutValidation("Cookie", MyParserRuntime.XiaohongshuCookie);
        }
    }

    private static void ApplyJsonHeaders(HttpRequestMessage request, string cookie, string referer)
    {
        request.Headers.TryAddWithoutValidation("User-Agent", XiaohongshuConstants.UserAgent);
        request.Headers.TryAddWithoutValidation("Accept", "application/json, text/plain, */*");
        request.Headers.TryAddWithoutValidation("Accept-Language", "zh-CN,zh;q=0.9,en;q=0.8");
        request.Headers.TryAddWithoutValidation("Origin", XiaohongshuConstants.Origin);
        request.Headers.TryAddWithoutValidation("Referer", referer);
        if (!string.IsNullOrWhiteSpace(cookie))
        {
            request.Headers.TryAddWithoutValidation("Cookie", cookie);
        }
    }

    private async Task<List<XiaohongshuComment>> FetchCommentsAsync(string noteId, string xsecToken, string xsecSource, int limit, CancellationToken cancellationToken)
    {
        const string uri = "/api/sns/web/v2/comment/page";
        var comments = new List<XiaohongshuComment>();
        var cursor = string.Empty;
        var maxPages = Math.Max(1, Math.Min(5, (limit + 9) / 10 + 1));
        for (var page = 0; page < maxPages; page++)
        {
            var parameters = new Dictionary<string, object?>
            {
                ["note_id"] = noteId,
                ["cursor"] = cursor,
                ["top_comment_id"] = string.Empty,
                ["image_formats"] = "jpg,webp,avif",
                ["xsec_token"] = xsecToken,
            };
            var referer = $"{XiaohongshuConstants.Origin}/explore/{noteId}?xsec_token={Uri.EscapeDataString(xsecToken)}&xsec_source={Uri.EscapeDataString(xsecSource)}";
            using var request = await CreateSignedRequestAsync(HttpMethod.Get, uri, MyParserRuntime.XiaohongshuCookie, null, parameters, referer, cancellationToken);
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if ((int)response.StatusCode is 461 or 471)
            {
                throw new XiaohongshuParseException("评论接口触发安全验证，请在浏览器完成验证后重新读取 Cookie。");
            }

            response.EnsureSuccessStatusCode();
            using var json = await response.Content.ReadFromJsonAsync<JsonDocument>(cancellationToken: cancellationToken) ?? throw new XiaohongshuParseException("评论接口空响应。");
            if (!json.RootElement.CodeOk())
            {
                throw new XiaohongshuParseException(json.RootElement.GetStringOrDefault("msg") ?? "评论接口返回异常。");
            }

            var data = json.RootElement.GetPropertyOrDefault("data") ?? json.RootElement;
            foreach (var item in data.GetPropertyOrDefault("comments").EnumerateArrayOrEmpty())
            {
                comments.Add(FormatComment(item));
                if (comments.Count >= limit)
                {
                    return comments;
                }
            }

            var hasMore = data.GetBoolLoose("has_more", "hasMore") ?? false;
            var next = data.GetStringOrDefault("cursor") ?? string.Empty;
            if (!hasMore || string.IsNullOrWhiteSpace(next) || next == cursor)
            {
                break;
            }

            cursor = next;
            await Task.Delay(350, cancellationToken);
        }

        return comments;
    }

    private async Task<(string Token, string Source)?> RecoverXsecTokenAsync(string noteId, string? userId, string? title, string? description, IReadOnlyList<string> tags, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(userId))
        {
            var token = await RecoverXsecTokenFromUserPostedAsync(noteId, userId, cancellationToken);
            if (token is not null)
            {
                return token;
            }
        }

        foreach (var keyword in DeriveSearchKeywords(title, description, tags))
        {
            var token = await RecoverXsecTokenFromSearchAsync(noteId, keyword, cancellationToken);
            if (token is not null)
            {
                return token;
            }

            await Task.Delay(250, cancellationToken);
        }

        return null;
    }

    private async Task<(string Token, string Source)?> RecoverXsecTokenFromUserPostedAsync(string noteId, string userId, CancellationToken cancellationToken)
    {
        const string uri = "/api/sns/web/v1/user_posted";
        var cursor = string.Empty;
        for (var page = 0; page < 3; page++)
        {
            var parameters = new Dictionary<string, object?> { ["num"] = 30, ["cursor"] = cursor, ["user_id"] = userId, ["image_scenes"] = "FD_WM_WEBP" };
            using var request = await CreateSignedRequestAsync(HttpMethod.Get, uri, MyParserRuntime.XiaohongshuCookie, null, parameters, $"{XiaohongshuConstants.Origin}/user/profile/{userId}", cancellationToken);
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            using var json = await response.Content.ReadFromJsonAsync<JsonDocument>(cancellationToken: cancellationToken);
            var data = json?.RootElement.GetPropertyOrDefault("data");
            foreach (var item in data?.GetPropertyOrDefault("notes").EnumerateArrayOrEmpty() ?? [])
            {
                var id = item.GetStringOrDefault("note_id") ?? item.GetStringOrDefault("noteId") ?? item.GetStringOrDefault("id");
                var token = item.GetStringOrDefault("xsec_token") ?? item.GetStringOrDefault("xsecToken");
                if (id == noteId && !string.IsNullOrWhiteSpace(token))
                {
                    return (token, item.GetStringOrDefault("xsec_source") ?? item.GetStringOrDefault("xsecSource") ?? "pc_user");
                }
            }

            var hasMore = data?.GetBoolLoose("has_more", "hasMore") ?? false;
            var next = data?.GetStringOrDefault("cursor") ?? string.Empty;
            if (!hasMore || string.IsNullOrWhiteSpace(next) || next == cursor)
            {
                break;
            }

            cursor = next;
        }

        return null;
    }

    private async Task<(string Token, string Source)?> RecoverXsecTokenFromSearchAsync(string noteId, string keyword, CancellationToken cancellationToken)
    {
        const string uri = "/api/sns/web/v1/search/notes";
        var payload = new Dictionary<string, object?>
        {
            ["keyword"] = keyword,
            ["page"] = 1,
            ["page_size"] = 20,
            ["search_id"] = GetSearchId(),
            ["sort"] = "general",
            ["note_type"] = 0,
            ["ext_flags"] = Array.Empty<object>(),
            ["filters"] = new object[]
            {
                new Dictionary<string, object?> { ["tags"] = new[] { "general" }, ["type"] = "sort_type" },
                new Dictionary<string, object?> { ["tags"] = new[] { "不限" }, ["type"] = "filter_note_type" },
                new Dictionary<string, object?> { ["tags"] = new[] { "不限" }, ["type"] = "filter_note_time" },
                new Dictionary<string, object?> { ["tags"] = new[] { "不限" }, ["type"] = "filter_note_range" },
                new Dictionary<string, object?> { ["tags"] = new[] { "不限" }, ["type"] = "filter_pos_distance" },
            },
            ["geo"] = string.Empty,
            ["image_formats"] = new[] { "jpg", "webp", "avif" },
        };
        using var request = await CreateSignedRequestAsync(HttpMethod.Post, uri, MyParserRuntime.XiaohongshuCookie, payload, null, XiaohongshuConstants.Origin + "/", cancellationToken);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        using var json = await response.Content.ReadFromJsonAsync<JsonDocument>(cancellationToken: cancellationToken);
        foreach (var item in json?.RootElement.GetPropertyOrDefault("data")?.GetPropertyOrDefault("items").EnumerateArrayOrEmpty() ?? [])
        {
            var card = item.GetPropertyOrDefault("note_card") ?? item.GetPropertyOrDefault("note") ?? item;
            var id = item.GetStringOrDefault("id") ?? card.GetStringOrDefault("note_id") ?? card.GetStringOrDefault("noteId");
            var token = item.GetStringOrDefault("xsec_token") ?? item.GetStringOrDefault("xsecToken");
            if (id == noteId && !string.IsNullOrWhiteSpace(token))
            {
                return (token, item.GetStringOrDefault("xsec_source") ?? item.GetStringOrDefault("xsecSource") ?? "pc_search");
            }
        }

        return null;
    }

    private static XiaohongshuComment FormatComment(JsonElement item)
    {
        var user = item.GetPropertyOrDefault("user_info") ?? item.GetPropertyOrDefault("userInfo");
        var create = item.GetLongLoose("create_time", "createTime", "time");
        return new XiaohongshuComment
        {
            Id = item.GetStringOrDefault("id"),
            Content = item.GetStringOrDefault("content") ?? string.Empty,
            LikeCount = item.GetLongLoose("like_count", "likeCount") ?? 0,
            IpLocation = item.GetStringOrDefault("ip_location") ?? item.GetStringOrDefault("ipLocation"),
            CreateTime = create > 0 ? DateTimeOffset.FromUnixTimeSeconds(create.Value > 9_999_999_999 ? create.Value / 1000 : create.Value) : null,
            User = new XiaohongshuUser
            {
                Id = user?.GetStringOrDefault("user_id") ?? user?.GetStringOrDefault("userId"),
                Nickname = user?.GetStringOrDefault("nickname") ?? user?.GetStringOrDefault("nickName") ?? "匿名用户",
                Avatar = user?.GetStringOrDefault("image") ?? user?.GetStringOrDefault("avatar"),
            },
            SubComments = item.GetPropertyOrDefault("sub_comments").EnumerateArrayOrEmpty().Take(3).Select(FormatComment).ToList(),
        };
    }

    private static JsonDocument ExtractInitialState(string html)
    {
        var markerIndex = html.IndexOf("window.__INITIAL_STATE__", StringComparison.Ordinal);
        if (markerIndex >= 0)
        {
            var braceIndex = html.IndexOf('{', markerIndex);
            if (braceIndex >= 0 && TryExtractJsonObject(html, braceIndex, out var objectText))
            {
                return ParseInitialStateObject(objectText);
            }
        }

        var match = InitialStateRegex().Match(html);
        if (match.Success)
        {
            return ParseInitialStateObject(match.Groups[1].Value);
        }

        throw new XiaohongshuParseException("页面中没有 __INITIAL_STATE__，可能需要登录 Cookie，或链接已失效/被风控。");
    }

    private static JsonDocument ParseInitialStateObject(string raw)
    {
        raw = WebUtility.HtmlDecode(raw).Trim().TrimEnd(';');
        raw = UndefinedRegex().Replace(raw, "null");
        return JsonDocument.Parse(raw);
    }

    private static bool TryExtractJsonObject(string text, int start, out string objectText)
    {
        var depth = 0;
        var inString = false;
        var escaped = false;
        for (var i = start; i < text.Length; i++)
        {
            var ch = text[i];
            if (inString)
            {
                if (escaped)
                {
                    escaped = false;
                }
                else if (ch == '\\')
                {
                    escaped = true;
                }
                else if (ch == '"')
                {
                    inString = false;
                }

                continue;
            }

            if (ch == '"')
            {
                inString = true;
                continue;
            }

            if (ch == '{')
            {
                depth++;
            }
            else if (ch == '}')
            {
                depth--;
                if (depth == 0)
                {
                    objectText = text[start..(i + 1)];
                    return true;
                }
            }
        }

        objectText = string.Empty;
        return false;
    }

    private static JsonElement GetNoteFromState(JsonElement state, string? noteId)
    {
        var detailMap = RecursiveFindProperty(state, "noteDetailMap") ?? RecursiveFindProperty(state, "note_detail_map");
        if (detailMap is { ValueKind: JsonValueKind.Object })
        {
            if (!string.IsNullOrWhiteSpace(noteId) && detailMap.Value.TryGetProperty(noteId, out var exact))
            {
                return exact.GetPropertyOrDefault("note") ?? exact;
            }

            foreach (var item in detailMap.Value.EnumerateObject())
            {
                var note = item.Value.GetPropertyOrDefault("note") ?? item.Value;
                if (note.ValueKind == JsonValueKind.Object)
                {
                    return note;
                }
            }
        }

        var found = RecursiveFindNoteLike(state);
        return found ?? throw new XiaohongshuParseException("没有从页面状态里找到笔记详情；请确认链接包含 xsec_token，或配置登录 Cookie 后重试。");
    }

    private static JsonElement? RecursiveFindProperty(JsonElement element, string propertyName)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            if (element.TryGetProperty(propertyName, out var value))
            {
                return value;
            }

            foreach (var item in element.EnumerateObject())
            {
                var found = RecursiveFindProperty(item.Value, propertyName);
                if (found is not null)
                {
                    return found;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                var found = RecursiveFindProperty(item, propertyName);
                if (found is not null)
                {
                    return found;
                }
            }
        }

        return null;
    }

    private static JsonElement? RecursiveFindNoteLike(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            if ((element.TryGetProperty("video", out _) || element.TryGetProperty("imageList", out _) || element.TryGetProperty("image_list", out _))
                && (element.TryGetProperty("title", out _) || element.TryGetProperty("desc", out _)))
            {
                return element;
            }

            foreach (var item in element.EnumerateObject())
            {
                var found = RecursiveFindNoteLike(item.Value);
                if (found is not null)
                {
                    return found;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                var found = RecursiveFindNoteLike(item);
                if (found is not null)
                {
                    return found;
                }
            }
        }

        return null;
    }

    private static IEnumerable<XiaohongshuVideoFormat> ExtractVideoFormats(JsonElement note, string html)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var index = 0;
        foreach (var stream in CollectStreamObjects(note.GetPropertyOrDefault("video") ?? default))
        {
            var url = stream.GetStringOrDefault("masterUrl") ?? stream.GetStringOrDefault("master_url") ?? stream.GetStringOrDefault("url");
            var backups = stream.GetStringArrayOrEmpty("backupUrls", "backup_urls");
            var urls = new[] { url }.Concat(backups).Where(i => !string.IsNullOrWhiteSpace(i) && i.StartsWith("http", StringComparison.OrdinalIgnoreCase)).Cast<string>().Distinct().ToList();
            if (urls.Count == 0 || !seen.Add(urls[0]))
            {
                continue;
            }

            yield return new XiaohongshuVideoFormat
            {
                Url = urls[0],
                Urls = urls,
                FormatId = stream.GetStringOrDefault("qualityType") ?? stream.GetStringOrDefault("quality_type") ?? stream.GetStringOrDefault("format") ?? $"stream-{index++}",
                Ext = GuessVideoExt(urls[0]),
                Width = stream.GetIntLoose("width") ?? 0,
                Height = stream.GetIntLoose("height") ?? 0,
                Fps = stream.GetDoubleLoose("fps") ?? 0,
                BitrateKbps = (stream.GetDoubleLoose("avgBitrate", "avg_bitrate", "videoBitrate", "video_bitrate", "bitrate") ?? 0) / 1000d,
                DurationSeconds = (stream.GetDoubleLoose("duration") ?? 0) / 1000d,
            };
        }

        var originKey = note.GetPropertyOrDefault("video")?.GetPropertyOrDefault("consumer")?.GetStringOrDefault("originVideoKey")
                        ?? note.GetPropertyOrDefault("video")?.GetPropertyOrDefault("consumer")?.GetStringOrDefault("origin_video_key");
        if (string.IsNullOrWhiteSpace(originKey))
        {
            var match = OriginVideoKeyRegex().Match(html);
            if (match.Success)
            {
                originKey = Regex.Unescape(match.Groups[1].Value).Replace("\\u002F", "/", StringComparison.OrdinalIgnoreCase);
            }
        }

        if (!string.IsNullOrWhiteSpace(originKey))
        {
            var originUrl = "https://sns-video-bd.xhscdn.com/" + originKey;
            if (seen.Add(originUrl))
            {
                yield return new XiaohongshuVideoFormat { Url = originUrl, Urls = [originUrl], FormatId = "origin", Ext = "mp4", BitrateKbps = 999_999 };
            }
        }

        var ogVideo = MetaContent(html, "og:video") ?? MetaContent(html, "og:video:url");
        if (!string.IsNullOrWhiteSpace(ogVideo) && ogVideo.StartsWith("http", StringComparison.OrdinalIgnoreCase) && seen.Add(ogVideo))
        {
            yield return new XiaohongshuVideoFormat { Url = ogVideo, Urls = [ogVideo], FormatId = "og:video", Ext = GuessVideoExt(ogVideo) };
        }
    }

    private static IEnumerable<JsonElement> CollectStreamObjects(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var hasUrl = element.GetStringOrDefault("masterUrl") is not null
                         || element.GetStringOrDefault("master_url") is not null
                         || element.GetStringOrDefault("url") is not null
                         || element.GetPropertyOrDefault("backupUrls") is not null
                         || element.GetPropertyOrDefault("backup_urls") is not null;
            if (hasUrl)
            {
                yield return element;
            }

            foreach (var item in element.EnumerateObject())
            {
                foreach (var found in CollectStreamObjects(item.Value))
                {
                    yield return found;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                foreach (var found in CollectStreamObjects(item))
                {
                    yield return found;
                }
            }
        }
    }

    private static IEnumerable<XiaohongshuImageInfo> ExtractImages(JsonElement note, string html)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var image in (note.GetPropertyOrDefault("imageList") ?? note.GetPropertyOrDefault("image_list")).EnumerateArrayOrEmpty())
        {
            var url = image.GetStringOrDefault("urlDefault") ?? image.GetStringOrDefault("urlPre") ?? image.GetStringOrDefault("url") ?? image.GetStringOrDefault("url_default") ?? image.GetStringOrDefault("url_pre");
            if (!string.IsNullOrWhiteSpace(url) && seen.Add(url))
            {
                yield return new XiaohongshuImageInfo { Url = url, Width = image.GetIntLoose("width") ?? 0, Height = image.GetIntLoose("height") ?? 0 };
            }
        }

        var ogImage = MetaContent(html, "og:image");
        if (!string.IsNullOrWhiteSpace(ogImage) && seen.Add(ogImage))
        {
            yield return new XiaohongshuImageInfo { Url = ogImage };
        }
    }

    private static IEnumerable<string> ExtractTags(JsonElement note)
    {
        foreach (var item in (note.GetPropertyOrDefault("tagList") ?? note.GetPropertyOrDefault("tag_list")).EnumerateArrayOrEmpty())
        {
            var name = item.GetStringOrDefault("name");
            if (!string.IsNullOrWhiteSpace(name))
            {
                yield return name;
            }
        }
    }

    private static string? MetaContent(string html, string key)
    {
        foreach (Match match in MetaRegex().Matches(html))
        {
            var tag = match.Value;
            if (Regex.IsMatch(tag, $"(?:property|name)=[\"']{Regex.Escape(key)}[\"']", RegexOptions.IgnoreCase))
            {
                var content = Regex.Match(tag, "content=[\"']([^\"']+)[\"']", RegexOptions.IgnoreCase);
                if (content.Success)
                {
                    return WebUtility.HtmlDecode(content.Groups[1].Value);
                }
            }
        }

        return null;
    }

    private static long GetVideoScore(XiaohongshuVideoFormat format)
    {
        return (long)format.Width * format.Height + (long)(format.Fps * 1000) + (long)format.BitrateKbps;
    }

    private static string GuessVideoExt(string url)
    {
        var path = Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.AbsolutePath.ToLowerInvariant() : url.ToLowerInvariant();
        foreach (var ext in new[] { "mp4", "mov", "m4v", "webm" })
        {
            if (path.Contains('.' + ext, StringComparison.OrdinalIgnoreCase))
            {
                return ext;
            }
        }

        return "mp4";
    }

    private static string FindUserIdInObject(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var key in new[] { "userId", "user_id", "userid", "id" })
            {
                var value = element.GetStringOrDefault(key);
                if (!string.IsNullOrWhiteSpace(value) && Regex.IsMatch(value, "^[0-9a-fA-F]{20,32}$"))
                {
                    return value;
                }
            }

            foreach (var item in element.EnumerateObject())
            {
                var found = FindUserIdInObject(item.Value);
                if (!string.IsNullOrWhiteSpace(found))
                {
                    return found;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                var found = FindUserIdInObject(item);
                if (!string.IsNullOrWhiteSpace(found))
                {
                    return found;
                }
            }
        }

        return string.Empty;
    }

    private static IEnumerable<string> DeriveSearchKeywords(string? title, string? description, IReadOnlyList<string> tags)
    {
        var list = new List<string>();
        Add(title);
        Add(description);
        foreach (var tag in tags)
        {
            Add(tag);
        }

        return list.Take(5);

        void Add(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return;
            }

            value = Regex.Replace(value, "https?://[^\\s，。)）>\\]]+", " ");
            value = Regex.Replace(value, "[#@]", " ");
            value = Regex.Replace(value, "\\s+", " ").Trim(" -_｜|，。,.！!？?\n\t".ToCharArray());
            if (value.Length > 36)
            {
                value = value[..36];
            }

            if (value.Length >= 2 && !list.Contains(value))
            {
                list.Add(value);
            }
        }
    }

    private static string MakeGuestCookie()
    {
        const string alphabet = "abcdefghijklmnopqrstuvwxyz0123456789";
        Span<byte> bytes = stackalloc byte[52];
        RandomNumberGenerator.Fill(bytes);
        var chars = bytes.ToArray().Select(b => alphabet[b % alphabet.Length]).ToArray();
        var cookies = new Dictionary<string, string>
        {
            ["a1"] = new string(chars),
            ["webId"] = Guid.NewGuid().ToString("N"),
            ["webBuild"] = "4.62.3",
            ["xsecappid"] = "xhs-pc-web",
        };
        return CookieDictToHeader(cookies);
    }

    private static string EnsureGuestCookieParts(string cookie)
    {
        var cookies = ParseCookieHeader(cookie);
        if (!cookies.ContainsKey("a1") || !cookies.ContainsKey("webId") || !cookies.ContainsKey("xsecappid"))
        {
            foreach (var (key, value) in ParseCookieHeader(MakeGuestCookie()))
            {
                cookies.TryAdd(key, value);
            }
        }

        return CookieDictToHeader(cookies);
    }

    private static Dictionary<string, string> ParseCookieHeader(string cookie)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in (cookie ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var pieces = part.Split('=', 2);
            if (pieces.Length == 2 && !string.IsNullOrWhiteSpace(pieces[0]))
            {
                result[pieces[0].Trim()] = pieces[1].Trim();
            }
        }

        return result;
    }

    private static string CookieDictToHeader(IReadOnlyDictionary<string, string> cookies)
    {
        return string.Join("; ", cookies.Where(i => !string.IsNullOrWhiteSpace(i.Key) && !string.IsNullOrWhiteSpace(i.Value)).Select(i => $"{i.Key}={i.Value}"));
    }

    private static string MergeSetCookie(string cookie, IEnumerable<string> setCookieHeaders)
    {
        var cookies = ParseCookieHeader(cookie);
        foreach (var header in setCookieHeaders)
        {
            var first = header.Split(';', 2)[0];
            var pieces = first.Split('=', 2);
            if (pieces.Length == 2 && !string.IsNullOrWhiteSpace(pieces[0]))
            {
                cookies[pieces[0].Trim()] = pieces[1].Trim();
            }
        }

        return CookieDictToHeader(cookies);
    }

    private static string BuildQuery(IReadOnlyDictionary<string, object?> parameters)
    {
        return string.Join("&", parameters.Select(i => $"{Uri.EscapeDataString(i.Key)}={Uri.EscapeDataString(Convert.ToString(i.Value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty)}"));
    }

    private static string GetSearchId()
    {
        var value = ((System.Numerics.BigInteger)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() << 64) + RandomNumberGenerator.GetInt32(0, int.MaxValue);
        return ToBase36(value);
    }

    private static string ToBase36(System.Numerics.BigInteger value)
    {
        const string alphabet = "0123456789abcdefghijklmnopqrstuvwxyz";
        if (value == 0)
        {
            return "0";
        }

        var sb = new StringBuilder();
        while (value > 0)
        {
            value = System.Numerics.BigInteger.DivRem(value, 36, out var remainder);
            sb.Insert(0, alphabet[(int)remainder]);
        }

        return sb.ToString();
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [GeneratedRegex("window\\.__INITIAL_STATE__\\s*=\\s*({.*?})\\s*</script>", RegexOptions.Singleline)]
    private static partial Regex InitialStateRegex();

    [GeneratedRegex("\\bundefined\\b")]
    private static partial Regex UndefinedRegex();

    [GeneratedRegex("<meta\\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex MetaRegex();

    [GeneratedRegex("\"originVideoKey\"\\s*:\\s*\"([^\"\\\\]*(?:\\\\.[^\"\\\\]*)*)\"")]
    private static partial Regex OriginVideoKeyRegex();

    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _http.Dispose();
        }
    }
}

internal static class XiaohongshuJsonExtensions
{
    public static JsonElement? GetPropertyOrDefault(this JsonElement element, string name)
    {
        return element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) ? value : null;
    }

    public static string? GetStringOrDefault(this JsonElement element, string name)
    {
        var value = element.GetPropertyOrDefault(name);
        return value?.ValueKind switch
        {
            JsonValueKind.String => value.Value.GetString(),
            JsonValueKind.Number => value.Value.ToString(),
            _ => null,
        };
    }

    public static int? GetIntLoose(this JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            var value = element.GetPropertyOrDefault(name);
            if (value is null)
            {
                continue;
            }

            if (value.Value.ValueKind == JsonValueKind.Number && value.Value.TryGetInt32(out var n)) return n;
            if (value.Value.ValueKind == JsonValueKind.String && int.TryParse(value.Value.GetString(), out n)) return n;
        }

        return null;
    }

    public static long? GetLongLoose(this JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            var value = element.GetPropertyOrDefault(name);
            if (value is null)
            {
                continue;
            }

            if (value.Value.ValueKind == JsonValueKind.Number && value.Value.TryGetInt64(out var n)) return n;
            if (value.Value.ValueKind == JsonValueKind.String && long.TryParse(value.Value.GetString(), out n)) return n;
        }

        return null;
    }

    public static double? GetDoubleLoose(this JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            var value = element.GetPropertyOrDefault(name);
            if (value is null)
            {
                continue;
            }

            if (value.Value.ValueKind == JsonValueKind.Number && value.Value.TryGetDouble(out var n)) return n;
            if (value.Value.ValueKind == JsonValueKind.String && double.TryParse(value.Value.GetString(), out n)) return n;
        }

        return null;
    }

    public static bool? GetBoolLoose(this JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            var value = element.GetPropertyOrDefault(name);
            if (value is null)
            {
                continue;
            }

            if (value.Value.ValueKind == JsonValueKind.True) return true;
            if (value.Value.ValueKind == JsonValueKind.False) return false;
            if (value.Value.ValueKind == JsonValueKind.Number && value.Value.TryGetInt32(out var n)) return n != 0;
            if (value.Value.ValueKind == JsonValueKind.String && bool.TryParse(value.Value.GetString(), out var b)) return b;
        }

        return null;
    }

    public static bool CodeOk(this JsonElement element)
    {
        if (element.GetBoolLoose("success") == false)
        {
            return false;
        }

        var code = element.GetIntLoose("code");
        return code is null or 0;
    }

    public static IEnumerable<JsonElement> EnumerateArrayOrEmpty(this JsonElement? element)
    {
        return element is { ValueKind: JsonValueKind.Array } ? element.Value.EnumerateArray() : [];
    }

    public static List<string> GetStringArrayOrEmpty(this JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            var value = element.GetPropertyOrDefault(name);
            if (value is { ValueKind: JsonValueKind.Array })
            {
                return value.Value.EnumerateArray().Select(i => i.ValueKind == JsonValueKind.String ? i.GetString() : null).Where(i => !string.IsNullOrWhiteSpace(i)).Cast<string>().ToList();
            }
        }

        return [];
    }
}
