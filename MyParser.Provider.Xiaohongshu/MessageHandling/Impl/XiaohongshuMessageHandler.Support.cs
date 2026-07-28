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
private async Task SendQrImageAsync(IncomingMessage message, string text, string fileName)
    {
        var qrFile = await BuildQrImageAsync(text, fileName);
        await SendImageAsync(message, new ImageOutgoingSegment(qrFile.Uri));
    }

    private static async Task<(string Uri, string Path)> BuildQrImageAsync(string text, string fileName)
    {
        var dir = Path.Combine(Path.GetTempPath(), "Shirobot.Plugin.MyParser", "xiaohongshu", "qr");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, fileName + ".png");
        var qr = QrCode.EncodeText(text, QrCode.Ecc.Medium);
        var png = qr.ToPngBitmap(border: 4, scale: 8);
        await File.WriteAllBytesAsync(path, png);
        return ("base64://" + Convert.ToBase64String(png), path);
    }

    private Task<string> UploadVideoFileAsync(IncomingMessage message, XiaohongshuParseResult result)
    {
        return _hostServices.UploadLocalVideoFileAsync(_config, message, result.LocalVideoPath, "小红书", result.NoteId);
    }

    private Task TryReactToSourceMessageAsync(IncomingMessage message, string faceId)
    {
        return _hostServices.ReactAsync(message, faceId, "小红书");
    }

    private void SaveXiaohongshuCookieToPluginDirectory()
    {
        var path = ResolveCookiePath("xiaohongshu.txt");
        File.WriteAllText(path, MyParserRuntime.XiaohongshuCookie ?? string.Empty, Encoding.UTF8);
    }

    private static string BuildHeaderText(XiaohongshuParseResult result)
    {
        var sb = new StringBuilder();
        sb.AppendLine(result.IsVideo ? "小红书视频" : "小红书图文");
        if (!string.IsNullOrWhiteSpace(result.Title)) sb.AppendLine(result.Title);
        if (!string.IsNullOrWhiteSpace(result.AuthorName)) sb.AppendLine("作者：" + result.AuthorName);
        if (!string.IsNullOrWhiteSpace(result.Description)) sb.AppendLine(TrimLine(result.Description, 600));
        sb.AppendLine($"数据：{FormatCount(result.LikeCount)}赞 / {FormatCount(result.CollectCount)}收藏 / {FormatCount(result.CommentCount)}评论");
        return sb.ToString().TrimEnd();
    }

    private static string BuildCommentsText(XiaohongshuParseResult result)
    {
        var sb = new StringBuilder();
        sb.AppendLine("前 10 条评论");
        foreach (var (comment, index) in result.Comments.Take(10).Select((comment, index) => (comment, index + 1)))
        {
            sb.AppendLine($"{index}. {comment.User.Nickname}: {comment.Content}");
        }

        return sb.ToString().TrimEnd();
    }

    private string FormatResult(XiaohongshuParseResult result)
    {
        var sb = new StringBuilder();
        sb.AppendLine(result.IsVideo ? "小红书视频解析成功" : "小红书解析成功");
        sb.AppendLine($"Note：{result.NoteId}");
        if (!string.IsNullOrWhiteSpace(result.Title)) sb.AppendLine($"标题：{TrimLine(result.Title, 140)}");
        if (!string.IsNullOrWhiteSpace(result.AuthorName)) sb.AppendLine($"作者：{result.AuthorName}");
        if (!string.IsNullOrWhiteSpace(result.Description)) sb.AppendLine($"摘要：{TrimLine(result.Description, 220)}");
        sb.AppendLine($"图片数：{result.Images.Count}");
        sb.AppendLine($"评论：已获取 {result.Comments.Count} 条");
        sb.AppendLine($"数据：{FormatCount(result.LikeCount)}赞 / {FormatCount(result.CollectCount)}收藏 / {FormatCount(result.CommentCount)}评论");
        if (!string.IsNullOrWhiteSpace(result.SourceUrl)) sb.AppendLine($"链接：{result.SourceUrl}");
        return sb.ToString().TrimEnd();
    }


    private string ResolveDownloadDirectory()
    {
        return Path.IsPathRooted(MyParserRuntime.XiaohongshuDownloadDirectory)
            ? MyParserRuntime.XiaohongshuDownloadDirectory
            : Path.Combine(AppContext.BaseDirectory, MyParserRuntime.XiaohongshuDownloadDirectory);
    }

    private static string FormatCount(long value)
    {
        if (value <= 0) return "--";
        if (value >= 100_000_000) return $"{value / 100_000_000d:F1}亿";
        if (value >= 10_000) return $"{value / 10_000d:F1}万";
        return value.ToString();
    }

    private static string TrimLine(string value, int maxLength) => ProviderTextUtilities.TrimLine(value, maxLength);
}
