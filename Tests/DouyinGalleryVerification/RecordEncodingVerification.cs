using System.Reflection;
using ShiroBot.SDK.Plugin;
using Shirobot.Plugin.MyParser;
using Shirobot.Plugin.MyParser.Parsing;

internal static class RecordEncodingVerification
{
    public static async Task RunAsync(string input)
    {
        var runtime = typeof(PluginConfig).Assembly.GetType("Shirobot.Plugin.MyParser.MyParserRuntime", true)!;
        var factory = runtime.GetProperty("TemporaryDirectoryFactory")!;
        var previous = factory.GetValue(null);
        var root = Path.Combine(Path.GetTempPath(), "myparser-record-check-" + Guid.NewGuid().ToString("N"));
        factory.SetValue(null, (Func<TemporaryDirectory>)(() => {
            var path = Path.Combine(root, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return new TemporaryDirectory(path, DateTimeOffset.UtcNow.AddHours(1));
        }));
        try
        {
            var assembly = typeof(MyParser.Provider.Douyin.Parsing.DouyinParser).Assembly;
            var service = assembly.GetType("Shirobot.Plugin.MyParser.Downloading.ProviderDownloadService", true)!;
            var request = new ProviderRecordBuildRequest("douyin", "抖音", "verification", input, "record");
            var variants = await (Task<IReadOnlyList<ProviderRecordVariant>>)service.GetMethod("BuildSilkRecordVariantsAsync")!
                .Invoke(Activator.CreateInstance(service), [new PluginConfig(), request, CancellationToken.None])!;
            foreach (var variant in variants)
            {
                var data = File.ReadAllBytes(variant.Path);
                if (data.Length < 12 || data[0] != 2 || !data.AsSpan(1, 9).SequenceEqual("#!SILK_V3"u8))
                    throw new Exception("Invalid Tencent Silk header.");
                var cursor = 10; var packets = 0;
                while (cursor < data.Length)
                {
                    if (cursor + 2 > data.Length) throw new Exception("Truncated Silk packet header.");
                    var size = System.Buffers.Binary.BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(cursor, 2));
                    cursor += 2;
                    if (size <= 0 || cursor + size > data.Length) throw new Exception("Invalid Silk packet.");
                    cursor += size; packets++;
                }
                if (packets < 1) throw new Exception("Empty Silk output.");
                var config = new PluginConfig { FileProtocol = 1 };
                var hostType = assembly.GetType("Shirobot.Plugin.MyParser.ProviderHostServices", true)!;
                using var host = (IDisposable)Activator.CreateInstance(hostType, [null, config])!;
                var buildUri = hostType.GetMethod("BuildRecordUriAsync")!;
                var fileUri = await (Task<string>)buildUri.Invoke(host, [variant.Path, CancellationToken.None])!;
                if (!fileUri.StartsWith("file://") || new Uri(fileUri).LocalPath != variant.Path)
                    throw new Exception("Voice ignored File protocol.");
                config.FileProtocol = 0;
                var base64Uri = await (Task<string>)buildUri.Invoke(host, [variant.Path, CancellationToken.None])!;
                if (!base64Uri.StartsWith("base64://") || !Convert.FromBase64String(base64Uri[9..]).SequenceEqual(data))
                    throw new Exception("Voice ignored Base64 protocol.");
                Console.WriteLine($"Tencent Silk encoding passed: bytes={data.Length}, packets={packets}, duration={packets * .02:F2}s");
            }
        }
        finally { factory.SetValue(null, previous); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
