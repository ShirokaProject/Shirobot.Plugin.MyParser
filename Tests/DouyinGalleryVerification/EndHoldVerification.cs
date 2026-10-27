using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using Shirobot.Plugin.MyParser;
using ShiroBot.SDK.Plugin;

internal static class EndHoldVerification
{
    public static async Task RunAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), "myparser-end-hold-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var runtime = typeof(PluginConfig).Assembly.GetType("Shirobot.Plugin.MyParser.MyParserRuntime", true)!;
        var factory = runtime.GetProperty("TemporaryDirectoryFactory")!;
        var previous = factory.GetValue(null);
        factory.SetValue(null, new Func<TemporaryDirectory>(() => new(directory, DateTimeOffset.UtcNow.AddHours(1))));
        var assembly = typeof(MyParserPlugin).Assembly;
        var sharp = assembly.GetType("Shirobot.Plugin.MyParser.Downloading.SharpMp4MediaService", true)!;
        var service = assembly.GetType("Shirobot.Plugin.MyParser.Downloading.ProviderDownloadService", true)!;
        try
        {
            foreach (var bframes in new[] { 0, 3 })
            {
                var source = Path.Combine(directory, $"source-{bframes}.mp4");
                var held = Path.Combine(directory, $"held-{bframes}.mp4");
                await Run("ffmpeg", "-v", "error", "-f", "lavfi", "-i", "testsrc2=size=128x96:rate=10:duration=1",
                    "-f", "lavfi", "-i", "sine=frequency=440:duration=1", "-c:v", "libx264", "-bf", bframes.ToString(), "-pix_fmt", "yuv420p", "-c:a", "aac", source);
                sharp.GetMethod("HoldLastFrame")!.Invoke(null, [source, held, 0.8, CancellationToken.None]);
                var originalFrames = await Frames(source);
                var heldFrames = await Frames(held);
                Check(heldFrames.Length == originalFrames.Length, "Native hold must not duplicate compressed frames.");
                for (var i = 0; i < originalFrames.Length; i++)
                    Check(Math.Abs(Number(originalFrames[i], "pts_time") - Number(heldFrames[i], "pts_time")) < .001, "Display timestamps changed.");
                var tailDuration = Number(heldFrames.Last(), "duration_time");
                Check(tailDuration > .85, $"Final displayed frame must hold: {tailDuration}");
                Check(await Hashes(source, "v") == await Hashes(held, "v"), "Video data changed.");
                Check(await Hashes(source, "a") == await Hashes(held, "a"), "Audio data changed.");
                await Run("ffmpeg", "-v", "error", "-i", held, "-f", "null", "-");
                foreach (var loops in new[] { 1, 3 })
                {
                    var originalPath = Environment.GetEnvironmentVariable("PATH");
                    string output;
                    try
                    {
                        Environment.SetEnvironmentVariable("PATH", "");
                        output = await (Task<string>)service.GetMethod("MuxLoopingVideoWithAudioAsync")!.Invoke(Activator.CreateInstance(service),
                            [new PluginConfig(), source, null, CancellationToken.None, loops])!;
                    }
                    finally { Environment.SetEnvironmentVariable("PATH", originalPath); }
                    var baseline = Path.Combine(directory, $"baseline-{bframes}-{loops}.mp4");
                    sharp.GetMethod("MuxLoopingVideoWithAudio")!.Invoke(null, [source, null, baseline, CancellationToken.None, loops]);
                    Check(Math.Abs(await Duration(output) - await Duration(baseline) - .8) < .05,
                        $"Production entry point missed the final hold: output={await Duration(output)}, baseline={await Duration(baseline)}.");
                    await Run("ffmpeg", "-v", "error", "-i", output, "-f", "null", "-");
                }
                Console.WriteLine($"PASS: B-frames={bframes}, final frame held, PTS and media data preserved, native playback with 1/3 loops.");
            }
            // An MP4 beginning between keyframes forces the external fallback.
            var transport = Path.Combine(directory, "fallback.mp4");
            await Run("ffmpeg", "-v", "error", "-i", Path.Combine(directory, "source-0.mp4"), "-ss", "0.1", "-c", "copy", "-copyinkf", transport);
            var fallback = await (Task<string>)service.GetMethod("MuxLoopingVideoWithAudioAsync")!.Invoke(Activator.CreateInstance(service),
                [new PluginConfig { FfmpegPath = "ffmpeg" }, transport, null, CancellationToken.None, 1])!;
            Check(Math.Abs(await Duration(fallback) - (await Duration(transport) + .8)) < .15, "FFmpeg fallback missed the hold.");
            await Run("ffmpeg", "-v", "error", "-i", fallback, "-f", "null", "-");
            Console.WriteLine("PASS: FFmpeg fallback ends with a cloned final frame.");
        }
        finally { factory.SetValue(null, previous); Directory.Delete(directory, true); }
    }
    private static async Task<JsonElement[]> Frames(string path)
    {
        using var doc = JsonDocument.Parse(await Run("ffprobe", "-v", "error", "-select_streams", "v:0", "-show_frames", "-of", "json", path));
        return doc.RootElement.GetProperty("frames").EnumerateArray().Select(e => e.Clone()).ToArray();
    }
    private static async Task<string> Hashes(string path, string stream)
    {
        using var doc = JsonDocument.Parse(await Run("ffprobe", "-v", "error", "-select_streams", stream + ":0", "-show_packets", "-show_data_hash", "sha256", "-of", "json", path));
        return string.Join("|", doc.RootElement.GetProperty("packets").EnumerateArray().Select(e => e.GetProperty("data_hash").GetString()));
    }
    private static async Task<double> Duration(string path)
    {
        using var doc = JsonDocument.Parse(await Run("ffprobe", "-v", "error", "-select_streams", "v:0", "-show_streams", "-of", "json", path));
        return Number(doc.RootElement.GetProperty("streams")[0], "duration");
    }
    private static double Number(JsonElement value, string property) => double.Parse(value.GetProperty(property).GetString()!, CultureInfo.InvariantCulture);
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static async Task<string> Run(string tool, params string[] args)
    {
        var info = new ProcessStartInfo(tool) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in args) info.ArgumentList.Add(argument);
        using var process = Process.Start(info)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0) throw new Exception(await stderr);
        return await stdout;
    }
}
