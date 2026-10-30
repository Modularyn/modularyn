namespace Modularyn.Modules.Files.Application;

/// <summary>The bytes already read for type detection, then the rest of the caller's stream, which it neither seeks nor disposes.</summary>
internal sealed class PrefixedStream(ReadOnlyMemory<byte> head, Stream rest) : Stream
{
    private int consumed;
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        if (consumed < head.Length) return FromHead(buffer);
        return rest.Read(buffer);
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (consumed < head.Length) return ValueTask.FromResult(FromHead(buffer.Span));
        return rest.ReadAsync(buffer, cancellationToken);
    }

    private int FromHead(Span<byte> buffer)
    {
        var count = Math.Min(buffer.Length, head.Length - consumed);
        head.Span.Slice(consumed, count).CopyTo(buffer);
        consumed += count;
        return count;
    }

    public override void Flush() => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
