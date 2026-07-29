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
public override async Task HandleLoginAsync(IncomingMessage message)
    {
        try
        {
            if (_provider is not IQrLoginProvider qrLoginProvider)
            {
                await ReplyAsync(message, "小红书 provider 不支持扫码登录。");
                return;
            }

            var session = await qrLoginProvider.GenerateQrLoginSessionAsync();
            await ReplyAsync(message,
                "小红书扫码登录\n"
                + "请用小红书 App 扫描下面二维码，并在 3 分钟内确认登录。\n"
                + "如果触发安全验证，请改用浏览器登录后复制 Cookie。\n"
                + $"如果二维码图片无法显示，请打开：{session.Url}");
            await SendQrImageAsync(message, session.Url, $"xhs_qr_{session.Id}");

            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            var current = session;
            while (!cts.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(3), cts.Token);
                var poll = await qrLoginProvider.PollQrLoginAsync(current, cts.Token);
                current = current with { State = poll.State ?? current.State };
                if (poll.NeedVerify)
                {
                    await ReplyAsync(message, poll.Message);
                    return;
                }

                if (poll.IsLogin)
                {
                    SaveXiaohongshuCookieToPluginDirectory();
                    await ReplyAsync(message, $"小红书登录成功：{poll.UserName ?? "已登录"}。Cookie 已保存到插件 cookies/xiaohongshu.txt。");
                    return;
                }

                BotLog.Info($"MyParser 小红书二维码轮询: status={poll.Code}, message={poll.Message}");
            }
        }
        catch (OperationCanceledException)
        {
            await ReplyAsync(message, "小红书登录二维码已超时，请重新发送登录命令。");
        }
        catch (Exception ex)
        {
            BotLog.Warning($"MyParser 小红书扫码登录失败：{ex}");
            await ReplyAsync(message, "小红书扫码登录失败：" + ex.Message);
        }
    }
}
