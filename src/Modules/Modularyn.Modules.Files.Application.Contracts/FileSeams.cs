namespace Modularyn.Modules.Files.Application.Contracts;

public enum FileReleaseVerdict { Release = 1, Reject = 2, Defer = 3 }

/// <summary>The version a release policy decides on, as Files verified it. The name is the client's display name.</summary>
public sealed record FileReleaseCandidate(Guid TenantId, Guid FileId, Guid VersionId, string Name, long Length, string Sha256, string ContentType);

/// <summary>
/// The deployment's quarantine policy, such as a scanner adapter or a moderation step; Files installs none and contains no
/// scanner. It receives one pending version's verified bytes and decides. Without a policy, on a deferral or when it fails,
/// the version stays pending and unreadable.
/// </summary>
public interface IFileReleasePolicy
{
    ValueTask<FileReleaseVerdict> DecideAsync(FileReleaseCandidate candidate, Stream content, CancellationToken cancellationToken);
}

/// <summary>
/// Where Files hands recovery after an upload fails, until CODE-05's durable jobs take it over. It receives the failed
/// upload's tenant context, captured in that unit, and later runs <c>FileService.RecoverAsync</c> with it through
/// <c>TenantWorkRunner</c>. Without one, recovery runs when the tenant next reaches its quota, or when called.
/// </summary>
public interface IFileRecoverySchedule
{
    ValueTask ScheduleAsync(string propagatedTenantContext, CancellationToken cancellationToken);
}
