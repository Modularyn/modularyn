namespace Modularyn.Framework.Modularity.Abstractions;

public sealed record CompositionEnvironment
{
    public CompositionEnvironment(SemanticVersion runtimeVersion, SemanticVersion frameworkVersion)
    {
        RuntimeVersion = runtimeVersion ?? throw new ArgumentNullException(nameof(runtimeVersion));
        FrameworkVersion = frameworkVersion ?? throw new ArgumentNullException(nameof(frameworkVersion));
    }
    public SemanticVersion RuntimeVersion { get; }
    public SemanticVersion FrameworkVersion { get; }
}

public sealed class CompositionSelection
{
    public CompositionSelection(IEnumerable<ModuleManifest> modules, CompositionEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(modules);
        Modules = DeclarationGuard.Copy(modules);
        Environment = environment ?? throw new ArgumentNullException(nameof(environment));
    }
    public IReadOnlyList<ModuleManifest> Modules { get; }
    public CompositionEnvironment Environment { get; }
}

public sealed record CompositionDiagnostic
{
    public CompositionDiagnostic(string code, string message, IEnumerable<string> moduleIds)
    {
        Code = DeclarationGuard.Name(code);
        if (!Code.StartsWith("MDC", StringComparison.Ordinal) || Code.Length < 6 || !Code[3..].All(char.IsAsciiDigit))
            throw new ArgumentException("Composition diagnostic codes must be MDC followed by at least three digits.", nameof(code));
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        Message = message;
        ArgumentNullException.ThrowIfNull(moduleIds);
        ModuleIds = DeclarationGuard.Copy(moduleIds.Select(DeclarationGuard.Name));
    }
    public string Code { get; }
    public string Message { get; }
    public IReadOnlyList<string> ModuleIds { get; }
}

/// <summary>
/// Runs before module construction. Lower orders run first; equal orders retain registration order.
/// Any returned diagnostic refuses the selection. Validators cannot alter the immutable selection.
/// </summary>
public interface ICompositionValidator
{
    int Order { get; }
    ValueTask<IReadOnlyList<CompositionDiagnostic>> ValidateAsync(CompositionSelection selection, CancellationToken cancellationToken);
}
