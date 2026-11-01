using System.Security.Cryptography;
using System.Text.Json;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Modularyn.Framework.Api.Contracts;
using Modularyn.Framework.Persistence.Abstractions;
using Modularyn.Framework.Persistence.EfCore;
using Modularyn.Framework.Security.Abstractions;
using Modularyn.Framework.Tenancy.Abstractions;

namespace Modularyn.Framework.Api;

/// <summary>A receipt in the owning module's transaction. Its subject restricts lookup; it never supplies authority.</summary>
public sealed class ApiOperation : ITenantScoped
{
    public Guid TenantId { get; set; }
    public Guid Id { get; set; }
    public string Subject { get; set; } = "";
    public string Fingerprint { get; set; } = "";
    public string Result { get; set; } = "";

    public static void Configure(ModelBuilder builder) => builder.Entity<ApiOperation>(entity =>
    {
        entity.ToTable("ApiOperations");
        entity.HasKey(row => new { row.TenantId, row.Id });
        entity.Property(row => row.Subject).IsRequired();
        entity.Property(row => row.Fingerprint).IsRequired();
        entity.Property(row => row.Result).IsRequired();
    });
}

/// <summary>
/// The caller authorizes the use case before entering. The effect and receipt commit in the same tenant-bound unit.
/// A unique receipt serializes concurrent retries across processes. No receipt means not observed, never proof of rollback.
/// </summary>
public static class ApiOperations
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static async Task<T> ExecuteAsync<T>(PostgreSqlModule module, TenantContext tenant, ExecutionPrincipal principal,
        Guid operationId, string operation, int version, object request, Func<EfCoreUnitOfWork, CancellationToken, Task<T>> effect,
        CancellationToken cancellationToken = default) where T : class
    {
        if (operationId == Guid.Empty || string.IsNullOrWhiteSpace(operation)) throw new ApiException(ApiCodes.Validation, 400);
        var subject = Subject(principal);
        var fingerprint = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(new { operation, version, request }, Json))));
        for (var attempt = 0; attempt < 3; attempt++)
        {
            await using var unit = await EfCoreUnitOfWork.BeginAsync(module, tenant, cancellationToken: cancellationToken).ConfigureAwait(false);
            var receipts = unit.Context.Set<ApiOperation>();
            var existing = await receipts.SingleOrDefaultAsync(row => row.Id == operationId, cancellationToken).ConfigureAwait(false);
            if (existing is not null)
            {
                if (existing.Subject != subject) throw new ApiException(ApiCodes.NotFound, 404);
                if (existing.Fingerprint != fingerprint) throw new ApiException(ApiCodes.OperationConflict, 409);
                return JsonSerializer.Deserialize<T>(existing.Result, Json) ?? throw new ApiException(ApiCodes.Protocol, 500);
            }
            var receipt = new ApiOperation { Id = operationId, Subject = subject, Fingerprint = fingerprint };
            receipts.Add(receipt);
            try { await unit.FlushAsync(cancellationToken).ConfigureAwait(false); }
            catch (DbUpdateException exception) when (exception.InnerException is Npgsql.PostgresException { SqlState: "23505" })
            { continue; }
            T result;
            try { result = await effect(unit, cancellationToken).ConfigureAwait(false); }
            catch (DbUpdateConcurrencyException) { throw new ApiException(ApiCodes.Conflict, 409); }
            receipt.Result = JsonSerializer.Serialize(result, Json);
            // Cancellation before this boundary rolls back. An interrupted commit acknowledgement requires a fresh lookup.
            cancellationToken.ThrowIfCancellationRequested();
            var committed = await unit.CompleteAsync(cancellationToken).ConfigureAwait(false);
            if (committed.State != UnitOfWorkState.Committed) throw new ApiException(ApiCodes.Conflict, 409);
            return result;
        }
        throw new ApiException(ApiCodes.Unavailable, 503);
    }

    public static async Task<T?> FindAsync<T>(PostgreSqlModule module, TenantContext tenant, ExecutionPrincipal principal,
        Guid operationId, CancellationToken cancellationToken = default) where T : class
    {
        var subject = Subject(principal);
        await using var unit = await EfCoreUnitOfWork.BeginAsync(module, tenant, cancellationToken: cancellationToken).ConfigureAwait(false);
        var receipt = await unit.Context.Set<ApiOperation>().AsNoTracking()
            .SingleOrDefaultAsync(row => row.Id == operationId && row.Subject == subject, cancellationToken).ConfigureAwait(false);
        return receipt is null ? null : JsonSerializer.Deserialize<T>(receipt.Result, Json);
    }

    private static string Subject(ExecutionPrincipal principal) => principal.EffectiveSubject.Kind + ":" + principal.EffectiveSubject.Id;
}
