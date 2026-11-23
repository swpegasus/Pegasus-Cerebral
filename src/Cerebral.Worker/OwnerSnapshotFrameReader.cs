using System.Buffers.Binary;

namespace Cerebral.Worker;

/// <summary>Observes only the existing protocol header; the frozen protocol still validates the original payload.</summary>
internal sealed class OwnerSnapshotFrameReader(Stream source) : Stream
{
    private readonly byte[] header = new byte[4];
    private int captured;
    internal int FrameBytes => captured == 4 ? BinaryPrimitives.ReadInt32LittleEndian(header) : 0;
    private void Observe(ReadOnlySpan<byte> bytes)
    {
        var count = Math.Min(4 - captured, bytes.Length);
        if (count <= 0) return;
        bytes[..count].CopyTo(header.AsSpan(captured)); captured += count;
    }
    public override int Read(byte[] buffer, int offset, int count)
    { var read = source.Read(buffer, offset, count); Observe(buffer.AsSpan(offset, read)); return read; }
    public override int Read(Span<byte> buffer)
    { var read = source.Read(buffer); Observe(buffer[..read]); return read; }
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
    { var read = await source.ReadAsync(buffer, token); Observe(buffer.Span[..read]); return read; }
    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token)
    { var read = await source.ReadAsync(buffer, offset, count, token); Observe(buffer.AsSpan(offset, read)); return read; }
    public override bool CanRead => source.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
