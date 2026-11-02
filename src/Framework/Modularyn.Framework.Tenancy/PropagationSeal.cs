using System.Buffers.Text;
using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Modularyn.Framework.Configuration;
using Modularyn.Framework.Tenancy.Abstractions;

namespace Modularyn.Framework.Tenancy;

/// <summary>
/// Seals stored propagated references with the host's data-protection key ring: the seal is the key ring's protection of
/// the SHA-256 digest of a reference's canonical content and binding, under a purpose of its own. The ring's keys rotate,
/// expire for new seals and revoke as SEC-02 declares them, so a revoked key's references stop opening. Verification
/// compares digests in constant time and treats any unreadable or foreign seal as invalid.
/// </summary>
public sealed class PropagationSeal : IPropagationSeal
{
    /// <summary>The data-protection purpose of the seal, apart from every other protected payload of the host.</summary>
    public const string ProtectorPurpose = "Modularyn.Framework.Tenancy.PropagatedTenantContext.v2";

    private readonly IDataProtector protector;

    public PropagationSeal(IDataProtectionProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        protector = provider.CreateProtector(ProtectorPurpose);
    }

    public bool IsAvailable => true;

    public string Seal(ReadOnlySpan<byte> content) => Base64Url.EncodeToString(protector.Protect(SHA256.HashData(content)));

    public bool Verify(ReadOnlySpan<byte> content, string seal)
    {
        if (string.IsNullOrEmpty(seal) || seal.Length > 1024) return false;
        byte[] digest;
        try
        {
            digest = protector.Unprotect(Base64Url.DecodeFromChars(seal));
        }
        catch (Exception exception) when (exception is CryptographicException or FormatException)
        {
            return false;
        }
        return CryptographicOperations.FixedTimeEquals(digest, SHA256.HashData(content));
    }
}

/// <summary>
/// The seal tenancy registers: <see cref="PropagationSeal"/> over the key ring that <c>AddModularynDataProtection</c>
/// declared. Without a declared ring it is unavailable, so references are neither sealed nor opened, rather than sealed
/// with keys generated in an undeclared location.
/// </summary>
internal sealed class DeclaredPropagationSeal(IServiceProvider services) : IPropagationSeal
{
    private readonly Lazy<PropagationSeal?> inner = new(() => services.GetService<DeclaredKeyRing>() is null
        || services.GetService<IDataProtectionProvider>() is not { } provider ? null : new PropagationSeal(provider));

    public bool IsAvailable => inner.Value is not null;

    public string Seal(ReadOnlySpan<byte> content) => (inner.Value
        ?? throw new InvalidOperationException("Stored references are sealed with the declared key ring; declare it with AddModularynDataProtection.")).Seal(content);

    public bool Verify(ReadOnlySpan<byte> content, string seal) => inner.Value is { } available && available.Verify(content, seal);
}
