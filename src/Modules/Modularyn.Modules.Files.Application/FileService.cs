using System.Security.Cryptography;
using Modularyn.Framework.Authorization.Abstractions;
using Modularyn.Framework.Security.Abstractions;
using Modularyn.Framework.Tenancy;
using Modularyn.Framework.Tenancy.Abstractions;
using Modularyn.Modules.Files.Application.Contracts;
using Modularyn.Modules.Files.Domain;

namespace Modularyn.Modules.Files.Application;

/// <summary>
/// The Files module's one <see cref="IFiles"/>. Every action asks SEC-05's evaluator before it reads or changes anything;
/// client names, sizes and bytes are validated before an intent exists; new versions stay pending until the deployment's
/// release policy releases them, and the read path refuses every unreleased version. Reads, deletes and restores take no
/// lease. An upload takes the tenant's exclusive lease only to reserve quota and commit its intent, and again to publish,
/// never while it streams the client's body.
/// </summary>
public sealed class FileService : IFiles
{
    public const int MaxListLimit = 100;
    public const int MaxListOffset = 10_000;
    private static readonly TimeSpan ScheduleTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan FirstLeaseRetry = TimeSpan.FromMilliseconds(10);
    private static readonly TimeSpan LastLeaseRetry = TimeSpan.FromMilliseconds(250);
    private readonly IFileMetadataStore metadata;
    private readonly IFileBlobStore blobs;
    private readonly IPermissionEvaluator permissions;
    private readonly TimeProvider time;
    private readonly FilesPolicy policy;
    private readonly long pendingBytes;
    private readonly IFileReleasePolicy? release;
    private readonly IFileRecoverySchedule? recovery;
    private readonly IFileStorageObserver? observer;

    public FileService(IFileMetadataStore metadata, IFileBlobStore blobs, IPermissionEvaluator permissions, TimeProvider time,
        FilesPolicy? policy = null, IFileReleasePolicy? release = null, IFileRecoverySchedule? recovery = null, IFileStorageObserver? observer = null)
    {
        this.metadata = metadata ?? throw new ArgumentNullException(nameof(metadata));
        this.blobs = blobs ?? throw new ArgumentNullException(nameof(blobs));
        this.permissions = permissions ?? throw new ArgumentNullException(nameof(permissions));
        this.time = time ?? throw new ArgumentNullException(nameof(time));
        this.policy = policy ?? new FilesPolicy();
        pendingBytes = this.policy.MaximumPendingBytes
            ?? (blobs.MaximumLength > long.MaxValue / FilesPolicy.PendingBytesPerMaximumLength ? long.MaxValue : blobs.MaximumLength * FilesPolicy.PendingBytesPerMaximumLength);
        if (pendingBytes < blobs.MaximumLength)
            throw new ArgumentException("The pending-byte quota must admit one upload of the store's maximum length.", nameof(policy));
        this.release = release;
        this.recovery = recovery;
        this.observer = observer;
    }

    public async Task<FileVersionInfo> UploadAsync(TenantContext tenant, ExecutionPrincipal? principal, FileUploadRequest request, Stream content,
        CancellationToken cancellationToken = default)
    {
        var tenantId = FileTenant.Require(tenant);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(content);
        cancellationToken.ThrowIfCancellationRequested();
        // Nothing the client sent is trusted, and nothing is reserved or written, before these checks pass: the declared
        // size against the store's limit first, then the digest's form and the metadata's canonical forms.
        if (request.Length < 0 || request.Length > blobs.MaximumLength || !content.CanRead) throw new FileStorageException(FileStorageError.InvalidLength);
        var expected = new BlobIntegrity(request.Length, request.Sha256);
        var name = FileMetadataRules.Name(request.Name);
        var container = FileMetadataRules.Container(request.Container);
        var owner = FileMetadataRules.Owner(request.Owner);
        var creator = await AuthorizeUploadAsync(tenant, principal, request.FileId, cancellationToken).ConfigureAwait(false);
        // The type is decided from the content's first bytes against the allowed list, before any intent exists.
        var head = await ReadHeadAsync(content, (int)Math.Min(request.Length, FileContentTypes.SniffLength), cancellationToken).ConfigureAwait(false);
        var contentType = FileContentTypes.Detect(head);
        if (!policy.AllowedContentTypes.Contains(contentType)) throw new FileStorageException(FileStorageError.UnsupportedContent);
        var fileId = request.FileId ?? Guid.NewGuid();
        var revisionId = Guid.NewGuid();
        var createdAt = FileTime.UtcNow(time);
        var committed = false;
        try
        {
            IFileBlobStaging staging;
            await using (var lease = await LeaseAsync(tenant, cancellationToken).ConfigureAwait(false))
            {
                await ReserveAsync(tenant, lease, expected.Length, cancellationToken).ConfigureAwait(false);
                // This durable intent makes every interruption accountable, even after a rename or a lost commit response.
                await using (var intent = await metadata.BeginAsync(tenant, cancellationToken).ConfigureAwait(false))
                {
                    if (request.FileId is null)
                        intent.Add(new FileAsset { TenantId = tenantId, Id = fileId, Name = name, Container = container, Owner = owner,
                            CreatedByKind = creator.Kind, CreatedBy = creator.Subject, CreatedAt = createdAt });
                    else if (await intent.FindFileAsync(fileId, cancellationToken).ConfigureAwait(false) is null)
                        throw new FileStorageException(FileStorageError.NotFound);
                    intent.Add(new FileUpload { TenantId = tenantId, Id = revisionId, FileId = fileId, Length = expected.Length,
                        Sha256 = expected.Sha256, ContentType = contentType, CreatedAt = createdAt,
                        PropagatedContext = PropagatedTenantContext.Capture(tenant, time).ToJson() });
                    await intent.CommitAsync(cancellationToken).ConfigureAwait(false);
                }
                committed = true;
                await ObserveAsync(FileStorageStage.IntentCommitted, revisionId, cancellationToken).ConfigureAwait(false);
                // Created and held under the lease, so recovery never finds this intent's staging file unheld while it streams.
                staging = await lease.StageAsync(revisionId, cancellationToken).ConfigureAwait(false);
            }
            await using (staging)
            {
                // An upload that streams longer than the policy allows is abandoned like any other failed one.
                using (var timeout = new CancellationTokenSource(policy.UploadTimeout, time))
                using (var streaming = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token))
                    await staging.WriteAsync(new PrefixedStream(head, content), expected, streaming.Token).ConfigureAwait(false);
                return await PublishAsync(tenant, tenantId, staging, fileId, createdAt, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception) when (committed && recovery is not null)
        {
            await ScheduleRecoveryAsync(tenant).ConfigureAwait(false);
            throw;
        }
    }

    public async Task<IReadOnlyList<FileSummary>> ListFilesAsync(TenantContext tenant, ExecutionPrincipal? principal, string container, int offset = 0,
        int limit = 50, CancellationToken cancellationToken = default)
    {
        FileTenant.Require(tenant);
        var name = FileMetadataRules.Container(container);
        if (offset is < 0 or > MaxListOffset) throw new ArgumentOutOfRangeException(nameof(offset));
        if (limit is < 1 or > MaxListLimit) throw new ArgumentOutOfRangeException(nameof(limit));
        // The evaluator's visibility is composed into the query, so files the subject may not read affect neither rows nor order.
        var visibility = await permissions.VisibleAsync(new AuthorizationRequest(tenant, principal, FilePermissions.Read), FilePermissions.ResourceType,
            cancellationToken).ConfigureAwait(false);
        if (!visibility.Decision.Permitted) throw new FileAccessDeniedException(visibility.Decision.Code!);
        await using var unit = await metadata.BeginAsync(tenant, cancellationToken).ConfigureAwait(false);
        return (await unit.ListFilesAsync(name, visibility, offset, limit, cancellationToken).ConfigureAwait(false)).Select(Summarize).ToArray();
    }

    public async Task<IReadOnlyList<FileVersionInfo>> ListAsync(TenantContext tenant, ExecutionPrincipal? principal, Guid fileId,
        CancellationToken cancellationToken = default)
    {
        FileTenant.Require(tenant);
        await using var unit = await metadata.BeginAsync(tenant, cancellationToken).ConfigureAwait(false);
        await AuthorizeFileAsync(unit, tenant, principal, FilePermissions.Read, fileId, cancellationToken).ConfigureAwait(false);
        return (await unit.ListRevisionsAsync(fileId, cancellationToken).ConfigureAwait(false)).Select(Describe).ToArray();
    }

    public async Task<FileReadResult> ReadAsync(TenantContext tenant, ExecutionPrincipal? principal, Guid fileId, Guid versionId, FileReadRange? range = null,
        CancellationToken cancellationToken = default)
    {
        FileTenant.Require(tenant);
        FileAsset file;
        FileRevision revision;
        await using (var unit = await metadata.BeginAsync(tenant, cancellationToken).ConfigureAwait(false))
        {
            file = await AuthorizeFileAsync(unit, tenant, principal, FilePermissions.Read, fileId, cancellationToken).ConfigureAwait(false);
            revision = await RequiredAsync(unit, fileId, versionId, false, cancellationToken).ConfigureAwait(false);
        }
        RequireReleased(revision);
        var content = await blobs.ReadAsync(tenant, revision.Id, new(revision.Length, revision.Sha256),
            range is null ? null : new(range.Offset, range.Length), cancellationToken).ConfigureAwait(false);
        return new(Describe(revision), content, Delivery(file, revision, FileGrantPurpose.Download));
    }

    public async Task<IReadOnlyList<FileVersionInfo>> ListDeletedAsync(TenantContext tenant, ExecutionPrincipal? principal, Guid fileId,
        CancellationToken cancellationToken = default)
    {
        FileTenant.Require(tenant);
        await using var unit = await metadata.BeginAsync(tenant, cancellationToken).ConfigureAwait(false);
        await AuthorizeFileAsync(unit, tenant, principal, FilePermissions.Restore, fileId, cancellationToken).ConfigureAwait(false);
        return (await unit.ListDeletedRevisionsAsync(fileId, cancellationToken).ConfigureAwait(false)).Select(Describe).ToArray();
    }

    public async Task<FileVersionInfo> DeleteAsync(TenantContext tenant, ExecutionPrincipal? principal, Guid fileId, Guid versionId, Guid expectedVersion,
        CancellationToken cancellationToken = default)
    {
        FileTenant.Require(tenant);
        await using var unit = await metadata.BeginAsync(tenant, cancellationToken).ConfigureAwait(false);
        await AuthorizeFileAsync(unit, tenant, principal, FilePermissions.Delete, fileId, cancellationToken).ConfigureAwait(false);
        var revision = await RequiredAsync(unit, fileId, versionId, true, cancellationToken).ConfigureAwait(false);
        RequireVersion(revision, expectedVersion);
        revision.IsDeleted = true;
        // A deleted version's grants end with it in the same transaction, so a restore never revives an earlier link.
        var now = FileTime.UtcNow(time);
        foreach (var grant in await unit.ListOpenGrantsAsync(revision.Id, cancellationToken).ConfigureAwait(false)) grant.RevokedAt = now;
        await unit.CommitAsync(cancellationToken).ConfigureAwait(false);
        return Describe(revision);
    }

    public async Task<FileVersionInfo> RestoreAsync(TenantContext tenant, ExecutionPrincipal? principal, Guid fileId, Guid versionId, Guid expectedVersion,
        CancellationToken cancellationToken = default)
    {
        FileTenant.Require(tenant);
        FileRevision retained;
        await using (var unit = await metadata.BeginAsync(tenant, cancellationToken).ConfigureAwait(false))
        {
            await AuthorizeFileAsync(unit, tenant, principal, FilePermissions.Restore, fileId, cancellationToken).ConfigureAwait(false);
            retained = await RequiredAsync(unit, fileId, versionId, true, cancellationToken).ConfigureAwait(false);
            RequireVersion(retained, expectedVersion);
        }
        // The retained bytes are hashed where they are, with no copy and no lease, before the version becomes visible.
        await blobs.VerifyAsync(tenant, retained.Id, new(retained.Length, retained.Sha256), cancellationToken).ConfigureAwait(false);
        await using var change = await metadata.BeginAsync(tenant, cancellationToken).ConfigureAwait(false);
        var revision = await RequiredAsync(change, fileId, versionId, true, cancellationToken).ConfigureAwait(false);
        RequireVersion(revision, expectedVersion);
        revision.IsDeleted = false;
        await change.CommitAsync(cancellationToken).ConfigureAwait(false);
        return Describe(revision);
    }

    public async Task<FileVersionInfo> ReleaseAsync(TenantContext tenant, ExecutionPrincipal? principal, Guid fileId, Guid versionId,
        CancellationToken cancellationToken = default)
    {
        var tenantId = FileTenant.Require(tenant);
        FileAsset file;
        FileRevision pending;
        await using (var unit = await metadata.BeginAsync(tenant, cancellationToken).ConfigureAwait(false))
        {
            file = await AuthorizeFileAsync(unit, tenant, principal, FilePermissions.Release, fileId, cancellationToken).ConfigureAwait(false);
            pending = await RequiredAsync(unit, fileId, versionId, false, cancellationToken).ConfigureAwait(false);
        }
        // Only the deployment's policy decides, and only a pending version; without one, it stays quarantined.
        if (pending.Quarantine != QuarantineState.Pending || release is null) return Describe(pending);
        FileReleaseVerdict verdict;
        await using (var content = await blobs.ReadAsync(tenant, pending.Id, new(pending.Length, pending.Sha256), null, cancellationToken).ConfigureAwait(false))
            verdict = await release.DecideAsync(new(tenantId, fileId, pending.Id, file.Name, pending.Length, pending.Sha256, pending.ContentType), content,
                cancellationToken).ConfigureAwait(false);
        if (verdict is not (FileReleaseVerdict.Release or FileReleaseVerdict.Reject)) return Describe(pending);
        await using var change = await metadata.BeginAsync(tenant, cancellationToken).ConfigureAwait(false);
        var revision = await RequiredAsync(change, fileId, versionId, false, cancellationToken).ConfigureAwait(false);
        // The verdict is for the version as it was read; any change since then makes the caller decide again.
        if (revision.Version != pending.Version || revision.Quarantine != QuarantineState.Pending) throw new FileStorageException(FileStorageError.Conflict);
        revision.Quarantine = verdict == FileReleaseVerdict.Release ? QuarantineState.Released : QuarantineState.Rejected;
        await change.CommitAsync(cancellationToken).ConfigureAwait(false);
        return Describe(revision);
    }

    public async Task<IssuedFileGrant> IssueGrantAsync(TenantContext tenant, ExecutionPrincipal? principal, Guid fileId, Guid versionId,
        FileGrantPurpose purpose, TimeSpan lifetime, CancellationToken cancellationToken = default)
    {
        var tenantId = FileTenant.Require(tenant);
        if (!Enum.IsDefined(purpose)) throw new ArgumentOutOfRangeException(nameof(purpose));
        if (lifetime <= TimeSpan.Zero || lifetime > policy.MaximumGrantLifetime) throw new ArgumentOutOfRangeException(nameof(lifetime));
        await using var unit = await metadata.BeginAsync(tenant, cancellationToken).ConfigureAwait(false);
        await AuthorizeFileAsync(unit, tenant, principal, FilePermissions.Grant, fileId, cancellationToken).ConfigureAwait(false);
        var revision = await RequiredAsync(unit, fileId, versionId, false, cancellationToken).ConfigureAwait(false);
        RequireReleased(revision);
        var creator = Creator(tenant, principal);
        var now = FileTime.UtcNow(time);
        var token = FileGrantToken.Create(Guid.NewGuid());
        var grant = new FileGrant { TenantId = tenantId, Id = token.GrantId, FileId = fileId, VersionId = revision.Id, Purpose = (GrantPurpose)purpose,
            Verifier = token.ComputeVerifier(tenantId), CreatedByKind = creator.Kind, CreatedBy = creator.Subject, CreatedAt = now,
            ExpiresAt = FileTime.Normalize(now + lifetime) };
        unit.Add(grant);
        await unit.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new(Inform(grant, now), token);
    }

    public async Task<IReadOnlyList<FileGrantInfo>> ListGrantsAsync(TenantContext tenant, ExecutionPrincipal? principal, Guid fileId,
        CancellationToken cancellationToken = default)
    {
        FileTenant.Require(tenant);
        await using var unit = await metadata.BeginAsync(tenant, cancellationToken).ConfigureAwait(false);
        await AuthorizeFileAsync(unit, tenant, principal, FilePermissions.Grant, fileId, cancellationToken).ConfigureAwait(false);
        var now = FileTime.UtcNow(time);
        return (await unit.ListGrantsAsync(fileId, cancellationToken).ConfigureAwait(false)).Select(grant => Inform(grant, now)).ToArray();
    }

    public async Task<FileGrantInfo> RevokeGrantAsync(TenantContext tenant, ExecutionPrincipal? principal, Guid grantId,
        CancellationToken cancellationToken = default)
    {
        FileTenant.Require(tenant);
        await using var unit = await metadata.BeginAsync(tenant, cancellationToken).ConfigureAwait(false);
        var grant = await unit.FindGrantAsync(grantId, cancellationToken).ConfigureAwait(false);
        // An unknown grant is answered as a file the subject may not use, after the same tenant-level decision.
        await AuthorizeFileAsync(unit, tenant, principal, FilePermissions.Grant, grant?.FileId, cancellationToken).ConfigureAwait(false);
        var now = FileTime.UtcNow(time);
        if (grant!.RevokedAt is null)
        {
            grant.RevokedAt = now;
            await unit.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        return Inform(grant, now);
    }

    public async Task<FileReadResult> RedeemGrantAsync(TenantContext tenant, ExecutionPrincipal? principal, FileGrantToken token, FileGrantPurpose purpose,
        FileReadRange? range = null, CancellationToken cancellationToken = default)
    {
        var tenantId = FileTenant.Require(tenant);
        ArgumentNullException.ThrowIfNull(token);
        // A caller who may not read files at all is refused before anything about the token is looked at.
        await AuthorizeTenantAsync(tenant, principal, FilePermissions.Read, cancellationToken).ConfigureAwait(false);
        FileAsset file;
        FileRevision revision;
        FileGrant grant;
        await using (var unit = await metadata.BeginAsync(tenant, cancellationToken).ConfigureAwait(false))
        {
            // Selected by its identity within the effective tenant, then compared in constant time with the stored
            // verifier, which binds the tenant and the grant; a token of another tenant is never found.
            grant = await unit.FindGrantAsync(token.GrantId, cancellationToken).ConfigureAwait(false) is { } found
                && CryptographicOperations.FixedTimeEquals(found.Verifier, token.ComputeVerifier(tenantId)) ? found : throw GrantRefused();
            // Delivery checks the principal's current authorization for the file: a grant never outlasts its holder's access.
            file = await AuthorizeFileAsync(unit, tenant, principal, FilePermissions.Read, grant.FileId, GrantRefused, cancellationToken)
                .ConfigureAwait(false);
            if ((int)grant.Purpose != (int)purpose || !IsOpen(grant, FileTime.UtcNow(time))) throw GrantRefused();
            revision = await unit.FindRevisionAsync(grant.FileId, grant.VersionId, false, cancellationToken).ConfigureAwait(false) is
                { Quarantine: QuarantineState.Released } current ? current : throw GrantRefused();
        }
        var content = await blobs.ReadAsync(tenant, revision.Id, new(revision.Length, revision.Sha256),
            range is null ? null : new(range.Offset, range.Length), cancellationToken).ConfigureAwait(false);
        try
        {
            if (grant.Purpose == GrantPurpose.SingleDownload) await ConsumeAsync(tenant, grant.Id, cancellationToken).ConfigureAwait(false);
            return new(Describe(revision), content, Delivery(file, revision, purpose));
        }
        catch
        {
            await content.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Reconciles one tenant under the exclusive lease that uploads take for their intent and publication steps: reclaims
    /// abandoned read slots, discards unreferenced content of abandoned uploads while leaving a staging file a live upload
    /// holds, removes files their failed first upload left without a version, and removes expired grants. Safe to repeat
    /// after any interruption; returns the number of intents reconciled.
    /// </summary>
    public async Task<int> RecoverAsync(TenantContext tenant, CancellationToken cancellationToken = default)
    {
        FileTenant.Require(tenant);
        await using var storage = await blobs.OpenAsync(tenant, cancellationToken).ConfigureAwait(false);
        return await ReconcileAsync(tenant, storage, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Background recovery revalidates a captured context through the tenancy runtime.</summary>
    public Task<TenantWorkOutcome> RecoverAsync(PropagatedTenantContext propagated, TenantWorkRunner runner,
        CancellationToken cancellationToken = default) =>
        runner.RunAsync(propagated, async (unit, token) => { await RecoverAsync(unit.Context, token).ConfigureAwait(false); }, cancellationToken);

    /// <summary>Recovery for a stored reference, such as one an <see cref="IFileRecoverySchedule"/> kept, parsed strictly by the runner.</summary>
    public Task<TenantWorkOutcome> RecoverAsync(string propagated, TenantWorkRunner runner, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(runner);
        return runner.RunAsync(propagated, async (unit, token) => { await RecoverAsync(unit.Context, token).ConfigureAwait(false); }, cancellationToken);
    }

    private async Task<FileVersionInfo> PublishAsync(TenantContext tenant, Guid tenantId, IFileBlobStaging staging, Guid fileId, DateTimeOffset createdAt,
        CancellationToken cancellationToken)
    {
        await using var lease = await LeaseAsync(tenant, cancellationToken).ConfigureAwait(false);
        await using var publish = await metadata.BeginAsync(tenant, cancellationToken).ConfigureAwait(false);
        // Found by its identity; an intent that recovery already reconciled is never published.
        var upload = await publish.FindUploadAsync(staging.RevisionId, cancellationToken).ConfigureAwait(false)
            ?? throw new FileStorageException(FileStorageError.Conflict);
        // Failures leave the intent for recovery. Never guess that a failed commit response means rollback.
        await lease.PublishAsync(staging, cancellationToken).ConfigureAwait(false);
        var revision = new FileRevision { TenantId = tenantId, Id = upload.Id, FileId = fileId, Length = upload.Length, Sha256 = upload.Sha256,
            ContentType = upload.ContentType, CreatedAt = createdAt };
        publish.Add(revision);
        publish.Remove(upload);
        await publish.CommitAsync(cancellationToken).ConfigureAwait(false);
        await ObserveAsync(FileStorageStage.MetadataCommitted, revision.Id, cancellationToken).ConfigureAwait(false);
        return Describe(revision);
    }

    // Under the lease, so no other reservation of this tenant interleaves between the count and the intent it admits.
    private async Task ReserveAsync(TenantContext tenant, IFileBlobSession lease, long length, CancellationToken cancellationToken)
    {
        if (await WithinQuotaAsync(tenant, length, cancellationToken).ConfigureAwait(false)) return;
        // Over quota: the tenant's own abandoned uploads are reclaimed first, under the same lease, and the quota decided again.
        await ReconcileAsync(tenant, lease, cancellationToken).ConfigureAwait(false);
        if (!await WithinQuotaAsync(tenant, length, cancellationToken).ConfigureAwait(false)) throw new FileStorageException(FileStorageError.QuotaExceeded);
    }

    private async Task<bool> WithinQuotaAsync(TenantContext tenant, long length, CancellationToken cancellationToken)
    {
        await using var unit = await metadata.BeginAsync(tenant, cancellationToken).ConfigureAwait(false);
        var pending = await unit.PendingUploadsAsync(cancellationToken).ConfigureAwait(false);
        return pending.Count < policy.MaximumPendingUploads && pending.Bytes <= pendingBytes - length;
    }

    private async Task<int> ReconcileAsync(TenantContext tenant, IFileBlobSession storage, CancellationToken cancellationToken)
    {
        await storage.RecoverReadSnapshotsAsync(cancellationToken).ConfigureAwait(false);
        await using var unit = await metadata.BeginAsync(tenant, cancellationToken).ConfigureAwait(false);
        var pending = await unit.ListUploadsAsync(cancellationToken).ConfigureAwait(false);
        var reconciled = new HashSet<Guid>();
        foreach (var upload in pending)
        {
            // Include deleted revisions: their bytes remain necessary for restore. A staging file that a live upload
            // still holds is in flight, not abandoned, and stays with its intent.
            var referenced = await unit.IsContentReferencedAsync(upload.Id, cancellationToken).ConfigureAwait(false);
            if (!referenced && !await storage.DiscardAsync(upload.Id, cancellationToken).ConfigureAwait(false)) continue;
            unit.Remove(upload);
            reconciled.Add(upload.Id);
        }
        // A file whose only uploads failed has no version and no other intent left, so it goes with them.
        foreach (var fileId in pending.Where(upload => reconciled.Contains(upload.Id)).Select(upload => upload.FileId).Distinct().ToArray())
        {
            if (pending.Any(upload => upload.FileId == fileId && !reconciled.Contains(upload.Id))) continue;
            if (await unit.HasRevisionsAsync(fileId, cancellationToken).ConfigureAwait(false)) continue;
            if (await unit.FindFileAsync(fileId, cancellationToken).ConfigureAwait(false) is { } file) unit.Remove(file);
        }
        await unit.RemoveExpiredGrantsAsync(FileTime.UtcNow(time), cancellationToken).ConfigureAwait(false);
        await unit.CommitAsync(cancellationToken).ConfigureAwait(false);
        return reconciled.Count;
    }

    // The lease is held only for an upload's short steps, so an upload waits for it briefly with growing pauses, which the
    // policy bounds, rather than failing at once. Recovery holds it longer and does not wait.
    private async Task<IFileBlobSession> LeaseAsync(TenantContext tenant, CancellationToken cancellationToken)
    {
        var waited = TimeSpan.Zero;
        var pause = FirstLeaseRetry;
        while (true)
        {
            try
            {
                return await blobs.OpenAsync(tenant, cancellationToken).ConfigureAwait(false);
            }
            catch (FileStorageException exception) when (exception.Code == FileStorageError.Busy && waited < policy.LeaseWait)
            {
                await Task.Delay(pause, time, cancellationToken).ConfigureAwait(false);
                waited += pause;
                pause = pause * 2 < LastLeaseRetry ? pause * 2 : LastLeaseRetry;
            }
        }
    }

    // Consumed in its own transaction once the verified bytes are held, so a grant is never spent on a failed delivery, and
    // of two concurrent redemptions the second loses the version check.
    private async Task ConsumeAsync(TenantContext tenant, Guid grantId, CancellationToken cancellationToken)
    {
        await using var unit = await metadata.BeginAsync(tenant, cancellationToken).ConfigureAwait(false);
        var now = FileTime.UtcNow(time);
        var grant = await unit.FindGrantAsync(grantId, cancellationToken).ConfigureAwait(false);
        if (grant is null || !IsOpen(grant, now)) throw GrantRefused();
        grant.ConsumedAt = now;
        try { await unit.CommitAsync(cancellationToken).ConfigureAwait(false); }
        catch (FileStorageException exception) when (exception.Code == FileStorageError.Conflict) { throw GrantRefused(); }
    }

    // The intent stays accountable whether or not scheduling succeeds, so this is best effort: the upload's own failure is
    // what the caller sees, and the next quota check or explicit recovery reclaims the intent if the schedule cannot.
    private async Task ScheduleRecoveryAsync(TenantContext tenant)
    {
        if (recovery is null || !tenant.IsActive) return;
        try
        {
            using var deadline = new CancellationTokenSource(ScheduleTimeout, time);
            await recovery.ScheduleAsync(PropagatedTenantContext.Capture(tenant, time).ToJson(), deadline.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Deliberately ignored, as above.
        }
    }

    private async Task<ActorReference> AuthorizeUploadAsync(TenantContext tenant, ExecutionPrincipal? principal, Guid? fileId,
        CancellationToken cancellationToken)
    {
        if (fileId is { } existing)
        {
            await using var unit = await metadata.BeginAsync(tenant, cancellationToken).ConfigureAwait(false);
            await AuthorizeFileAsync(unit, tenant, principal, FilePermissions.Upload, existing, cancellationToken).ConfigureAwait(false);
        }
        else await AuthorizeTenantAsync(tenant, principal, FilePermissions.Upload, cancellationToken).ConfigureAwait(false);
        return Creator(tenant, principal);
    }

    private async Task AuthorizeTenantAsync(TenantContext tenant, ExecutionPrincipal? principal, string permission, CancellationToken cancellationToken)
    {
        var decision = await permissions.AuthorizeAsync(new AuthorizationRequest(tenant, principal, permission), cancellationToken).ConfigureAwait(false);
        if (!decision.Permitted) throw new FileAccessDeniedException(decision.Code!);
    }

    /// <summary>
    /// Decides one action on one file, after resolving the file within the effective tenant. A missing file and a file the
    /// subject may not use are answered alike, and a caller without the permission at all is refused with its code whether
    /// or not the file exists.
    /// </summary>
    private Task<FileAsset> AuthorizeFileAsync(IFileMetadataUnit unit, TenantContext tenant, ExecutionPrincipal? principal, string permission,
        Guid? fileId, CancellationToken cancellationToken) =>
        AuthorizeFileAsync(unit, tenant, principal, permission, fileId, () => new FileStorageException(FileStorageError.NotFound), cancellationToken);

    private async Task<FileAsset> AuthorizeFileAsync(IFileMetadataUnit unit, TenantContext tenant, ExecutionPrincipal? principal, string permission,
        Guid? fileId, Func<Exception> missing, CancellationToken cancellationToken)
    {
        var file = fileId is { } id ? await unit.FindFileAsync(id, cancellationToken).ConfigureAwait(false) : null;
        var decision = await permissions.AuthorizeAsync(new AuthorizationRequest(tenant, principal, permission)
        {
            Resource = file is null ? null : FilePermissions.Resource(file.TenantId, file.Id, OwnerOf(file))
        }, cancellationToken).ConfigureAwait(false);
        if (!decision.Permitted) throw decision.Code == AuthorizationCodes.ResourceNotFound ? missing() : new FileAccessDeniedException(decision.Code!);
        return file ?? throw missing();
    }

    // Whose resource a new file is: the evaluator's subject, that is a user's effective subject, or for a service identity
    // the user that its revalidated context names, or the service itself in a context it is the actor of.
    private static ActorReference Creator(TenantContext tenant, ExecutionPrincipal? principal) => principal switch
    {
        null => throw new FileAccessDeniedException(AuthorizationCodes.Unauthenticated),
        { ServiceScope: not null } => tenant.Actor,
        _ => new ActorReference(ActorKind.User, principal.EffectiveSubject.Id)
    };

    private static ActorReference? OwnerOf(FileAsset file) => string.IsNullOrEmpty(file.CreatedBy) ? null : new(file.CreatedByKind, file.CreatedBy);

    private static bool IsOpen(FileGrant grant, DateTimeOffset now) => grant.RevokedAt is null && grant.ConsumedAt is null && now < grant.ExpiresAt;

    private static FileStorageException GrantRefused() => new(FileStorageError.GrantRefused);

    private static async Task<byte[]> ReadHeadAsync(Stream content, int length, CancellationToken cancellationToken)
    {
        var head = new byte[length];
        var filled = 0;
        while (filled < length)
        {
            var read = await content.ReadAsync(head.AsMemory(filled), cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            filled += read;
        }
        // A short head is passed on as it is; the store then refuses the missing bytes against the declared length.
        return filled == length ? head : head[..filled];
    }

    private ValueTask ObserveAsync(FileStorageStage stage, Guid id, CancellationToken cancellationToken) =>
        observer?.ReachedAsync(stage, id, cancellationToken) ?? ValueTask.CompletedTask;

    private static async Task<FileRevision> RequiredAsync(IFileMetadataUnit unit, Guid fileId, Guid versionId, bool includeDeleted,
        CancellationToken cancellationToken) =>
        await unit.FindRevisionAsync(fileId, versionId, includeDeleted, cancellationToken).ConfigureAwait(false)
            ?? throw new FileStorageException(FileStorageError.NotFound);

    private static void RequireVersion(FileRevision revision, Guid expected)
    {
        if (expected == Guid.Empty || revision.Version != expected) throw new FileStorageException(FileStorageError.Conflict);
    }

    private static void RequireReleased(FileRevision revision)
    {
        if (revision.Quarantine != QuarantineState.Released) throw new FileStorageException(FileStorageError.Quarantined);
    }

    private FileDelivery Delivery(FileAsset file, FileRevision revision, FileGrantPurpose purpose) => new(revision.ContentType,
        purpose == FileGrantPurpose.Preview && policy.InlineContentTypes.Contains(revision.ContentType) ? FileDisposition.Inline : FileDisposition.Attachment,
        file.Name);

    private static FileVersionInfo Describe(FileRevision revision) => new(revision.FileId, revision.Id, revision.Length,
        revision.Sha256, revision.CreatedAt, (FileQuarantineState)revision.Quarantine, revision.IsDeleted, revision.Version, revision.ContentType);

    private static FileSummary Summarize(FileAsset file) => new(file.Id, file.Name, file.Container, file.Owner, OwnerOf(file), file.CreatedAt);

    private static FileGrantInfo Inform(FileGrant grant, DateTimeOffset now) => new(grant.Id, grant.FileId, grant.VersionId, (FileGrantPurpose)grant.Purpose,
        new ActorReference(grant.CreatedByKind, grant.CreatedBy), grant.CreatedAt, grant.ExpiresAt,
        grant.RevokedAt is not null ? FileGrantState.Revoked : grant.ConsumedAt is not null ? FileGrantState.Consumed
            : now >= grant.ExpiresAt ? FileGrantState.Expired : FileGrantState.Active);
}
