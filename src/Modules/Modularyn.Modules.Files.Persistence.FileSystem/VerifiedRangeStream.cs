using Modularyn.Framework.Tenancy.Abstractions;
using Modularyn.Modules.Files.Domain;

namespace Modularyn.Modules.Files.Persistence.FileSystem;

/// <summary>A read-only, nonseekable interval over a verified snapshot in a held read slot, which disposal frees.</summary>
internal sealed class VerifiedRangeStream(FileStream slot, long length, TenantContext tenant) : Stream
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
        var read = slot.Read(buffer[..(int)Math.Min(buffer.Length, length - position)]);
        position += read;
        return read;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        Require();
        cancellationToken.ThrowIfCancellationRequested();
        var read = await slot.ReadAsync(buffer[..(int)Math.Min(buffer.Length, length - position)], cancellationToken).ConfigureAwait(false);
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
        if (disposing && !disposed) { disposed = true; Release(slot); }
        base.Dispose(disposing);
    }

    public override ValueTask DisposeAsync()
    {
        if (!disposed) { disposed = true; Release(slot); }
        GC.SuppressFinalize(this);
        return ValueTask.CompletedTask;
    }

    /// <summary>Empties the slot and closes it, which frees it for the tenant's next reader.</summary>
    internal static void Release(FileStream slot)
    {
        try { slot.SetLength(0); }
        catch (IOException)
        {
            // The slot keeps its bytes until its next reader or recovery truncates it; its count still bounds the tenant.
        }
        finally { slot.Dispose(); }
    }
}
