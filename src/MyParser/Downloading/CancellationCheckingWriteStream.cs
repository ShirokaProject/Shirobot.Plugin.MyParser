namespace Shirobot.Plugin.MyParser.Downloading;

/// <summary>Checks cancellation on each encoder packet; the caller owns the underlying stream.</summary>
internal sealed class CancellationCheckingWriteStream(Stream inner, CancellationToken cancellationToken) : Stream
{
    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => inner.CanWrite;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { cancellationToken.ThrowIfCancellationRequested(); inner.Flush(); }
    public override void Write(byte[] buffer, int offset, int count)
    { cancellationToken.ThrowIfCancellationRequested(); inner.Write(buffer, offset, count); }
    public override void Write(ReadOnlySpan<byte> buffer)
    { cancellationToken.ThrowIfCancellationRequested(); inner.Write(buffer); }
    public override void WriteByte(byte value)
    { cancellationToken.ThrowIfCancellationRequested(); inner.WriteByte(value); }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}
