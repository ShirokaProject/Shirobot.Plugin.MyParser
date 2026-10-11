using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using Shirobot.Plugin.MyParser;
using ShiroBot.SDK.Plugin;

internal static class LoopMuxVerification
{
    internal static async Task RunAsync(string? sourceVideo = null, string? sourceAudio = null)
    {
        var directory = Path.Combine(Path.GetTempPath(), "myparser-loop-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var outputs = new List<string>();
        var factory = typeof(PluginConfig).Assembly.GetType("Shirobot.Plugin.MyParser.MyParserRuntime", true)!
            .GetProperty("TemporaryDirectoryFactory")!;
        var previousFactory = factory.GetValue(null);
        factory.SetValue(null, (Func<TemporaryDirectory>)(() =>
        {
            var path = Path.Combine(directory, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return new TemporaryDirectory(path, DateTimeOffset.UtcNow.AddHours(1));
        }));
        try
        {
            var video = Path.Combine(directory, "clip.mp4");
            var shortMp3 = Path.Combine(directory, "short.mp3");
            var longMp3 = Path.Combine(directory, "long.mp3");
            var aac = Path.Combine(directory, "audio.m4a");
            var vbr = Path.Combine(directory, "vbr.mp3");
            var lowRate = Path.Combine(directory, "mpeg25.mp3");
            var stereo = Path.Combine(directory, "stereo.mp3");
            if (sourceVideo is not null) File.Copy(sourceVideo, video);
            else await RunAsync("ffmpeg", "-v", "error", "-f", "lavfi", "-i", "testsrc2=size=64x64:rate=10:duration=1", "-c:v", "libx264", "-pix_fmt", "yuv420p", video);
            using var original = JsonDocument.Parse(await RunAsync("ffprobe", "-v", "error", "-count_frames", "-select_streams", "v:0", "-show_streams", "-of", "json", video));
            var originalStream = original.RootElement.GetProperty("streams")[0];
            var originalFrames = int.Parse(originalStream.GetProperty("nb_read_frames").GetString()!, CultureInfo.InvariantCulture);
            var originalDuration = double.Parse(originalStream.GetProperty("duration").GetString()!, CultureInfo.InvariantCulture);
            using var originalAudio = JsonDocument.Parse(await RunAsync("ffprobe", "-v", "error", "-select_streams", "a:0", "-show_streams", "-of", "json", video));
            var hasEmbeddedAudio = originalAudio.RootElement.GetProperty("streams").GetArrayLength() > 0;
            await RunAsync("ffmpeg", "-v", "error", "-f", "lavfi", "-i", "sine=frequency=440:duration=0.5", shortMp3);
            await RunAsync("ffmpeg", "-v", "error", "-f", "lavfi", "-i", "sine=frequency=440:duration=5", longMp3);
            await RunAsync("ffmpeg", "-v", "error", "-i", longMp3, "-c:a", "aac", aac);
            await RunAsync("ffmpeg", "-v", "error", "-f", "lavfi", "-i", "sine=frequency=440:duration=5", "-ar", "22050", "-q:a", "4", vbr);
            await RunAsync("ffmpeg", "-v", "error", "-f", "lavfi", "-i", "sine=frequency=440:duration=5", "-ar", "8000", "-b:a", "16k", lowRate);
            await RunAsync("ffmpeg", "-v", "error", "-f", "lavfi", "-i", "sine=frequency=440:duration=5", "-ar", "48000", "-ac", "2", "-b:a", "128k", "-id3v2_version", "0", "-write_xing", "0", stereo);
            var assembly = typeof(MyParser.Provider.Douyin.Parsing.DouyinParser).Assembly;
            var type = assembly.GetType("Shirobot.Plugin.MyParser.Downloading.ProviderDownloadService", true)!;
            var service = Activator.CreateInstance(type)!;
            var method = type.GetMethod("MuxLoopingVideoWithAudioAsync")!;
            var scenarios = new List<(string? Audio, int Loops)> { (shortMp3, 3), (longMp3, 3), (vbr, 3), (lowRate, 3), (stereo, 3), (aac, 3), (null, 3), (longMp3, 1), (null, 1) };
            if (sourceAudio is not null) scenarios.Add((sourceAudio, 3));
            foreach (var (audio, loops) in scenarios)
            {
                // Call SharpMP4 directly; an external fallback must not mask a regression.
                var path = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".mp4");
                assembly.GetType("Shirobot.Plugin.MyParser.Downloading.SharpMp4MediaService", true)!
                    .GetMethod("MuxLoopingVideoWithAudio")!.Invoke(null, [video, audio, path, CancellationToken.None, loops]);
                if (audio == longMp3 && loops == 3 && !hasEmbeddedAudio)
                {
                    // Verify the plugin entry point with no external executable on PATH.
                    var originalSearchPath = Environment.GetEnvironmentVariable("PATH");
                    try
                    {
                        Environment.SetEnvironmentVariable("PATH", "");
                        var hosted = await (Task<string>)method.Invoke(service,
                            [new PluginConfig(), video, audio, CancellationToken.None, loops])!;
                        outputs.Add(hosted);
                    }
                    finally { Environment.SetEnvironmentVariable("PATH", originalSearchPath); }
                }
                outputs.Add(path);
                using var probe = JsonDocument.Parse(await RunAsync("ffprobe", "-v", "error", "-count_frames", "-show_streams", "-of", "json", path));
                var streams = probe.RootElement.GetProperty("streams").EnumerateArray().ToArray();
                var visual = streams.Single(s => s.GetProperty("codec_type").GetString() == "video");
                var frames = int.Parse(visual.GetProperty("nb_read_frames").GetString()!, CultureInfo.InvariantCulture);
                var duration = double.Parse(visual.GetProperty("duration").GetString()!, CultureInfo.InvariantCulture);
                if (frames != loops * originalFrames || Math.Abs(duration - loops * originalDuration) > .1) throw new Exception($"Incorrect looping: frames={frames}, duration={duration}, loops={loops}");
                if (streams.Count(s => s.GetProperty("codec_type").GetString() == "audio") != (audio is null && !hasEmbeddedAudio ? 0 : 1)) throw new Exception("Incorrect audio track count.");
                if (hasEmbeddedAudio && audio is null || !hasEmbeddedAudio && audio is not null && Path.GetExtension(audio) == ".mp3")
                {
                    var sound = streams.Single(s => s.GetProperty("codec_type").GetString() == "audio");
                    var expectedCodec = hasEmbeddedAudio ? originalAudio.RootElement.GetProperty("streams")[0].GetProperty("codec_name").GetString() : "mp3";
                    if (sound.GetProperty("codec_name").GetString() != expectedCodec) throw new Exception("Original audio was replaced or re-encoded.");
                    if (hasEmbeddedAudio && Math.Abs(double.Parse(sound.GetProperty("duration").GetString()!, CultureInfo.InvariantCulture) - duration) > .1)
                        throw new Exception("Embedded audio did not loop with the video.");
                    using var sourcePackets = JsonDocument.Parse(await RunAsync("ffprobe", "-v", "error", "-select_streams", "a:0", "-show_packets", "-show_data_hash", "sha256", "-of", "json", hasEmbeddedAudio ? video : audio!));
                    using var targetPackets = JsonDocument.Parse(await RunAsync("ffprobe", "-v", "error", "-select_streams", "a:0", "-show_packets", "-show_data_hash", "sha256", "-of", "json", path));
                    var expected = sourcePackets.RootElement.GetProperty("packets").EnumerateArray().Select(p => p.GetProperty("data_hash").GetString()).ToArray();
                    var actual = targetPackets.RootElement.GetProperty("packets").EnumerateArray().Select(p => p.GetProperty("data_hash").GetString()).ToArray();
                    var expectedOutput = hasEmbeddedAudio ? Enumerable.Range(0, actual.Length).Select(index => expected[index % expected.Length]) : expected.Take(actual.Length);
                    if (actual.Length == 0 || !actual.SequenceEqual(expectedOutput)) throw new Exception("Audio frames were not copied intact.");
                }
                await RunAsync("ffmpeg", "-v", "error", "-i", path, "-f", "null", "-");
                Console.WriteLine($"Loop mux verified: audio={Path.GetFileName(audio) ?? "none"}, loops={loops}, frames={frames}, duration={duration}");
            }
            using var cancel = new CancellationTokenSource();
            cancel.Cancel();
            try
            {
                await (Task<string>)method.Invoke(service, [new PluginConfig { FfmpegPath = "ffmpeg" }, video, aac, cancel.Token, 3])!;
                throw new Exception("Cancelled mux unexpectedly succeeded.");
            }
            catch (OperationCanceledException) { }
        }
        finally
        {
            factory.SetValue(null, previousFactory);
            foreach (var output in outputs) File.Delete(output);
            Directory.Delete(directory, true);
        }
    }

    private static async Task<string> RunAsync(string tool, params string[] args)
    {
        var info = new ProcessStartInfo(tool) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var arg in args) info.ArgumentList.Add(arg);
        using var process = Process.Start(info)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0) throw new Exception($"{tool} failed: {await stderr}");
        return await stdout;
    }
}
