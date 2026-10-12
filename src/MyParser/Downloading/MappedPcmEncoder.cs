using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using SilkCodec.NET;

namespace Shirobot.Plugin.MyParser.Downloading;

/// <summary>Provides a temporary PCM file to the codec without retaining a full-track managed array.</summary>
internal static class MappedPcmEncoder
{
    public static unsafe void Encode(SilkEncoder encoder, FileStream pcm, Stream silk, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (pcm.Length <= 0 || (pcm.Length & 1) != 0 || pcm.Length > int.MaxValue)
            throw new InvalidDataException("PCM 长度无效或超过单次编码可处理的长度。");
        var length = checked((int)pcm.Length);
        using var mapping = MemoryMappedFile.CreateFromFile(pcm, null, 0, MemoryMappedFileAccess.Read,
            HandleInheritability.None, leaveOpen: true);
        using var view = mapping.CreateViewAccessor(0, length, MemoryMappedFileAccess.Read);
        byte* pointer = null;
        view.SafeMemoryMappedViewHandle.AcquirePointer(ref pointer);
        try
        {
            var bytes = new ReadOnlySpan<byte>(pointer + view.PointerOffset, length);
            using var output = new CancellationCheckingWriteStream(silk, cancellationToken);
            if (BitConverter.IsLittleEndian)
                encoder.Encode(MemoryMarshal.Cast<byte, short>(bytes), output);
            else
            {
                var samples = new short[length / 2];
                for (var i = 0; i < samples.Length; i++)
                    samples[i] = System.Buffers.Binary.BinaryPrimitives.ReadInt16LittleEndian(bytes.Slice(i * 2, 2));
                encoder.Encode(samples, output);
            }
        }
        finally { view.SafeMemoryMappedViewHandle.ReleasePointer(); }
    }
}
