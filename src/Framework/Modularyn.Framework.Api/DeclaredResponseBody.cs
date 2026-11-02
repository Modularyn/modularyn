using System.IO.Pipelines;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Net.Http.Headers;
using Modularyn.Framework.Api.Contracts;

namespace Modularyn.Framework.Api;

/// <summary>A handler sent its response with a media type that its endpoint's inventory declaration does not allow.</summary>
internal sealed class UndeclaredMediaTypeException() : InvalidOperationException("The response's media type is not one its endpoint declares.");

/// <summary>
/// The response body of a declared endpoint (TM-G02-011). Nothing written through this feature reaches the client until the
/// response's media type is one its declaration allows: JSON for a JSON endpoint, HTML for a page, one of a download's declared
/// types, plain text where the inventory declares it, and the API's own problem document for any endpoint. Parameters such as a
/// charset are ignored. Bytes or a file sent with no type, or with any other, are refused before they are written, and so is
/// everything after them; the endpoint wrapper then answers 500 with <see cref="ApiCodes.Protocol"/>, or aborts the connection
/// when the response had already started. An empty response, such as a redirect or a bare status, needs no type, and an explicit
/// start of a response that names no type waits for its first bytes.
/// </summary>
/// <remarks>
/// Every supported write path goes through this feature for the handler's duration. A handler that reaches the server's own
/// body feature another way, such as by casting the request's <see cref="IHttpResponseFeature"/>, writes past it; module code is
/// trusted in process (RR-G02-019).
/// </remarks>
internal sealed class DeclaredResponseBody : Stream, IHttpResponseBodyFeature
{
    internal const string ProblemMediaType = "application/problem+json";
    private readonly HttpContext context;
    private readonly ApiEndpoint contract;
    private readonly IHttpResponseBodyFeature inner;
    private PipeWriter? writer;

    internal DeclaredResponseBody(HttpContext context, ApiEndpoint contract, IHttpResponseBodyFeature inner)
    {
        this.context = context;
        this.contract = contract;
        this.inner = inner;
    }

    /// <summary>True once the handler sent, or tried to send, something its declaration does not allow.</summary>
    internal bool Refused { get; private set; }

    /// <summary>True when <paramref name="contentType"/> names a type the endpoint declares, or the API's problem document.</summary>
    internal static bool Allows(ApiEndpoint contract, string? contentType)
    {
        if (!MediaTypeHeaderValue.TryParse(contentType, out var parsed) || parsed.MediaType.Value is not { Length: > 0 } type) return false;
        if (string.Equals(type, ProblemMediaType, StringComparison.OrdinalIgnoreCase)) return true;
        return contract.Download is { } download ? download.MediaTypes.Contains(type, StringComparer.OrdinalIgnoreCase)
            : string.Equals(type, contract.ResponseMediaType, StringComparison.OrdinalIgnoreCase);
    }

    // Checked whenever anything would reach the client. Starting an empty response needs no type, but any type it names must be
    // declared; bytes always need a declared type. A refusal is final for the response.
    private void Check(bool bytes)
    {
        var type = context.Response.ContentType;
        if (!Refused && (Allows(contract, type) || !bytes && string.IsNullOrEmpty(type))) return;
        Refused = true;
        throw new UndeclaredMediaTypeException();
    }

    /// <summary>Sends what the handler left in the body's writer, through the same check, without starting an empty response.</summary>
    internal async Task CompleteWriterAsync(CancellationToken cancellationToken)
    {
        if (writer is null || writer.CanGetUnflushedBytes && writer.UnflushedBytes == 0) return;
        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    Stream IHttpResponseBodyFeature.Stream => this;

    PipeWriter IHttpResponseBodyFeature.Writer => writer ??= PipeWriter.Create(this, new StreamPipeWriterOptions(leaveOpen: true));

    void IHttpResponseBodyFeature.DisableBuffering() => inner.DisableBuffering();

    // Starting a response that names no type yet sends nothing worth sending early, so it waits for the first bytes, which then
    // need a declared type, or for the server's own completion of an empty response. A refusal can then still answer MAP008.
    Task IHttpResponseBodyFeature.StartAsync(CancellationToken cancellationToken)
    {
        if (!Refused && string.IsNullOrEmpty(context.Response.ContentType)) return Task.CompletedTask;
        Check(bytes: false);
        return inner.StartAsync(cancellationToken);
    }

    async Task IHttpResponseBodyFeature.SendFileAsync(string path, long offset, long? count, CancellationToken cancellationToken)
    {
        await CompleteWriterAsync(cancellationToken).ConfigureAwait(false);
        Check(bytes: true);
        await inner.SendFileAsync(path, offset, count, cancellationToken).ConfigureAwait(false);
    }

    async Task IHttpResponseBodyFeature.CompleteAsync()
    {
        await CompleteWriterAsync(context.RequestAborted).ConfigureAwait(false);
        Check(bytes: false);
        await inner.CompleteAsync().ConfigureAwait(false);
    }

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        if (buffer.IsEmpty) return;
        Check(bytes: true);
        inner.Stream.Write(buffer);
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (buffer.IsEmpty) return ValueTask.CompletedTask;
        Check(bytes: true);
        return inner.Stream.WriteAsync(buffer, cancellationToken);
    }

    public override void Flush()
    {
        Check(bytes: false);
        inner.Stream.Flush();
    }

    public override Task FlushAsync(CancellationToken cancellationToken)
    {
        Check(bytes: false);
        return inner.Stream.FlushAsync(cancellationToken);
    }
}
