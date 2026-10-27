using Modularyn.Framework.Tenancy.Abstractions;
using Modularyn.Modules.Files.Domain;

namespace Modularyn.Modules.Files.Persistence.FileSystem;

/// <summary>A read-only, nonseekable interval over a private verified snapshot.</summary>
internal sealed class VerifiedRangeStream(FileStream snapshot, long length, TenantContext tenant) : Stream
{
    private long position;
    private bool disposed;
    public override bool CanRead => !disposed;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => length;
    public override long Position { get => position; set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        Require();
        var read = snapshot.Read(buffer[..(int)Math.Min(buffer.Length, length - position)]);
        position += read;
        return read;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        Require();
        cancellationToken.ThrowIfCancellationRequested();
        var read = await snapshot.ReadAsync(buffer[..(int)Math.Min(buffer.Length, length - position)], cancellationToken).ConfigureAwait(false);
        position += read;
        return read;
    }

    private void Require()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        FileTenant.Require(tenant);
    }

    public override void Flush() => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing && !disposed) { disposed = true; snapshot.Dispose(); }
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        if (!disposed) { disposed = true; await snapshot.DisposeAsync().ConfigureAwait(false); }
        GC.SuppressFinalize(this);
    }
}
