using NLayer;

namespace Shirobot.Plugin.MyParser.Services;

internal static class ManagedMp3PcmConverter
{
    private const int TargetSampleRate = 48000;

    public static void ConvertToMonoS16Le(string inputPath, string outputPath, CancellationToken cancellationToken)
    {
        using var mpeg = new MpegFile(inputPath)
        {
            StereoMode = StereoMode.DownmixToMono,
        };
        if (mpeg.SampleRate <= 0)
        {
            throw new InvalidDataException("MP3 采样率无效。");
        }

        var reader = new MonoSampleReader(mpeg);
        if (!reader.TryRead(out var left))
        {
            throw new InvalidDataException("MP3 未解码出 PCM 样本。");
        }

        using var output = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.Read);
        var writer = new Pcm16Writer(output);
        var sourceStep = mpeg.SampleRate / (double)TargetSampleRate;
        var sourcePosition = 0d;
        long leftIndex = 0;
        var hasRight = reader.TryRead(out var right);

        while (hasRight)
        {
            cancellationToken.ThrowIfCancellationRequested();
            while (sourcePosition < leftIndex + 1d)
            {
                var fraction = sourcePosition - leftIndex;
                writer.Write(left + (right - left) * fraction);
                sourcePosition += sourceStep;
            }

            left = right;
            leftIndex++;
            hasRight = reader.TryRead(out right);
        }

        if (sourcePosition <= leftIndex)
        {
            writer.Write(left);
        }

        writer.Flush();
        if (output.Length == 0)
        {
            throw new InvalidDataException("MP3 转换后的 PCM 为空。");
        }
    }

    private sealed class MonoSampleReader(MpegFile mpeg)
    {
        private readonly float[] buffer = new float[8192];
        private int offset;
        private int count;

        public bool TryRead(out float sample)
        {
            if (offset >= count)
            {
                count = mpeg.ReadSamples(buffer, 0, buffer.Length);
                offset = 0;
                if (count == 0)
                {
                    sample = 0;
                    return false;
                }
            }

            sample = buffer[offset++];
            return true;
        }
    }

    private sealed class Pcm16Writer(Stream output)
    {
        private readonly byte[] buffer = new byte[16 * 1024];
        private int offset;

        public void Write(double sample)
        {
            var value = (short)Math.Clamp(Math.Round(sample * short.MaxValue), short.MinValue, short.MaxValue);
            buffer[offset++] = (byte)value;
            buffer[offset++] = (byte)(value >> 8);
            if (offset == buffer.Length)
            {
                Flush();
            }
        }

        public void Flush()
        {
            if (offset == 0)
            {
                return;
            }

            output.Write(buffer, 0, offset);
            offset = 0;
        }
    }
}
