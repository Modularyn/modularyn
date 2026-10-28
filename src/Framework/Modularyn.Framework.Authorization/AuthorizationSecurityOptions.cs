using Microsoft.Extensions.Options;
using Modularyn.Framework.Tenancy.Abstractions;

namespace Modularyn.Framework.Authorization;

/// <summary>
/// The authorization deployment options, bound strictly from <c>Modularyn:Authorization</c> and validated before startup.
/// They change only on restart.
/// </summary>
public sealed class AuthorizationSecurityOptions
{
    public static readonly TimeSpan MinimumFreshnessBound = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan MaximumFreshnessBound = TimeSpan.FromMinutes(5);

    /// <summary>
    /// The release's declared freshness bound B: the longest a decision for a bounded permission may rest on authority read
    /// earlier. A revocation takes effect at once in the process that made it, and in every other process within B.
    /// Security-sensitive permissions are never served from a cache. From one second to five minutes.
    /// </summary>
    public TimeSpan FreshnessBound { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Standing host authority declared by the deployment, for the first host operators: deployment authority, as trusted
    /// as the configuration that names it. Every further grant is a stored host-authority grant, made in an audited host
    /// scope by an actor who holds the operation itself.
    /// </summary>
    public List<HostOperatorOptions> HostOperators { get; set; } = [];
}

/// <summary>One actor and the declared host operations the deployment grants it.</summary>
public sealed class HostOperatorOptions
{
    public ActorKind Kind { get; set; } = ActorKind.User;
    public string Subject { get; set; } = "";
    public List<string> Operations { get; set; } = [];
}

internal sealed class AuthorizationSecurityOptionsValidation(string sectionPath, AuthorizationRegistry registry) : IValidateOptions<AuthorizationSecurityOptions>
{
    public ValidateOptionsResult Validate(string? name, AuthorizationSecurityOptions options)
    {
        var failures = new List<string>();
        if (options.FreshnessBound < AuthorizationSecurityOptions.MinimumFreshnessBound || options.FreshnessBound > AuthorizationSecurityOptions.MaximumFreshnessBound)
            failures.Add($"'{sectionPath}:FreshnessBound' must be from one second to five minutes.");
        var actors = new HashSet<(ActorKind, string)>();
        for (var index = 0; index < options.HostOperators.Count; index++)
        {
            var at = $"{sectionPath}:HostOperators:{index}";
            var entry = options.HostOperators[index];
            if (entry is null || !Enum.IsDefined(entry.Kind)) failures.Add($"'{at}:Kind' must be User or Service.");
            else if (!IsSubject(entry.Subject)) failures.Add($"'{at}:Subject' must be an actor subject of at most 256 characters.");
            else if (!actors.Add((entry.Kind, entry.Subject))) failures.Add($"'{at}' names an actor that another entry already names.");
            var operations = entry?.Operations ?? [];
            if (operations.Count == 0) failures.Add($"'{at}:Operations' must list at least one host operation.");
            for (var operation = 0; operation < operations.Count; operation++)
                if (registry.FindHostOperation(operations[operation]) is null)
                    failures.Add($"'{at}:Operations:{operation}' must name a host operation that a composed module declares.");
        }
        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    private static bool IsSubject(string? subject)
    {
        if (string.IsNullOrWhiteSpace(subject)) return false;
        try
        {
            _ = new ActorReference(ActorKind.User, subject);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
