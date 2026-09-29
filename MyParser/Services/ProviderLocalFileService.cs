using System.Diagnostics;
using ShiroBot.SDK.Abstractions;
using ShiroBot.SDK.Models;
using ShiroBot.SDK.Plugin;
using ShiroBot.Model.QQ;

namespace Shirobot.Plugin.MyParser.Services;

internal sealed class ProviderLocalFileService(IBotContext context)
{
    public Task RunLoggedBackgroundAsync(string description, Func<Task> action) => Task.Run(async () =>
    {
        try
        {
            await action().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            BotLog.Warning($"MyParser {description} 未完成: {ex.Message}");
        }
    });

    public string ResolveCookiePath(string fileName)
    {
        var cookieDirectory = Path.Combine(context.PluginDirectory, "cookies");
        Directory.CreateDirectory(cookieDirectory);
        return Path.Combine(cookieDirectory, Path.GetFileName(fileName));
    }

    public Task<string> UploadLocalVideoFileAsync(
        PluginConfig config,
        MessageEvent message,
        string? localVideoPath,
        string platformName,
        string mediaId) => UploadLocalFileAsync(config, message, localVideoPath, platformName, mediaId);

    public async Task<string> UploadLocalFileAsync(
        PluginConfig config,
        MessageEvent message,
        string? localFilePath,
        string platformName,
        string mediaId,
        bool preferBase64 = false)
    {
        if (string.IsNullOrWhiteSpace(localFilePath) || !File.Exists(localFilePath))
            throw new InvalidOperationException("本地文件不存在。");

        var fileApi = context.GetAdapterExtension<IQFileApi>()
                      ?? throw new NotSupportedException("当前适配器不支持 QQ 文件上传扩展。");
        if (!long.TryParse(message.Channel.Id, out var peerId))
            throw new NotSupportedException("当前渠道 ID 不是 QQ 数字 ID，无法上传文件。");

        var localPath = Path.GetFullPath(localFilePath);
        var fileSize = new FileInfo(localPath).Length;
        var uploadMode = preferBase64 ? "base64" : "file";
        var fileUri = preferBase64
            ? "base64://" + Convert.ToBase64String(await File.ReadAllBytesAsync(localPath).ConfigureAwait(false))
            : new Uri(localPath).AbsoluteUri;
        var fileName = Path.GetFileName(localPath);
        var stopwatch = Stopwatch.StartNew();
        BotLog.Info($"MyParser {platformName} 文件上传开始: media_id={mediaId}, mode={uploadMode}, file_mb={fileSize / 1024d / 1024d:F2}, file={localPath}");

        var fileId = message.Channel.Type switch
        {
            ChannelType.Group => await fileApi.UploadGroupFileAsync(peerId, fileUri, fileName).ConfigureAwait(false),
            ChannelType.Direct => await fileApi.UploadPrivateFileAsync(peerId, fileUri, fileName).ConfigureAwait(false),
            _ => throw new NotSupportedException("当前消息类型不支持文件上传。"),
        };
        var scene = message.Channel.Type == ChannelType.Group ? "group" : "friend";
        if (string.IsNullOrWhiteSpace(fileId))
            BotLog.Warning($"MyParser 文件上传返回空 FileId；不再按失败处理。scene={scene}, mode={uploadMode}");
        return $"{scene} FileId={fileId} Mode={uploadMode} elapsed={stopwatch.Elapsed:mm\\:ss}";
    }

    public string GetMessageScene(MessageEvent message) => message.Channel.Type switch
    {
        ChannelType.Group => "group",
        ChannelType.Direct => "friend",
        ChannelType.Thread => "thread",
        ChannelType.Other => "other",
        _ => "unknown",
    };
}
