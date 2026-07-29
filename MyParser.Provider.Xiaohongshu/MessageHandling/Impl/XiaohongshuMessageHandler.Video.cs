using System.Diagnostics;
using System.Net;
using System.Text;
using Net.Codecrete.QrCodeGenerator;
using ShiroBot.AvaloniaSdk;
using Shirobot.Plugin.MyParser.Parsing;
using MyParser.Provider.Xiaohongshu.Infrastructure;
using MyParser.Provider.Xiaohongshu.Models;
using MyParser.Provider.Xiaohongshu.Views;
using ShiroBot.SDK.Models;
using ShiroBot.SDK.Abstractions;
using ShiroBot.SDK.Core;
using ShiroBot.SDK.Plugin;

namespace MyParser.Provider.Xiaohongshu.MessageHandling;

internal sealed partial class XiaohongshuMessageHandler
{
private async Task SendVideoFlowAsync(IncomingMessage message, XiaohongshuParseResult result)
    {
        string? fileUploadInfo = null;
        try
        {
            _ = StartSendCoverOrCardAsync(message, result);
            var videoSegment = await BuildVideoSegmentAsync(result);
            await SendVideoMessageAsync(message, result, videoSegment);

            if (_config.UploadVideoAsFile && !_config.UploadVideoAsFileOnlyOnVideoSendFailure && !string.IsNullOrWhiteSpace(result.LocalVideoPath))
            {
                fileUploadInfo = await UploadVideoFileAsync(message, result);
                BotLog.Info($"MyParser 小红书文件上传完成: note_id={result.NoteId}, {fileUploadInfo}");
            }

            if (result.LocalVideoRegisteredToHttpServer && _config.DeleteLocalVideoDelaySeconds <= 0)
            {
                _hostServices.UnregisterLocalVideoFile(result.LocalVideoPath);
                result.LocalVideoRegisteredToHttpServer = false;
            }

            _hostServices.DeleteLocalVideoIfConfigured(_config, result.LocalVideoPath, "xiaohongshu");
        }
        catch (Exception ex)
        {
            BotLog.Warning($"MyParser 小红书视频消息发送失败: note_id={result.NoteId}, error={ex.Message}");
            if (_config.UploadVideoAsFile && !string.IsNullOrWhiteSpace(result.LocalVideoPath))
            {
                try
                {
                    fileUploadInfo = await UploadVideoFileAsync(message, result);
                    if (result.LocalVideoRegisteredToHttpServer && _config.DeleteLocalVideoDelaySeconds <= 0)
                    {
                        _hostServices.UnregisterLocalVideoFile(result.LocalVideoPath);
                        result.LocalVideoRegisteredToHttpServer = false;
                    }

                    _hostServices.DeleteLocalVideoIfConfigured(_config, result.LocalVideoPath, "xiaohongshu");
                    BotLog.Info($"MyParser 小红书 VideoSegment 失败后文件上传完成: note_id={result.NoteId}, {fileUploadInfo}");
                    return;
                }
                catch (Exception uploadEx)
                {
                    throw new XiaohongshuParseException("视频发送失败，文件上传也失败：" + uploadEx.Message);
                }
            }

            throw;
        }
    }

    private Task StartSendCoverOrCardAsync(IncomingMessage message, XiaohongshuParseResult result)
    {
        if (string.IsNullOrWhiteSpace(result.CoverUrl) && result.Images.Count == 0)
        {
            return Task.CompletedTask;
        }

        return _hostServices.RunLoggedBackgroundAsync($"小红书封面卡片异步发送: note_id={result.NoteId}", () => SendCoverOrCardAsync(message, result));
    }

    private async Task<VideoOutgoingSegment> BuildVideoSegmentAsync(XiaohongshuParseResult result)
    {
        var (fileUri, localPath) = await DownloadVideoAsync(result);
        result.LocalVideoFileUri = fileUri;
        result.LocalVideoPath = localPath;
        var segmentResult = await _hostServices.BuildLocalVideoSegmentAsync(_config, new ProviderLocalVideoSegmentRequest(
            "小红书",
            result.NoteId,
            localPath,
            fileUri,
            result.CoverUrl,
            "note_id"));
        result.LocalVideoRegisteredToHttpServer = segmentResult.RegisteredToHttpServer;
        return segmentResult.Segment;
    }

    private Task<(string FileUri, string LocalPath)> DownloadVideoAsync(XiaohongshuParseResult result)
    {
        var selected = result.SelectedVideo ?? throw new XiaohongshuParseException("没有可下载的小红书视频地址。");
        var candidates = (selected.Urls.Count > 0 ? selected.Urls : [selected.Url])
            .Where(i => !string.IsNullOrWhiteSpace(i))
            .ToArray();
        var extension = string.IsNullOrWhiteSpace(selected.Ext) ? "mp4" : selected.Ext.Trim('.');
        var request = new ProviderVideoDownloadRequest(
            "xiaohongshu",
            "小红书",
            result.NoteId,
            $"xiaohongshu:{result.NoteId}:{selected.FormatId}:{selected.Width}x{selected.Height}:{selected.BitrateKbps:0}",
            candidates,
            MyParserRuntime.XiaohongshuDownloadDirectory,
            "xhs",
            extension,
            (method, url, range) => CreateVideoRequest(method, url, result, range),
            ProviderVideoValidationKind.Mp4OrWebM,
            "note_id");

        return _hostServices.DownloadProviderVideoAsync(_config, request);
    }

    private static HttpRequestMessage CreateVideoRequest(HttpMethod method, string url, XiaohongshuParseResult result, string? range)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.TryAddWithoutValidation("User-Agent", XiaohongshuConstants.UserAgent);
        request.Headers.TryAddWithoutValidation("Referer", result.SourceUrl ?? XiaohongshuConstants.Origin + "/");
        request.Headers.TryAddWithoutValidation("Accept", "video/webm,video/mp4,video/*;q=0.9,*/*;q=0.8");
        if (!string.IsNullOrWhiteSpace(range))
        {
            request.Headers.TryAddWithoutValidation("Range", range);
        }

        if (!string.IsNullOrWhiteSpace(MyParserRuntime.XiaohongshuCookie))
        {
            request.Headers.TryAddWithoutValidation("Cookie", MyParserRuntime.XiaohongshuCookie);
        }

        return request;
    }

    private async Task SendVideoMessageAsync(IncomingMessage message, XiaohongshuParseResult result, VideoOutgoingSegment videoSegment)
    {
        var segments = new OutgoingSegment[] { videoSegment };
        var stopwatch = Stopwatch.StartNew();
        BotLog.Info($"MyParser 小红书 VideoSegment 发送开始: note_id={result.NoteId}, scene={GetMessageScene(message)}, uri_mode={_hostServices.GetUriMode(videoSegment.Uri)}");
        await _context.Message.ReplyAsync(message, segments);

        BotLog.Info($"MyParser 小红书 VideoSegment 发送完成: note_id={result.NoteId}, elapsed={stopwatch.Elapsed:mm\\:ss}");
    }
}
