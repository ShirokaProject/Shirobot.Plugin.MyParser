using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using SharpMP4.Audio;
using SharpMP4.Builders;
using Shirobot.Plugin.MyParser;

internal static class AudioMixVerification
{
    public static async Task RunAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), "myparser-managed-mix-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var runtime = typeof(PluginConfig).Assembly.GetType("Shirobot.Plugin.MyParser.MyParserRuntime", true)!;
        var factory = runtime.GetProperty("TemporaryDirectoryFactory")!;
        var previousFactory = factory.GetValue(null);
        factory.SetValue(null, new Func<ShiroBot.SDK.Plugin.TemporaryDirectory>(() => new(directory, DateTimeOffset.UtcNow.AddHours(1))));
        try
        {
            var video = Path.Combine(directory, "voice.mp4");
            var longMusic = Path.Combine(directory, "music.mp3");
            var shortMusic = Path.Combine(directory, "short.mp3");
            var aacMusic = Path.Combine(directory, "music.m4a");
            await Run("ffmpeg", "-v", "error", "-f", "lavfi", "-i", "testsrc2=size=160x120:rate=30:duration=1", "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000:duration=1", "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac", "-shortest", video);
            await Run("ffmpeg", "-v", "error", "-f", "lavfi", "-i", "sine=frequency=880:sample_rate=44100:duration=5", "-c:a", "libmp3lame", longMusic);
            await Run("ffmpeg", "-v", "error", "-f", "lavfi", "-i", "sine=frequency=880:sample_rate=22050:duration=0.4", "-c:a", "libmp3lame", shortMusic);
            await Run("ffmpeg", "-v", "error", "-i", longMusic, "-c:a", "aac", aacMusic);
            foreach (var (music, loops) in new[] {(longMusic, 3), (shortMusic, 3), (aacMusic, 3), (longMusic, 1)})
            {
                var output = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".mp4");
                var timer = Stopwatch.StartNew();
                using (var visual = File.OpenRead(video))
                using (var audio = File.OpenRead(music))
                using (var target = File.Create(output))
                    LoopingVideoAudioMuxer.MuxMixingEmbeddedAudio(visual, audio, target, loopCount: loops);
                var elapsed = timer.Elapsed.TotalSeconds;
                using var probe = JsonDocument.Parse(await Run("ffprobe", "-v", "error", "-count_frames", "-show_streams", "-of", "json", output));
                var streams = probe.RootElement.GetProperty("streams").EnumerateArray().ToArray();
                var sound = streams.Single(s => s.GetProperty("codec_type").GetString() == "audio");
                if (sound.GetProperty("codec_name").GetString() != "aac") throw new Exception("Mixed output is not AAC.");
                var picture = streams.Single(s => s.GetProperty("codec_type").GetString() == "video");
                using var musicProbe = JsonDocument.Parse(await Run("ffprobe", "-v", "error", "-show_format", "-of", "json", music));
                var musicDuration = double.Parse(musicProbe.RootElement.GetProperty("format").GetProperty("duration").GetString()!, CultureInfo.InvariantCulture);
                var expectedDuration = Math.Max(loops, musicDuration);
                var outputDuration = double.Parse(sound.GetProperty("duration").GetString()!, CultureInfo.InvariantCulture);
                if (Math.Abs(outputDuration - expectedDuration) > .08) throw new Exception("Background music was truncated or output duration changed.");
                if (Math.Abs(double.Parse(picture.GetProperty("duration").GetString()!, CultureInfo.InvariantCulture) - outputDuration) > .03)
                    throw new Exception("Picture ended before the soundtrack.");
                if (int.Parse(picture.GetProperty("nb_read_frames").GetString()!) < loops * 30) throw new Exception("Minimum video looping changed.");
                var decoded = Path.Combine(directory, "decoded.f32");
                await Run("ffmpeg", "-y", "-v", "error", "-i", output, "-map", "0:a:0", "-ar", "48000", "-ac", "1", "-f", "f32le", decoded);
                var data = File.ReadAllBytes(decoded);
                var pcm = new float[data.Length / 4]; Buffer.BlockCopy(data, 0, pcm, 0, data.Length);
                var voice = Magnitude(pcm, 440, .15); var bg = Magnitude(pcm, 880, .15);
                Console.WriteLine($"managed mix: {Path.GetFileName(music)}, loops={loops}, voice={voice:F5}, music={bg:F5}, elapsed={elapsed:F3}s");
                if (Math.Abs(voice - .0625) > .008 || Math.Abs(bg - .0625) > .008) throw new Exception("Both source tones must survive at half gain without amplification.");
                if (loops == 3)
                {
                    if (Magnitude(pcm, 440, 2.6) > .005) throw new Exception("Original audio repeated after its first pass.");
                    if (music != shortMusic && Magnitude(pcm, 880, 2.6) < .04) throw new Exception("Background audio was lost later in the video.");
                    if (music == shortMusic && Magnitude(pcm, 880, 2.6) > .005) throw new Exception("Short background music was repeated instead of padded.");
                }
                if (music != shortMusic)
                {
                    if (Magnitude(pcm, 440, 4.5) > .005) throw new Exception("Original audio repeated while music continued.");
                    if (Magnitude(pcm, 880, 4.5) < .04) throw new Exception("Background music did not play to its end.");
                }
                // Video packets must be copied unchanged, not re-encoded.
                using var sourcePackets = JsonDocument.Parse(await Run("ffprobe", "-v", "error", "-select_streams", "v:0", "-show_packets", "-show_data_hash", "sha256", "-of", "json", video));
                using var outputPackets = JsonDocument.Parse(await Run("ffprobe", "-v", "error", "-select_streams", "v:0", "-show_packets", "-show_data_hash", "sha256", "-of", "json", output));
                var expected = sourcePackets.RootElement.GetProperty("packets").EnumerateArray().Select(p => p.GetProperty("data_hash").GetString()).ToArray();
                var actual = outputPackets.RootElement.GetProperty("packets").EnumerateArray().Select(p => p.GetProperty("data_hash").GetString()).ToArray();
                if (actual.Where((hash, i) => hash != expected[i % expected.Length]).Any()) throw new Exception("Video packets changed.");
            }
            // The production entry point must also mix without FFmpeg on PATH.
            var serviceType = typeof(MyParser.Provider.Douyin.Parsing.DouyinParser).Assembly.GetType("Shirobot.Plugin.MyParser.Downloading.ProviderDownloadService", true)!;
            var path = Environment.GetEnvironmentVariable("PATH");
            try
            {
                Environment.SetEnvironmentVariable("PATH", "");
                await (Task<string>)serviceType.GetMethod("MuxLoopingVideoWithAudioAsync")!.Invoke(Activator.CreateInstance(serviceType), [new PluginConfig(), video, longMusic, CancellationToken.None, 3])!;
            }
            finally { Environment.SetEnvironmentVariable("PATH", path); }
            var encoder = new ManagedAacEncoder(); var frame = new float[2048]; var packet = new byte[1536];
            for (int i = 0; i < frame.Length; i++) frame[i] = .1f * MathF.Sin(i * .03f);
            for (int i = 0; i < 20; i++) encoder.Encode(frame, packet);
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 100; i++) encoder.Encode(frame, packet);
            if (GC.GetAllocatedBytesForCurrentThread() != before) throw new Exception("AAC encoding allocated per frame.");
            Console.WriteLine("Managed mixing, resampling, AAC playback, video-copy, loop timing, zero encoder allocations and no-FFmpeg host checks passed.");
        }
        finally { factory.SetValue(null, previousFactory); Directory.Delete(directory, true); }
    }
    private static double Magnitude(float[] pcm, double frequency, double start)
    {
        int offset = (int)(start * 48000), length = 4800;
        double real = 0, imaginary = 0;
        for (int i = 0; i < length; i++)
        {
            double phase = 2 * Math.PI * frequency * i / 48000;
            real += pcm[offset + i] * Math.Cos(phase); imaginary += pcm[offset + i] * Math.Sin(phase);
        }
        return 2 * Math.Sqrt(real * real + imaginary * imaginary) / length;
    }
    private static async Task<string> Run(string tool, params string[] args)
    {
        var info = new ProcessStartInfo(tool) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var arg in args) info.ArgumentList.Add(arg);
        using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0) throw new Exception(await error);
        return await output;
    }
}
