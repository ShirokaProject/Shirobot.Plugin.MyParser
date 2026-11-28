using ShiroBot.SDK.Models;
using ShiroBot.SDK.Plugin;

namespace Shirobot.Plugin.MyParser.MessageHandling;

/// <summary>一次语音投递最终走的是哪一级。</summary>
internal enum VoiceDeliveryLevel
{
    /// <summary>1. 原音频直接作为语音发送。</summary>
    Original,
    /// <summary>2. 编码成 Silk 后作为语音发送（QQ）。</summary>
    Silk,
    /// <summary>3. 都发不了，上传原音频文件（MP3）。</summary>
    File,
}

/// <summary>要发的一段音频。</summary>
/// <param name="LocalPath">下载好的原音频（MP3）。</param>
/// <param name="PreparedSilk">提前开始的 Silk 编码；null 时需要时再编码。</param>
/// <param name="OnVoiceSent">每条语音发出后的回调（例如登记「回复 mp3 获取文件」）。</param>
internal sealed record VoicePackage(
    string ProviderId,
    string ProviderName,
    string MediaId,
    string LocalPath,
    string FileNamePrefix,
    Task<IReadOnlyList<RecordOutgoingSegment>>? PreparedSilk = null,
    Action<string>? OnVoiceSent = null);

/// <summary>
/// 多平台语音兜底发送。按顺序尝试：
/// 1. 原音频直接发语音（平台支持语音且不需要 Silk 时）；
/// 2. 需要 Silk 的地方（QQ 且开启了 Silk 编码，或原音频被 QQ 拒绝）编码后再发；
/// 3. 都发不了时上传原 MP3 文件。
/// </summary>
internal sealed class VoiceFallbackSender(IMessageContext? messages, IProviderHostServices host, PluginConfig config)
{
    /// <summary>这个会话是否要发 Silk：QQ 且开启了 Silk 编码。用于决定要不要提前编码。</summary>
    public bool NeedsSilk(MessageEvent message) =>
        config.EnableSilkEncoding && IsQq(message) && SupportsVoice(message);

    public bool SupportsVoice(MessageEvent message) =>
        MessageCapabilityProbe.Get(messages, message) is not { } capabilities
        || capabilities.NativeFeatures.HasFlag(MessageFeatures.Audio);

    /// <summary>按三级兜底发送；全部失败时抛出最后一次的异常，由调用方上报。</summary>
    public async Task<VoiceDeliveryLevel> SendAsync(MessageEvent message, VoicePackage package, CancellationToken cancellationToken)
    {
        Exception? lastError = null;
        if (SupportsVoice(message))
        {
            if (!NeedsSilk(message))
            {
                try
                {
                    var uri = await host.BuildRecordUriAsync(package.LocalPath, cancellationToken).ConfigureAwait(false);
                    await SendVoiceAsync(message, package, [new RecordOutgoingSegment(uri)]).ConfigureAwait(false);
                    return Log(package, VoiceDeliveryLevel.Original);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    lastError = ex;
                    BotLog.Warning($"MyParser {package.ProviderName} 原音频语音发送失败: media_id={package.MediaId}, error={ex.Message}");
                }
            }

            // QQ 上原音频被拒绝时，即使没开 Silk 编码也再用 Silk 试一次。
            if (IsQq(message))
            {
                try
                {
                    var records = await (package.PreparedSilk ?? EncodeSilkAsync(package, cancellationToken)).ConfigureAwait(false);
                    await SendVoiceAsync(message, package, records).ConfigureAwait(false);
                    return Log(package, VoiceDeliveryLevel.Silk);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    lastError = ex;
                    BotLog.Warning($"MyParser {package.ProviderName} Silk 语音发送失败: media_id={package.MediaId}, error={ex.Message}");
                }
            }
        }

        try
        {
            var upload = await host.UploadLocalFileAsync(config, message, package.LocalPath,
                package.ProviderName, package.MediaId, preferBase64: config.FileProtocol == 0).ConfigureAwait(false);
            global::Shirobot.Plugin.MyParser.DiagnosticLog.Info($"MyParser 音频已改为上传文件: media_id={package.MediaId}, {upload}");
            return Log(package, VoiceDeliveryLevel.File);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            BotLog.Warning($"MyParser {package.ProviderName} 音频文件上传失败: media_id={package.MediaId}, error={ex.Message}");
            throw lastError is null ? ex : new AggregateException("语音和文件都发送失败。", lastError, ex);
        }
    }

    /// <summary>编码 Silk 语音（可能分成多段）。</summary>
    public async Task<IReadOnlyList<RecordOutgoingSegment>> EncodeSilkAsync(VoicePackage package, CancellationToken cancellationToken)
    {
        var variants = await host.BuildSilkRecordVariantsAsync(config,
            new ProviderRecordBuildRequest(package.ProviderId, package.ProviderName, package.MediaId, package.LocalPath, package.FileNamePrefix),
            cancellationToken).ConfigureAwait(false);
        var records = new List<RecordOutgoingSegment>();
        foreach (var variant in variants)
            records.Add(new RecordOutgoingSegment(await host.BuildRecordUriAsync(variant.Path, cancellationToken).ConfigureAwait(false)));
        return records;
    }

    private async Task SendVoiceAsync(MessageEvent message, VoicePackage package, IReadOnlyList<RecordOutgoingSegment> records)
    {
        if (records.Count == 0) throw new InvalidOperationException("没有可发送的语音。");
        var delivered = 0;
        foreach (var record in records)
        {
            try
            {
                var sent = await host.SendSegmentsAsync(message, [record]).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(sent.MessageId)) throw new InvalidOperationException("语音发送没有返回消息 ID。");
                package.OnVoiceSent?.Invoke(sent.MessageId);
                delivered++;
            }
            // 已经发出一段时不再往下兜底，免得同一首歌又以文件形式发一遍。
            catch (Exception ex) when (delivered > 0 && ex is not OperationCanceledException)
            {
                BotLog.Warning($"MyParser {package.ProviderName} 分段语音部分发送失败: media_id={package.MediaId}, sent={delivered}/{records.Count}, error={ex.Message}");
                return;
            }
        }
    }

    private static bool IsQq(MessageEvent message) => string.Equals(message.Platform, "qq", StringComparison.OrdinalIgnoreCase);

    private static VoiceDeliveryLevel Log(VoicePackage package, VoiceDeliveryLevel level)
    {
        global::Shirobot.Plugin.MyParser.DiagnosticLog.Info($"MyParser 语音投递: media_id={package.MediaId}, level={level}");
        return level;
    }
}
