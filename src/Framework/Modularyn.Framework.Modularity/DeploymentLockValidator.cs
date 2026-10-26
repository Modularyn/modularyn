using System.Security.Cryptography;
using System.Text;
using Modularyn.Framework.Modularity.Abstractions;

namespace Modularyn.Framework.Modularity;

/// <summary>
/// Refuses a selection that differs from the deployment lock, or whose locked package files are missing, altered
/// or reached through links. It reads the lock and hashes every locked file on each validation, so a later
/// activation attempt observes the files as they are then. Diagnostics name modules, lock locations and paths
/// relative to the content root; they never include file system locations or exception text.
/// </summary>
public sealed class DeploymentLockValidator : ICompositionTrustValidator
{
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private readonly string lockPath;
    private readonly string lockName;
    private readonly string contentRoot;

    /// <param name="lockPath">The deployment lock file of the release.</param>
    /// <param name="contentRoot">The directory that locked package paths are relative to, usually the application base directory.</param>
    /// <param name="order">The position among trust validators.</param>
    public DeploymentLockValidator(string lockPath, string contentRoot, int order = 0)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(lockPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentRoot);
        this.lockPath = Path.GetFullPath(lockPath);
        lockName = Path.GetFileName(this.lockPath);
        this.contentRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(contentRoot));
        Order = order;
    }

    public int Order { get; }

    public async ValueTask<IReadOnlyList<CompositionDiagnostic>> ValidateAsync(CompositionSelection selection, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(selection);
        var selected = selection.Modules.Select(module => module.Id).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var (deploymentLock, refusal) = await ReadLockAsync(selected, cancellationToken).ConfigureAwait(false);
        if (deploymentLock is null) return [refusal!];

        var diagnostics = new List<CompositionDiagnostic>();
        CompareSelection(selection, deploymentLock, diagnostics);
        await VerifyPackagesAsync(deploymentLock, diagnostics, cancellationToken).ConfigureAwait(false);
        return diagnostics;
    }

    private async Task<(DeploymentLock? Lock, CompositionDiagnostic? Refusal)> ReadLockAsync(string[] selected, CancellationToken cancellationToken)
    {
        string text;
        try
        {
            await using var stream = new FileStream(lockPath, new FileStreamOptions
            {
                Mode = FileMode.Open, Access = FileAccess.Read, Share = FileShare.Read, Options = FileOptions.Asynchronous | FileOptions.SequentialScan
            });
            if (stream.Length > DeploymentLock.MaximumBytes)
                return (null, Unavailable(selected, $"is larger than {DeploymentLock.MaximumBytes} bytes"));
            var bytes = new byte[stream.Length];
            await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
            if (stream.ReadByte() != -1) return (null, Unavailable(selected, "changed while it was read"));
            text = StrictUtf8.GetString(bytes);
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return (null, Unavailable(selected, "was not found"));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return (null, Unavailable(selected, "could not be read"));
        }
        catch (DecoderFallbackException)
        {
            return (null, Invalid(selected, "the lock is not UTF-8 text"));
        }

        try
        {
            return (DeploymentLock.Parse(text), null);
        }
        catch (DeploymentLockFormatException exception)
        {
            return (null, Invalid(selected, exception.Message));
        }
    }

    private CompositionDiagnostic Unavailable(string[] selected, string reason) => new(DeploymentTrustDiagnosticCodes.LockUnavailable,
        $"The deployment lock '{lockName}' {reason}, so no module can be verified. Deploy the lock manifest produced for this release before activation.", selected);

    private CompositionDiagnostic Invalid(string[] selected, string reason) => new(DeploymentTrustDiagnosticCodes.LockInvalid,
        $"The deployment lock '{lockName}' is not a valid format {DeploymentLock.FormatVersion} lock: {reason}. Deploy the unmodified lock manifest produced for this release.", selected);

    private static void CompareSelection(CompositionSelection selection, DeploymentLock deploymentLock, List<CompositionDiagnostic> diagnostics)
    {
        var locked = deploymentLock.Modules.ToDictionary(module => module.Id, StringComparer.Ordinal);
        foreach (var manifest in selection.Modules.OrderBy(module => module.Id, StringComparer.Ordinal).ThenBy(module => module.Version.Value, StringComparer.Ordinal))
        {
            if (!locked.TryGetValue(manifest.Id, out var entry))
            {
                diagnostics.Add(new(DeploymentTrustDiagnosticCodes.UnlistedModule,
                    $"Module '{manifest.Id}' {manifest.Version} is not listed in the deployment lock. Deploy the reviewed release that lists it, or remove '{manifest.Id}' from the selection.",
                    [manifest.Id]));
                continue;
            }
            if (manifest.Version.Value != entry.Version.Value)
            {
                diagnostics.Add(new(DeploymentTrustDiagnosticCodes.VersionMismatch,
                    $"Module '{manifest.Id}' is selected at {manifest.Version}, but the deployment lock records {entry.Version}. Deploy the locked version.",
                    [manifest.Id]));
                continue;
            }
            if (ModuleManifestDigest.Compute(manifest) != entry.ManifestSha256)
                diagnostics.Add(new(DeploymentTrustDiagnosticCodes.ManifestMismatch,
                    $"Module '{manifest.Id}' {manifest.Version} declares dependencies, provisions, routes or assets that differ from its locked manifest digest. Deploy the reviewed package unchanged.",
                    [manifest.Id]));
            if (!manifest.Migrations.SequenceEqual(entry.Migrations))
                diagnostics.Add(new(DeploymentTrustDiagnosticCodes.MigrationMismatch,
                    $"Module '{manifest.Id}' {manifest.Version} declares migrations [{Describe(manifest.Migrations)}], but the deployment lock records [{Describe(entry.Migrations)}]. Deploy the reviewed package and its lock together.",
                    [manifest.Id]));
        }

        var selected = selection.Modules.Select(module => module.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var entry in deploymentLock.Modules.Where(module => !selected.Contains(module.Id)).OrderBy(module => module.Id, StringComparer.Ordinal))
            diagnostics.Add(new(DeploymentTrustDiagnosticCodes.UnselectedModule,
                $"The deployment lock lists module '{entry.Id}' {entry.Version}, but the selection does not include it. Select exactly the locked modules, or deploy the lock that matches this selection.",
                [entry.Id]));
    }

    private static string Describe(IEnumerable<ModuleAsset> migrations) => string.Join(", ", migrations.Select(item => $"{item.Name} {item.Version}"));

    private async Task VerifyPackagesAsync(DeploymentLock deploymentLock, List<CompositionDiagnostic> diagnostics, CancellationToken cancellationToken)
    {
        var outcomes = new Dictionary<string, FileOutcome>(StringComparer.Ordinal);
        foreach (var module in deploymentLock.Modules.OrderBy(module => module.Id, StringComparer.Ordinal))
        foreach (var file in module.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!outcomes.TryGetValue(file.Path, out var outcome))
                outcomes[file.Path] = outcome = await VerifyAsync(file, cancellationToken).ConfigureAwait(false);
            var problem = outcome switch
            {
                FileOutcome.Missing => (DeploymentTrustDiagnosticCodes.PackageFileMissing, "is missing from the content root. Redeploy the reviewed package"),
                FileOutcome.Altered => (DeploymentTrustDiagnosticCodes.PackageContentMismatch, "does not match the content hash in the deployment lock. Redeploy the reviewed package"),
                FileOutcome.Unsafe => (DeploymentTrustDiagnosticCodes.PackageFileUnsafe, "is a link, is reached through a link or is not a regular file. Deploy package files as regular files under the content root"),
                FileOutcome.Unreadable => (DeploymentTrustDiagnosticCodes.PackageFileUnreadable, "could not be read. Grant the host read access to its package files or redeploy them"),
                _ => default((string Code, string Reason)?)
            };
            if (problem is { } found)
                diagnostics.Add(new(found.Code, $"Package file '{file.Path}' of module '{module.Id}' {found.Reason}.", [module.Id]));
        }
    }

    private async Task<FileOutcome> VerifyAsync(LockedFile file, CancellationToken cancellationToken)
    {
        var segments = file.Path.Split('/');
        var current = contentRoot;
        try
        {
            for (var index = 0; index < segments.Length; index++)
            {
                current = Path.Join(current, segments[index]);
                if (new FileInfo(current).LinkTarget is not null) return FileOutcome.Unsafe;
                var last = index == segments.Length - 1;
                if (Directory.Exists(current))
                {
                    if (new DirectoryInfo(current).LinkTarget is not null || last) return FileOutcome.Unsafe;
                    continue;
                }
                if (!File.Exists(current)) return FileOutcome.Missing;
                if (!last) return FileOutcome.Unsafe;
            }
            var full = Path.GetFullPath(current);
            if (!full.StartsWith(contentRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal)) return FileOutcome.Unsafe;

            await using var stream = new FileStream(full, new FileStreamOptions
            {
                Mode = FileMode.Open, Access = FileAccess.Read, Share = FileShare.Read, Options = FileOptions.Asynchronous | FileOptions.SequentialScan
            });
            var actual = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
            return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(actual), Encoding.ASCII.GetBytes(file.Sha256))
                ? FileOutcome.Verified : FileOutcome.Altered;
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return FileOutcome.Missing;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return FileOutcome.Unreadable;
        }
    }

    private enum FileOutcome { Verified, Missing, Altered, Unsafe, Unreadable }
}

public static class DeploymentTrustDiagnosticCodes
{
    public const string LockUnavailable = "MDC200";
    public const string LockInvalid = "MDC201";
    public const string UnlistedModule = "MDC202";
    public const string UnselectedModule = "MDC203";
    public const string VersionMismatch = "MDC204";
    public const string ManifestMismatch = "MDC205";
    public const string MigrationMismatch = "MDC206";
    public const string PackageFileMissing = "MDC210";
    public const string PackageContentMismatch = "MDC211";
    public const string PackageFileUnsafe = "MDC212";
    public const string PackageFileUnreadable = "MDC213";
}
