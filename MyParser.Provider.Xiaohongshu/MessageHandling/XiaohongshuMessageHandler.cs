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

internal sealed partial class XiaohongshuMessageHandler : ProviderMessageHandlerBase
{
    public override string ProviderId => "xiaohongshu";

    private readonly IBotContext _context;
    private readonly PluginConfig _config;
    private readonly ParseProviderRegistry _providerRegistry;
    private readonly IParseProvider _provider;
    private readonly IProviderHostServices _hostServices;
    
    public XiaohongshuMessageHandler(IBotContext context, PluginConfig config, ParseProviderRegistry providerRegistry, IParseProvider provider, IProviderHostServices hostServices)
        : base(new ProviderMessageHandlerContext(context, config, providerRegistry, provider, hostServices))
    {
        _context = context;
        _config = config;
        _providerRegistry = providerRegistry;
        _provider = provider;
        _hostServices = hostServices;
    }

    public override async Task ParseAndReplyAsync(IncomingMessage message, string text, bool silentProviderMismatch = false, CancellationToken cancellationToken = default)
    {
        try
        {
            await TryReactToSourceMessageAsync(message, "351");
            var media = await _providerRegistry.ParseAsync(text, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (media.ProviderPayload is not XiaohongshuParseResult result)
            {
                await TryReactToSourceMessageAsync(message, "9");
                await ReplyAsync(message, "小红书链接已识别，但发送流程尚未接入。");
                return;
            }

            if (result.IsVideo && _config.SendVideoSegment)
            {
                await SendVideoFlowAsync(message, result);
                await TryReactToSourceMessageAsync(message, "426");
                return;
            }

            if (result.IsGallery)
            {
                await SendGalleryForwardAsync(message, result);
                await SendGalleryCardAsync(message, result);
                await TryReactToSourceMessageAsync(message, "426");
                return;
            }

            await ReplyAsync(message, FormatResult(result));
            await TryReactToSourceMessageAsync(message, "426");
        }
        catch (XiaohongshuSignRequiredException ex)
        {
            await TryReactToSourceMessageAsync(message, "9");
            await ReplyAsync(message, "小红书解析需要 xhshow sign 服务：" + ex.Message);
        }
        catch (XiaohongshuParseException ex)
        {
            await TryReactToSourceMessageAsync(message, "9");
            await ReplyAsync(message, "小红书解析失败：" + ex.Message);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (TaskCanceledException)
        {
            await TryReactToSourceMessageAsync(message, "9");
            await ReplyAsync(message, "小红书解析超时，请稍后重试。若经常失败，请检查 Cookie / sign 服务。");
        }
        catch (Exception ex)
        {
            await TryReactToSourceMessageAsync(message, "9");
            BotLog.Error($"MyParser 小红书解析异常：{ex}");
            await ReplyAsync(message, "小红书解析异常：" + ex.Message);
        }
    }





}
