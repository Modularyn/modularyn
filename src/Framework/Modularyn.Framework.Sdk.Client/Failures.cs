using System.Text.Json;
using Modularyn.Framework.Api.Contracts;

namespace Modularyn.Framework.Sdk.Client;

public class ApiFailure(ApiProblem problem) : Exception(problem.Code)
{
    public ApiProblem Problem { get; } = problem;
}
public sealed class ValidationFailure(ApiProblem problem) : ApiFailure(problem);
public sealed class ConcurrencyFailure(ApiProblem problem) : ApiFailure(problem);
public sealed class AuthenticationFailure(ApiProblem problem) : ApiFailure(problem);
public sealed class AuthorizationFailure(ApiProblem problem) : ApiFailure(problem);
public sealed class MissingFailure(ApiProblem problem) : ApiFailure(problem);
public sealed class AvailabilityFailure(ApiProblem problem) : ApiFailure(problem);
public sealed class OperationConflictFailure(ApiProblem problem) : ApiFailure(problem);
public sealed class CompatibilityFailure(ApiProblem problem, JsonElement? authoritativeResponse = null) : ApiFailure(problem)
{
    /// <summary>A completed success with an unexpected version remains an observed outcome. Reconcile before any retry.</summary>
    public JsonElement? AuthoritativeResponse { get; } = authoritativeResponse;
}
public sealed class OutcomeUnknownFailure(ApiProblem problem) : ApiFailure(problem)
{
    public OutcomeUnknownFailure(Guid operationId) : this(new("urn:modularyn:problem:" + ApiCodes.OutcomeUnknown,
        "Reconcile this operation before retrying with the same identity.", 0, ApiCodes.OutcomeUnknown, "", operationId)) { }
}
public sealed class ProtocolFailure(ApiProblem problem, JsonElement? authoritativeResponse = null) : ApiFailure(problem)
{
    public JsonElement? AuthoritativeResponse { get; } = authoritativeResponse;
}
