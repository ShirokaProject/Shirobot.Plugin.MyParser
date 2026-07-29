using MyParser.Provider.Douyin.Views;
using MyParser.Provider.Douyin.Models;
using MyParser.Provider.Douyin.Infrastructure;
using static MyParser.Provider.Douyin.Infrastructure.DouyinRequestHeaders;
using System.Diagnostics;
using System.Net;
using System.Text;
using ShiroBot.AvaloniaSdk;
using Shirobot.Plugin.MyParser.Parsing;
using ShiroBot.SDK.Models;
using ShiroBot.SDK.Abstractions;
using ShiroBot.SDK.Core;
using ShiroBot.SDK.Plugin;

namespace MyParser.Provider.Douyin.MessageHandling;

internal sealed partial class DouyinMessageHandler : ProviderMessageHandlerBase
{
    public override string ProviderId => "douyin";

    private readonly IBotContext _context;
    private readonly PluginConfig _config;
    private readonly ParseProviderRegistry _providerRegistry;
    private readonly IParseProvider _douyinProvider;
    private readonly IProviderHostServices _hostServices;
    
    public DouyinMessageHandler(
        IBotContext context,
        PluginConfig config,
        ParseProviderRegistry providerRegistry,
        IParseProvider douyinProvider,
        IProviderHostServices hostServices)
        : base(new ProviderMessageHandlerContext(context, config, providerRegistry, douyinProvider, hostServices))
    {
        _context = context;
        _config = config;
        _providerRegistry = providerRegistry;
        _douyinProvider = douyinProvider;
        _hostServices = hostServices;
    }

    public override async Task ParseAndReplyAsync(IncomingMessage message, string text, bool silentProviderMismatch = false, CancellationToken cancellationToken = default)
    {
        await TryReactToSourceMessageAsync(message, "351");
        if (_providerRegistry is null)
        {
            await TryReactToSourceMessageAsync(message, "9");
            await _context.Message.ReplyAsync(message, "解析器尚未初始化，请稍后重试。");
            return;
        }

        try
        {
            var media = await _providerRegistry.ParseAsync(text, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (media.ProviderPayload is not DouyinParseResult result)
            {
                await TryReactToSourceMessageAsync(message, "9");
                await _context.Message.ReplyAsync(message, $"{media.ProviderName} 已识别，但该平台发送流程尚未接入。");
                return;
            }

            if (result.IsIgnored)
            {
                await TryReactToSourceMessageAsync(message, "426");
                return;
            }

            LogDouyinQualityInfo(result);
            _ = StartSendCommentsMessageAsync(message, result, cancellationToken);
            var shouldDownloadVideo = _config.SendVideoSegment && result.IsVideo && !result.IsGallery;
            var videoSent = false;
            var fileUploaded = false;
            string? videoSendError = null;
            string? fileUploadInfo = null;

            if (shouldDownloadVideo)
            {
                try
                {
                    _ = StartSendCoverMessageAsync(message, result, cancellationToken);
                    var videoSegment = await BuildVideoSegmentAsync(result);
                    cancellationToken.ThrowIfCancellationRequested();
                    if (videoSegment is null)
                    {
                        await TryReactToSourceMessageAsync(message, "9");
                        await _context.Message.ReplyAsync(message, "视频解析成功，但没有生成 VideoSegment。");
                        return;
                    }

                    await SendVideoMessageAsync(message, result, videoSegment);
                    videoSent = true;

                    if (_config.UploadVideoAsFile && !_config.UploadVideoAsFileOnlyOnVideoSendFailure && !string.IsNullOrWhiteSpace(result.LocalVideoPath))
                    {
                        try
                        {
                            fileUploadInfo = await UploadVideoFileAsync(message, result);
                            fileUploaded = true;
                            BotLog.Info($"MyParser 文件上传完成: aweme_id={result.AwemeId}, {fileUploadInfo}");
                        }
                        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                        {
                            throw;
                        }
                        catch (Exception uploadEx)
                        {
                            fileUploadInfo = uploadEx.Message;
                            BotLog.Warning($"MyParser 文件上传失败: aweme_id={result.AwemeId}, error={uploadEx.Message}");
                        }
                    }

                    if (result.LocalVideoRegisteredToHttpServer && _config.DeleteLocalVideoDelaySeconds <= 0)
                    {
                        _hostServices.UnregisterLocalVideoFile(result.LocalVideoPath);
                        result.LocalVideoRegisteredToHttpServer = false;
                    }

                    _hostServices.DeleteLocalVideoIfConfigured(_config, result.LocalVideoPath, "douyin");
                    await TryReactToSourceMessageAsync(message, "426");
                    return;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    BotLog.Warning($"MyParser 视频消息发送失败：{ex.Message}");
                    if (_config.UploadVideoAsFile && !string.IsNullOrWhiteSpace(result.LocalVideoPath))
                    {
                        try
                        {
                            fileUploadInfo = await UploadVideoFileAsync(message, result);
                            fileUploaded = true;
                            BotLog.Info($"MyParser VideoSegment 失败后文件上传完成: aweme_id={result.AwemeId}, {fileUploadInfo}");
                            if (result.LocalVideoRegisteredToHttpServer && _config.DeleteLocalVideoDelaySeconds <= 0)
                            {
                                _hostServices.UnregisterLocalVideoFile(result.LocalVideoPath);
                                result.LocalVideoRegisteredToHttpServer = false;
                            }

                            _hostServices.DeleteLocalVideoIfConfigured(_config, result.LocalVideoPath, "douyin");
                            await TryReactToSourceMessageAsync(message, "426");
                            return;
                        }
                        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                        {
                            throw;
                        }
                        catch (Exception uploadEx)
                        {
                            BotLog.Warning($"MyParser VideoSegment 失败后文件上传也失败: aweme_id={result.AwemeId}, error={uploadEx.Message}");
                            await TryReactToSourceMessageAsync(message, "9");
                            await _context.Message.ReplyAsync(message, "视频发送失败，文件上传也失败：" + uploadEx.Message);
                            return;
                        }
                    }

                    await TryReactToSourceMessageAsync(message, "9");
                    await _context.Message.ReplyAsync(message, "视频发送失败：" + ex.Message);
                    return;
                }
            }

            if (result.IsGallery)
            {
                if (!string.IsNullOrWhiteSpace(result.CoverUrl))
                {
                    await SendCoverMessageAsync(message, result, cancellationToken);
                }

                cancellationToken.ThrowIfCancellationRequested();
                await SendGalleryMessageAsync(message, result, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                if (!string.IsNullOrWhiteSpace(result.MusicUrl))
                {
                    await SendMusicMessageAsync(message, result);
                }

                await TryReactToSourceMessageAsync(message, "426");
                return;
            }

            var reply = FormatDouyinResult(result, shouldDownloadVideo, videoSent, videoSendError, fileUploaded, fileUploadInfo);
            if (_config.QuoteReply)
            {
                await _context.Message.QuoteReplyAsync(message, reply);
            }
            else
            {
                await _context.Message.ReplyAsync(message, reply);
            }
            await TryReactToSourceMessageAsync(message, "426");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (DouyinParseException ex)
        {
            await TryReactToSourceMessageAsync(message, "9");
            await _context.Message.ReplyAsync(message, "解析失败：" + ex.Message);
        }
        catch (TaskCanceledException)
        {
            await TryReactToSourceMessageAsync(message, "9");
            await _context.Message.ReplyAsync(message, "解析超时，请稍后重试。若经常失败，请配置有效 DouyinCookie。");
        }
        catch (Exception ex)
        {
            await TryReactToSourceMessageAsync(message, "9");
            BotLog.Error($"MyParser 解析异常：{ex}");
            await _context.Message.ReplyAsync(message, "解析异常：" + ex.Message);
        }
    }

    public override Task HandleLoginAsync(IncomingMessage message)
    {
        return _context.Message.ReplyAsync(message, "Douyin provider 暂不支持扫码登录，请编辑插件 cookies/douyin.txt。 ");
    }

    private Task TryReactToSourceMessageAsync(IncomingMessage message, string faceId)
    {
        return _hostServices.ReactAsync(message, faceId, "Douyin");
    }

    private static void LogDouyinQualityInfo(DouyinParseResult result)
    {
        if (result.Qualities.Count == 0)
        {
            BotLog.Info($"MyParser 抖音解析: aweme_id={result.AwemeId}, type={(result.IsGallery ? "gallery" : "unknown")}, qualities=0");
            return;
        }

        var selected = result.Qualities.First();
        BotLog.Info(
            "MyParser 抖音选中画质: "
            + $"aweme_id={result.AwemeId}, "
            + $"label={selected.Label}, "
            + $"ratio={selected.Ratio}, "
            + $"fps={(selected.Fps > 0 ? selected.Fps : 0)}, "
            + $"bitrate_kbps={(selected.BitRate > 0 ? selected.BitRate / 1000 : 0)}, "
            + $"size={selected.Width}x{selected.Height}, "
            + $"codec={(string.IsNullOrWhiteSpace(selected.Codec) ? "unknown" : selected.Codec)}, "
            + $"gear={selected.GearName}, "
            + $"total_options={result.Qualities.Count}");

        foreach (var (quality, index) in result.Qualities.Take(12).Select((quality, index) => (quality, index + 1)))
        {
            BotLog.Info(
                "MyParser 抖音可用画质: "
                + $"#{index}, "
                + $"label={quality.Label}, "
                + $"ratio={quality.Ratio}, "
                + $"fps={(quality.Fps > 0 ? quality.Fps : 0)}, "
                + $"bitrate_kbps={(quality.BitRate > 0 ? quality.BitRate / 1000 : 0)}, "
                + $"size={quality.Width}x{quality.Height}, "
                + $"codec={(string.IsNullOrWhiteSpace(quality.Codec) ? "unknown" : quality.Codec)}, "
                + $"gear={quality.GearName}, "
                + $"bytevc1={quality.IsByteVc1}");
        }
    }





}
