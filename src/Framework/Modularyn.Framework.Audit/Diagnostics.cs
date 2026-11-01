using Modularyn.Framework.Authorization.Abstractions;
using Modularyn.Framework.Observability;
using Modularyn.Framework.Security.Abstractions;
using Modularyn.Framework.Tenancy.Abstractions;

namespace Modularyn.Framework.Audit;

/// <summary>
/// What a durable event, job or schedule payload may carry (TM-G02-039): references, such as identifiers, and the
/// operational values its handler needs, never a secret, credential, token, session identifier or propagated context, and
/// no personal data that the handler can read again by reference. Payloads are classified as operational references; the
/// framework has no classification for secrets or personal data, so it refuses at append every form it recognizes and
/// removes a payload once its row can no longer run.
/// </summary>
public static class DurablePayloads
{
    /// <summary>The longest payload, in characters: room for references, not for documents.</summary>
    public const int MaxLength = 65_536;

    /// <summary>What a redacted payload holds. An appended payload is never empty, so a redacted one is told apart.</summary>
    public const string Redacted = "";

    /// <summary>
    /// Refuses a payload that is empty or longer than <see cref="MaxLength"/>, holds a control character other than tab or
    /// a line break, holds a recognized secret form, or holds a propagated tenant context.
    /// </summary>
    public static void Require(string payload, string parameterName)
    {
        if (string.IsNullOrEmpty(payload) || payload.Length > MaxLength)
            throw new ArgumentException("A durable payload is 1 to 65,536 characters of references.", parameterName);
        if (payload.Any(character => char.IsControl(character) && character is not '\t' and not '\n' and not '\r'))
            throw new ArgumentException("A durable payload holds no control characters other than tabs and line breaks.", parameterName);
        if (SecretForms.Contains(payload))
            throw new ArgumentException("A durable payload carries references, never a secret, credential or token.", parameterName);
        // Only the store captures propagated context, beside the payload; a payload that carries one would let it travel unbound.
        if (payload.Contains(PropagatedTenantContext.FormatName, StringComparison.Ordinal))
            throw new ArgumentException("A durable payload never carries a propagated tenant context.", parameterName);
    }

    /// <summary>Refuses a key or name stored beside a payload, such as an ordering key, that holds a recognized secret form.</summary>
    public static void RequireReference(string value, string parameterName)
    {
        if (SecretForms.Contains(value)) throw new ArgumentException("A durable reference never holds a secret, credential or token.", parameterName);
    }

    public static bool IsRedacted(string payload) => string.IsNullOrEmpty(payload);
}

/// <summary>
/// What diagnostics show of a durable payload: whether it is still held and its length, never its content, so job history
/// and quarantine views show redacted arguments.
/// </summary>
public readonly record struct PayloadSummary(bool Redacted, int Length)
{
    public static PayloadSummary Of(string payload) => DurablePayloads.IsRedacted(payload) ? new(true, 0) : new(false, payload.Length);

    public override string ToString() => Redacted ? "redacted" : Length + " characters";
}

/// <summary>A diagnostics read was refused, with the stable code of the decision that refused it and no diagnostic content.</summary>
public sealed class DiagnosticsAccessException(string code) : InvalidOperationException("The diagnostics read was refused.")
{
    public string Code { get; } = code;
}

/// <summary>
/// The authority that reads detailed diagnostics, separate from audit reading and from every administration permission
/// (TM-G02-052, TM-G02-054): a tenant's operational diagnostics need <see cref="AuditModule.DiagnosticsPermission"/> at the
/// current profile through the evaluator, in that tenant's context, and the host's own need an active host scope granted for
/// <see cref="AuditModule.DiagnosticsHostReadOperation"/>.
/// </summary>
public static class DiagnosticsAccess
{
    /// <summary>Requires the tenant diagnostics permission now, or throws <see cref="DiagnosticsAccessException"/>.</summary>
    public static async Task RequireTenantAsync(IPermissionEvaluator? evaluator, TenantContext context, ExecutionPrincipal? principal,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!context.IsActive || context.Scope != TenantScope.Tenant) throw new DiagnosticsAccessException(AuthorizationCodes.NoTenantContext);
        if (evaluator is null) throw new DiagnosticsAccessException(AuthorizationCodes.Unavailable);
        var decision = await evaluator.AuthorizeAsync(new(context, principal, AuditModule.DiagnosticsPermission) { RequireCurrent = true }, cancellationToken)
            .ConfigureAwait(false);
        if (!decision.Permitted) throw new DiagnosticsAccessException(decision.Code!);
    }

    /// <summary>Requires an active host scope granted for the host diagnostics read, or throws <see cref="DiagnosticsAccessException"/>.</summary>
    public static void RequireHost(TenantContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!context.IsActive || context.Scope != TenantScope.Host || context.HostGrant?.OperationId != AuditModule.DiagnosticsHostReadOperation.Id)
            throw new DiagnosticsAccessException(AuditCodes.HostReadRequired);
    }
}
