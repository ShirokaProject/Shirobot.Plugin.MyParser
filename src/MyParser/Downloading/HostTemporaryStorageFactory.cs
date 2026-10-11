using SharpISOBMFF;
using SharpMP4.Common;

namespace Shirobot.Plugin.MyParser.Downloading;

internal sealed class HostTemporaryStorageFactory : ITemporaryStorageFactory
{
    public IStorage Create(IMp4Logger? logger = null) => new Storage(logger);

    private sealed class Storage : IStorage
    {
        public Storage(IMp4Logger? logger)
        {
            Logger = logger ?? new DefaultMp4Logger();
            Stream = new FileStream(Path.Combine(MyParserRuntime.CreateTempDirectory(), "mp4-storage.bin"),
                FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read);
        }

        public Stream Stream { get; }
        public IMp4Logger Logger { get; set; }
        public void Dispose() => Stream.Dispose();
        public void Flush() => Stream.Flush();
        public long GetLength() => Stream.Length;
        public long GetPosition() => Stream.Position;
        public long SeekFromCurrent(long offset) => Stream.Seek(offset, SeekOrigin.Current);
        public long SeekFromEnd(long offset) => Stream.Seek(offset, SeekOrigin.End);
        public long SeekFromBeginning(long offset) => Stream.Seek(offset, SeekOrigin.Begin);
        public void ReadExactly(byte[] data, int offset, int length) => Stream.ReadExactly(data, offset, length);
        public void Write(byte[] buffer, int offset, int length) => Stream.Write(buffer, offset, length);
        public int ReadByte() => Stream.ReadByte();
        public void WriteByte(byte value) => Stream.WriteByte(value);
        public bool CanStreamSeek() => Stream.CanSeek;
        public int Read(byte[] buffer, int offset, int length) => Stream.Read(buffer, offset, length);
    }
}
