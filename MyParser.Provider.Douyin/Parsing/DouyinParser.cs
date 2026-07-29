using Shirobot.Plugin.MyParser.Parsing;
using MyParser.Provider.Douyin.Models;
using MyParser.Provider.Douyin.Abstractions;
using MyParser.Provider.Douyin.Services;
using MyParser.Provider.Douyin.WorkParsers;
using MyParser.Provider.Douyin.Infrastructure;
using MyParser.Provider.Douyin.Utilities;

namespace MyParser.Provider.Douyin.Parsing;

public sealed class DouyinParser : IParserHttpClientAccessor, IDisposable
{
    private readonly HttpClient _http;
    private readonly bool _ownsHttpClient;
    private readonly DouyinParseService _parseService;
    private readonly DouyinLoginStatusChecker _loginStatusChecker;

    public HttpClient HttpClient => _http;

    public DouyinParser(PluginConfig config, HttpClient? httpClient = null)
    {
        _ownsHttpClient = httpClient is null;
        _http = httpClient ?? DouyinHttpClientFactory.Create(config);

        IDouyinWorkParser[] workParsers =
        [
            new DouyinLiveWorkParser(),
            new DouyinGalleryWorkParser(),
            new DouyinVideoWorkParser(config),
        ];

        _parseService = new DouyinParseService(_http, workParsers, config);
        _loginStatusChecker = new DouyinLoginStatusChecker(_http);
    }

    public void SetCookieIfEmpty(string? cookie)
    {
        if (!string.IsNullOrWhiteSpace(MyParserRuntime.DouyinCookie) || string.IsNullOrWhiteSpace(cookie))
        {
            return;
        }

        MyParserRuntime.DouyinCookie = cookie.Trim();
    }

    public Task<string> CheckLoginStatusAsync(CancellationToken cancellationToken = default)
    {
        return _loginStatusChecker.CheckLoginStatusAsync(cancellationToken);
    }

    public Task<DouyinParseResult> ParseAsync(string text, CancellationToken cancellationToken = default)
    {
        return _parseService.ParseAsync(text, cancellationToken);
    }

    public static bool ContainsDouyinUrl(string text) => DouyinUrlParser.ContainsDouyinUrl(text);

    public static string? ExtractDouyinUrl(string text) => DouyinUrlParser.ExtractDouyinUrl(text);

    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _http.Dispose();
        }
    }
}
