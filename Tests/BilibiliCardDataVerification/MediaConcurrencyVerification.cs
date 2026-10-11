using System.Reflection;
using ShiroBot.SDK.Models;
using Shirobot.Plugin.MyParser;
using Shirobot.Plugin.MyParser.Parsing;

internal static class MediaConcurrencyVerification
{
    internal static async Task RunAsync(Type handlerType, MessageEvent message, IParseProvider provider, ParsedMedia media)
    {
        foreach (var scenario in new[] { "success", "cancel", "cover-failure", "preview", "disabled", "audio-success", "audio-cancel" })
        {
            var isAudio = scenario.StartsWith("audio-");
            var deliveryName = isAudio ? "audio" : "video";
            using var cancellation = new CancellationTokenSource();
            var downloadStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseDownload = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var downloadStopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var services = DispatchProxy.Create<IProviderHostServices, RecordingHostServices>();
            var recorder = (RecordingHostServices)services;
            recorder.FailImage = scenario == "cover-failure";
            recorder.Download = async token =>
            {
                downloadStarted.SetResult();
                try
                {
                    await releaseDownload.Task.WaitAsync(token);
                    return ("file:///verification.mp4", "/verification.mp4");
                }
                finally { downloadStopped.SetResult(); }
            };
            var renderer = new BlockingCardRenderer();
            var config = new PluginConfig { SendVideoSegment = scenario != "disabled", EnableSilkEncoding = false };
            var presenter = Activator.CreateInstance(handlerType,
                [new ProviderMessageHandlerContext(null!, config, provider, services, renderer)])!;
            var input = media with
            {
                Kind = isAudio ? ParsedMediaKind.Track : media.Kind,
                CoverUrl = "https://example.com/cover.jpg",
                Attributes = scenario == "preview" ? new Dictionary<string, string> { ["preview_only"] = "true" } : media.Attributes,
            };
            var presentation = (Task)handlerType.GetMethod("PresentAsync", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(presenter, [message, input, false, cancellation.Token])!;
            await renderer.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (scenario is "preview" or "disabled")
            {
                if (downloadStarted.Task.IsCompleted) throw new Exception("Preview/disabled delivery started a video download.");
                renderer.Release.SetResult();
                await presentation.WaitAsync(TimeSpan.FromSeconds(5));
                continue;
            }
            await downloadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (scenario is "cancel" or "audio-cancel")
            {
                cancellation.Cancel();
                try { await presentation.WaitAsync(TimeSpan.FromSeconds(5)); throw new Exception("Cancellation was ignored."); }
                catch (OperationCanceledException) { }
            }
            else if (scenario == "cover-failure")
            {
                renderer.Release.SetResult();
                try { await presentation.WaitAsync(TimeSpan.FromSeconds(5)); throw new Exception("Cover send failure was swallowed."); }
                catch (IOException ex) when (ex.Message == "image-send-failed") { }
            }
            else
            {
                releaseDownload.SetResult();
                await downloadStopped.Task.WaitAsync(TimeSpan.FromSeconds(5));
                if (recorder.Messages.Contains(deliveryName)) throw new Exception("Video sent before the cover.");
                renderer.Release.SetResult();
                await presentation.WaitAsync(TimeSpan.FromSeconds(5));
                if (!recorder.Messages.SequenceEqual(new[] { "download", "image", deliveryName }))
                    throw new Exception("Parallel preparation changed message order or duplicated delivery.");
            }
            await downloadStopped.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (scenario is not ("success" or "audio-success") && recorder.Messages.Contains(deliveryName)) throw new Exception("Cancelled preparation sent a video.");
        }
        Console.WriteLine("Concurrent cover/video/audio preparation, message order, cancellation and disabled-download verification passed.");
    }

    private sealed class BlockingCardRenderer : IProviderMediaCardRenderer
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<string?> RenderAsync(ParsedMedia media, ProviderCardPurpose purpose, CancellationToken cancellationToken = default)
        {
            Started.SetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return "base64://preview";
        }
    }
}
