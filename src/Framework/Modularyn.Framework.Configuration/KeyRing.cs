using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Modularyn.Framework.Configuration;

public sealed class KeyRingOptions
{
    /// <summary>The application boundary that shares this key ring. Payloads protected under another name cannot be read.</summary>
    public string? ApplicationName { get; set; }

    /// <summary>The fully qualified, existing directory that holds the key ring, with owner-only access.</summary>
    public string? Directory { get; set; }

    /// <summary>The declared lifetime of each new key, from 7 to 90 days. A new key is created before the current one expires.</summary>
    public TimeSpan KeyLifetime { get; set; } = TimeSpan.FromDays(90);
}

public static class KeyRingServiceCollectionExtensions
{
    public static readonly TimeSpan MinimumKeyLifetime = TimeSpan.FromDays(7);
    public static readonly TimeSpan MaximumKeyLifetime = TimeSpan.FromDays(90);

    /// <summary>
    /// Adds ASP.NET Core data protection with a declared key ring: an application name, a key lifetime, and either the
    /// owner-only directory named by <c>{sectionPath}:Directory</c> or the supplied <paramref name="keyStore"/>. Startup
    /// is refused while the declaration is missing or invalid; keys are never generated in an undeclared location.
    /// The key store and application name are applied after any other configuration, so they cannot be replaced.
    /// </summary>
    public static IDataProtectionBuilder AddModularynDataProtection(this IServiceCollection services, IConfiguration configuration,
        string sectionPath = "Modularyn:DataProtection", Func<IServiceProvider, IXmlRepository>? keyStore = null)
    {
        services.AddModularynDeploymentOptions<KeyRingOptions>(configuration, sectionPath);
        services.AddSingleton<IValidateOptions<KeyRingOptions>>(new KeyRingOptionsValidation(sectionPath, keyStore is not null));
        services.AddSingleton(new DeclaredKeyRing());
        var builder = services.AddDataProtection();
        services.AddSingleton<IPostConfigureOptions<DataProtectionOptions>>(provider => new PostConfigureOptions<DataProtectionOptions>(Options.DefaultName,
            options => options.ApplicationDiscriminator = provider.GetRequiredService<IOptions<KeyRingOptions>>().Value.ApplicationName));
        services.AddSingleton<IPostConfigureOptions<KeyManagementOptions>>(provider => new PostConfigureOptions<KeyManagementOptions>(Options.DefaultName, options =>
        {
            var ring = provider.GetRequiredService<IOptions<KeyRingOptions>>().Value;
            options.NewKeyLifetime = ring.KeyLifetime;
            options.XmlRepository = keyStore is null ? new RestrictedFileSystemXmlRepository(ring.Directory!) : keyStore(provider);
        }));
        return builder;
    }
}

/// <summary>Present in a service collection only when <see cref="KeyRingServiceCollectionExtensions.AddModularynDataProtection"/> declared the key ring.</summary>
public sealed class DeclaredKeyRing
{
    internal DeclaredKeyRing() { }
}

/// <summary>Creates and checks key-ring directories with owner-only access.</summary>
public static class KeyRingDirectory
{
    private const UnixFileMode OwnerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private const UnixFileMode Shared = UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
        | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;

    /// <summary>
    /// Creates the directory with mode 0700 on Unix, or with a protected access list that grants only the current account
    /// on Windows, and confirms the result. An existing directory is not changed and must already be restricted.
    /// </summary>
    public static void Create(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!System.IO.Directory.Exists(path))
        {
            if (OperatingSystem.IsWindows())
            {
                var security = new DirectorySecurity();
                security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
                security.AddAccessRule(new FileSystemAccessRule(CurrentAccount(), FileSystemRights.FullControl,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
                new DirectoryInfo(path).Create(security);
            }
            else
            {
                System.IO.Directory.CreateDirectory(path, OwnerOnly);
            }
        }
        if (!IsRestricted(path)) throw new InvalidOperationException("The key-ring directory is accessible to other accounts.");
    }

    /// <summary>
    /// True when the path is a directory, not a link, and other accounts have no access: no group or other mode bits on
    /// Unix; on Windows, a protected access list whose allow rules name only the current account, LocalSystem or Administrators.
    /// </summary>
    public static bool IsRestricted(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var directory = new DirectoryInfo(path);
        if (!directory.Exists || directory.LinkTarget is not null) return false;
        return OperatingSystem.IsWindows() ? IsRestrictedOnWindows(directory) : (directory.UnixFileMode & Shared) == 0;
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static bool IsRestrictedOnWindows(DirectoryInfo directory)
    {
        var security = directory.GetAccessControl();
        if (!security.AreAccessRulesProtected) return false;
        var allowed = new[]
        {
            CurrentAccount(), new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null)
        };
        foreach (FileSystemAccessRule rule in security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier)))
            if (rule.AccessControlType == AccessControlType.Allow && !allowed.Contains(rule.IdentityReference)) return false;
        return true;
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static SecurityIdentifier CurrentAccount() => WindowsIdentity.GetCurrent().User
        ?? throw new InvalidOperationException("The current account has no security identifier.");
}

internal sealed class KeyRingOptionsValidation(string sectionPath, bool customStore) : IValidateOptions<KeyRingOptions>
{
    public ValidateOptionsResult Validate(string? name, KeyRingOptions options)
    {
        var failures = new List<string>();
        if (string.IsNullOrWhiteSpace(options.ApplicationName) || options.ApplicationName != options.ApplicationName.Trim()
            || options.ApplicationName.Length > 200 || options.ApplicationName.Any(char.IsControl))
            failures.Add($"'{sectionPath}:ApplicationName' must name the application boundary that shares the key ring, in at most 200 characters.");
        if (options.KeyLifetime < KeyRingServiceCollectionExtensions.MinimumKeyLifetime || options.KeyLifetime > KeyRingServiceCollectionExtensions.MaximumKeyLifetime)
            failures.Add($"'{sectionPath}:KeyLifetime' must declare a key lifetime from 7 to 90 days.");
        var key = sectionPath + ":Directory";
        if (customStore)
        {
            if (options.Directory is not null)
                failures.Add($"'{key}' must be absent when the application supplies its own key store.");
        }
        else if (string.IsNullOrWhiteSpace(options.Directory))
            failures.Add($"'{key}' must declare the key-ring location. Keys are never generated in an undeclared location.");
        else if (!Path.IsPathFullyQualified(options.Directory))
            failures.Add($"'{key}' must be a fully qualified directory.");
        else if (!System.IO.Directory.Exists(options.Directory))
            failures.Add($"'{key}' names a directory that does not exist. Mount or create it with owner-only access before startup.");
        else if (!KeyRingDirectory.IsRestricted(options.Directory))
            failures.Add($"'{key}' is a link or is accessible to other accounts. Restrict it to the application's account.");
        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}

/// <summary>
/// Stores each key as an XML file created with owner-only access and moved into place whole. It reads only regular
/// files, never follows links, prohibits DTDs, and refuses to read or write while the directory is not restricted.
/// </summary>
internal sealed class RestrictedFileSystemXmlRepository(string directory) : IXmlRepository
{
    private static readonly Regex FriendlyName = new(@"\A[A-Za-z0-9_-]{1,64}\z", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private static readonly XmlReaderSettings Reading = new() { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, IgnoreProcessingInstructions = true };

    public IReadOnlyCollection<XElement> GetAllElements()
    {
        EnsureRestricted();
        var elements = new List<XElement>();
        foreach (var file in new DirectoryInfo(directory).EnumerateFiles("*.xml", SearchOption.TopDirectoryOnly).OrderBy(file => file.Name, StringComparer.Ordinal))
        {
            if (file.LinkTarget is not null) continue;
            using var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var reader = XmlReader.Create(stream, Reading);
            elements.Add(XElement.Load(reader));
        }
        return elements;
    }

    public void StoreElement(XElement element, string friendlyName)
    {
        ArgumentNullException.ThrowIfNull(element);
        EnsureRestricted();
        var name = friendlyName is not null && FriendlyName.IsMatch(friendlyName) ? friendlyName : Guid.NewGuid().ToString("N");
        var temporary = Path.Join(directory, Guid.NewGuid().ToString("N") + ".tmp");
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        try
        {
            using (var stream = new FileStream(temporary, options))
            {
                element.Save(stream);
                stream.Flush(flushToDisk: true);
            }
            var destination = Path.Join(directory, name + ".xml");
            if (File.Exists(destination)) destination = Path.Join(directory, Guid.NewGuid().ToString("N") + ".xml");
            File.Move(temporary, destination, overwrite: false);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private void EnsureRestricted()
    {
        if (!KeyRingDirectory.IsRestricted(directory))
            throw new InvalidOperationException("The declared key-ring directory is missing, is a link or is accessible to other accounts.");
    }
}
