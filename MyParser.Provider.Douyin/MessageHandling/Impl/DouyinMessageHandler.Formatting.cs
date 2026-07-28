using System.Diagnostics;
using System.Net;
using System.Text;
using MyParser.Provider.Douyin.Infrastructure;
using MyParser.Provider.Douyin.Models;
using MyParser.Provider.Douyin.Views;
using ShiroBot.AvaloniaSdk;
using ShiroBot.SDK.Models;
using ShiroBot.SDK.Abstractions;
using ShiroBot.SDK.Core;
using ShiroBot.SDK.Plugin;
using Shirobot.Plugin.MyParser.Parsing;
using static MyParser.Provider.Douyin.Infrastructure.DouyinRequestHeaders;

namespace MyParser.Provider.Douyin.MessageHandling;

internal sealed partial class DouyinMessageHandler
{
private Task<string> UploadVideoFileAsync(IncomingMessage message, DouyinParseResult result)
    {
        BotLog.Info($"MyParser 准备上传视频文件: aweme_id={result.AwemeId}, physical_path={DescribePhysicalPath(result.LocalVideoPath)}");
        return _hostServices.UploadLocalVideoFileAsync(_config, message, result.LocalVideoPath, "抖音", result.AwemeId);
    }

    private static string DescribePhysicalPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return "<none>";
        }

        try
        {
            var fullPath = Path.GetFullPath(path);
            return $"{fullPath} (exists={File.Exists(fullPath)})";
        }
        catch
        {
            return path;
        }
    }

    private static string? BuildLocalFileUri(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return null;
        }

        return new Uri(Path.GetFullPath(path)).AbsoluteUri;
    }

    private string FormatDouyinResult(
        DouyinParseResult result,
        bool videoDownloadAttempted = false,
        bool videoSent = false,
        string? videoSendError = null,
        bool fileUploaded = false,
        string? fileUploadInfo = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine(result.IsGallery ? "抖音图集解析成功" : "抖音视频解析成功");
        sb.AppendLine($"ID：{result.AwemeId}");

        if (!string.IsNullOrWhiteSpace(result.Title))
        {
            sb.AppendLine($"标题：{TrimLine(result.Title, 140)}");
        }

        if (!string.IsNullOrWhiteSpace(result.AuthorName))
        {
            sb.AppendLine($"作者：{result.AuthorName}");
        }

        if (result.IsGallery)
        {
            sb.AppendLine($"图片数：{result.Images.Count}");
            sb.AppendLine("图集：已解析，未展示直链");
        }
        else if (!string.IsNullOrWhiteSpace(result.VideoUrl))
        {
            var quality = result.Qualities.FirstOrDefault();
            var videoStatus = videoSent
                ? "视频：已下载并已调用 VideoSegment 发送接口"
                : videoDownloadAttempted
                    ? $"视频：下载或发送失败，已隐藏直链；原因：{TrimLine(videoSendError ?? "未知错误", 80)}"
                    : "视频：已解析，未展示直链";
            sb.AppendLine(videoStatus);
            if (_config.UploadVideoAsFile)
            {
                sb.AppendLine(fileUploaded
                    ? $"文件上传：已上传为{fileUploadInfo}"
                    : $"文件上传：失败或未执行；原因：{TrimLine(fileUploadInfo ?? "未知", 80)}");
            }

            if (quality is not null)
            {
                sb.AppendLine($"清晰度：{quality.Label}");
            }
        }
        else
        {
            sb.AppendLine("未提取到视频或图集资源。可尝试配置 DouyinCookie 后重试。");
        }

        if (!string.IsNullOrWhiteSpace(result.MusicUrl))
        {
            sb.AppendLine($"音乐：{result.MusicUrl}");
        }

        return sb.ToString().TrimEnd();
    }

    private static string GetPlainText(IncomingMessage message) => message.GetPlainText();

    private static string TrimLine(string value, int maxLength) => ProviderTextUtilities.TrimLine(value, maxLength);
}
