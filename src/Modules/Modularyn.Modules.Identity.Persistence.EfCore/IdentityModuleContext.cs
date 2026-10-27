using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Modularyn.Framework.Modularity.Abstractions;
using Modularyn.Framework.Persistence.Abstractions;
using Modularyn.Framework.Persistence.EfCore;
using Modularyn.Framework.Tenancy.Abstractions;
using Modularyn.Modules.Identity.Domain;

[assembly: InternalsVisibleTo("Modularyn.Modules.Identity.Persistence.EfCore.Tests")]

namespace Modularyn.Modules.Identity.Persistence.EfCore;

/// <summary>
/// A local account in ASP.NET Core Identity's user shape. Only the user name, its normalized form, the password verifier,
/// the security stamp and the concurrency stamp are mapped; e-mail, telephone, two-factor and lockout columns are not, so
/// no plaintext token, authenticator key or recovery code path of the Identity store has anywhere to write.
/// </summary>
public sealed class IdentityAccount : IdentityUser<Guid>, IHostScoped
{
    public bool Disabled { get; set; }
}

internal sealed class SessionRow : IHostScoped
{
    public Guid Id { get; set; }
    public byte[] TokenHash { get; set; } = [];
    public Guid AccountId { get; set; }
    public DateTimeOffset AuthenticatedAt { get; set; }
    public DateTimeOffset LastSeenAt { get; set; }
    public DateTimeOffset AbsoluteExpiresAt { get; set; }
    public string SecurityStamp { get; set; } = "";
    public DateTimeOffset? EndedAt { get; set; }
    public int? EndReason { get; set; }
}

internal sealed class ThrottleRow : IHostScoped
{
    public byte[] Key { get; set; } = [];
    public int Kind { get; set; }
    public int Failures { get; set; }
    public DateTimeOffset? WindowStartedAt { get; set; }
    public DateTimeOffset? LastFailureAt { get; set; }
    public DateTimeOffset? BlockedUntil { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

internal sealed class DelegationRow : IConcurrencyTracked, IHostScoped
{
    public Guid Id { get; set; }
    public string ActorId { get; set; } = "";
    public int ActorKind { get; set; }
    public string SubjectId { get; set; } = "";
    public int SubjectKind { get; set; }
    public string Scope { get; set; } = "";
    public string Purpose { get; set; } = "";
    public Guid? Tenant { get; set; }
    public string IssuedById { get; set; } = "";
    public int IssuedByKind { get; set; }
    public DateTimeOffset IssuedAt { get; set; }
    public DateTimeOffset NotBefore { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public string? RevokedById { get; set; }
    public int? RevokedByKind { get; set; }
    public Guid Version { get; set; }
}

internal sealed class AuthenticatorSeedRow : IHostScoped
{
    public Guid AccountId { get; set; }
    public int KeyVersion { get; set; }
    public byte[] Nonce { get; set; } = [];
    public byte[] Ciphertext { get; set; } = [];
    public byte[] Tag { get; set; } = [];
    public DateTimeOffset CreatedAt { get; set; }
}

internal sealed class RecoveryCodeRow : IConcurrencyTracked, IHostScoped
{
    public Guid Id { get; set; }
    public Guid AccountId { get; set; }
    public Guid SetId { get; set; }
    public byte[] Salt { get; set; } = [];
    public byte[] Verifier { get; set; } = [];
    public int Iterations { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ConsumedAt { get; set; }
    public DateTimeOffset? InvalidatedAt { get; set; }
    public Guid Version { get; set; }
}

/// <summary>A global account's membership of one tenant. It links a host-level account to a tenant; it grants nothing else.</summary>
internal sealed class TenantMembershipRow : IHostScoped
{
    public Guid AccountId { get; set; }
    public Guid TenantId { get; set; }
    public DateTimeOffset AddedAt { get; set; }
}

internal sealed class PasskeyCredentialRow : IHostScoped
{
    public Guid Id { get; set; }
    public Guid AccountId { get; set; }
    public byte[] CredentialId { get; set; } = [];
    public string RelyingPartyId { get; set; } = "";
    public byte[] PublicKey { get; set; } = [];
    public int Algorithm { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public int KeyVersion { get; set; }
    public byte[] IntegrityTag { get; set; } = [];
}

/// <summary>
/// The identity module's own context. It maps only the module's schema; its migrations are written by hand. It is
/// internal, so only this module can name it and so open owner-bound units over identity's records
/// (<see cref="IdentityOwnerScopes"/>).
/// </summary>
internal sealed class IdentityModuleContext(PostgreSqlModule module) : ModuleDbContext(module)
{
    protected override void ConfigureModule(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<IdentityAccount>(entity =>
        {
            entity.ToTable("Accounts");
            entity.HasKey(account => account.Id).HasName("PK_Accounts");
            entity.Property(account => account.Id).ValueGeneratedNever();
            entity.Property(account => account.UserName).HasMaxLength(256).IsRequired();
            entity.Property(account => account.NormalizedUserName).HasMaxLength(256).IsRequired();
            entity.HasIndex(account => account.NormalizedUserName).IsUnique().HasDatabaseName("IX_Accounts_NormalizedUserName");
            entity.Property(account => account.PasswordHash).IsRequired();
            entity.Property(account => account.SecurityStamp).IsRequired();
            entity.Property(account => account.ConcurrencyStamp).IsRequired().IsConcurrencyToken();
            entity.Ignore(account => account.Email);
            entity.Ignore(account => account.NormalizedEmail);
            entity.Ignore(account => account.EmailConfirmed);
            entity.Ignore(account => account.PhoneNumber);
            entity.Ignore(account => account.PhoneNumberConfirmed);
            entity.Ignore(account => account.TwoFactorEnabled);
            entity.Ignore(account => account.LockoutEnd);
            entity.Ignore(account => account.LockoutEnabled);
            entity.Ignore(account => account.AccessFailedCount);
        });
        modelBuilder.Entity<SessionRow>(entity =>
        {
            entity.ToTable("Sessions");
            entity.HasKey(session => session.Id).HasName("PK_Sessions");
            entity.Property(session => session.Id).ValueGeneratedNever();
            entity.HasIndex(session => session.TokenHash).IsUnique().HasDatabaseName("IX_Sessions_TokenHash");
            entity.HasIndex(session => session.AccountId).HasDatabaseName("IX_Sessions_AccountId");
            entity.HasOne<IdentityAccount>().WithMany().HasForeignKey(session => session.AccountId)
                .OnDelete(DeleteBehavior.Cascade).HasConstraintName("FK_Sessions_Accounts_AccountId");
        });
        modelBuilder.Entity<ThrottleRow>(entity =>
        {
            entity.ToTable("VerificationThrottle");
            entity.HasKey(row => row.Key).HasName("PK_VerificationThrottle");
            entity.Property(row => row.Key).ValueGeneratedNever();
        });
        modelBuilder.Entity<DelegationRow>(entity =>
        {
            entity.ToTable("Delegations");
            entity.HasKey(row => row.Id).HasName("PK_Delegations");
            entity.Property(row => row.Id).ValueGeneratedNever();
            entity.HasIndex(row => row.ActorId).HasDatabaseName("IX_Delegations_ActorId");
        });
        modelBuilder.Entity<AuthenticatorSeedRow>(entity =>
        {
            entity.ToTable("AuthenticatorSeeds");
            entity.HasKey(row => row.AccountId).HasName("PK_AuthenticatorSeeds");
            entity.Property(row => row.AccountId).ValueGeneratedNever();
            entity.HasOne<IdentityAccount>().WithOne().HasForeignKey<AuthenticatorSeedRow>(row => row.AccountId)
                .OnDelete(DeleteBehavior.Cascade).HasConstraintName("FK_AuthenticatorSeeds_Accounts_AccountId");
        });
        modelBuilder.Entity<RecoveryCodeRow>(entity =>
        {
            entity.ToTable("RecoveryCodes");
            entity.HasKey(row => row.Id).HasName("PK_RecoveryCodes");
            entity.Property(row => row.Id).ValueGeneratedNever();
            entity.HasIndex(row => row.AccountId).HasDatabaseName("IX_RecoveryCodes_AccountId");
            entity.HasOne<IdentityAccount>().WithMany().HasForeignKey(row => row.AccountId)
                .OnDelete(DeleteBehavior.Cascade).HasConstraintName("FK_RecoveryCodes_Accounts_AccountId");
        });
        modelBuilder.Entity<TenantMembershipRow>(entity =>
        {
            entity.ToTable("TenantMemberships");
            entity.HasKey(row => new { row.AccountId, row.TenantId }).HasName("PK_TenantMemberships");
            entity.HasIndex(row => row.TenantId).HasDatabaseName("IX_TenantMemberships_TenantId");
            entity.HasOne<IdentityAccount>().WithMany().HasForeignKey(row => row.AccountId)
                .OnDelete(DeleteBehavior.Cascade).HasConstraintName("FK_TenantMemberships_Accounts_AccountId");
        });
        modelBuilder.Entity<PasskeyCredentialRow>(entity =>
        {
            entity.ToTable("PasskeyCredentials");
            entity.HasKey(row => row.Id).HasName("PK_PasskeyCredentials");
            entity.Property(row => row.Id).ValueGeneratedNever();
            entity.HasIndex(row => row.CredentialId).IsUnique().HasDatabaseName("IX_PasskeyCredentials_CredentialId");
            entity.HasIndex(row => row.AccountId).HasDatabaseName("IX_PasskeyCredentials_AccountId");
            entity.HasOne<IdentityAccount>().WithMany().HasForeignKey(row => row.AccountId)
                .OnDelete(DeleteBehavior.Cascade).HasConstraintName("FK_PasskeyCredentials_Accounts_AccountId");
        });

        // PostgreSQL keeps whole microseconds, so every stored time must already be one (StoredTime). A time the module
        // failed to normalize is refused here, written or compared, rather than silently truncated by the database.
        var precision = new ValueConverter<DateTimeOffset, DateTimeOffset>(value => StoredTime.Require(value), value => value);
        foreach (var property in modelBuilder.Model.GetEntityTypes().SelectMany(entity => entity.GetProperties())
            .Where(property => property.ClrType == typeof(DateTimeOffset) || property.ClrType == typeof(DateTimeOffset?)).ToArray())
            property.SetValueConverter(precision);
    }
}

/// <summary>
/// The owner scopes under which the identity module changes its own host-scoped records through its own context. These
/// records are global: accounts, their credentials, sessions and delegations, the verification throttle and tenant
/// memberships exist apart from any tenant, so the module writes them in owner-bound units whether or not a request
/// resolved a tenant, and reaches no tenant data through them.
/// </summary>
internal static class IdentityOwnerScopes
{
    internal static readonly ModuleOwnerScope Accounts = ModuleOwnerScope.For<IdentityModuleContext>("identity.accounts");
    internal static readonly ModuleOwnerScope Sessions = ModuleOwnerScope.For<IdentityModuleContext>("identity.sessions");
    internal static readonly ModuleOwnerScope VerificationThrottle = ModuleOwnerScope.For<IdentityModuleContext>("identity.verification-throttle");
    internal static readonly ModuleOwnerScope Delegations = ModuleOwnerScope.For<IdentityModuleContext>("identity.delegations");
    internal static readonly ModuleOwnerScope Credentials = ModuleOwnerScope.For<IdentityModuleContext>("identity.credentials");
    internal static readonly ModuleOwnerScope Memberships = ModuleOwnerScope.For<IdentityModuleContext>("identity.memberships");
}

public static class IdentityPersistence
{
    /// <summary>The identity module's identity, equal to the contracts' module identifier.</summary>
    public const string ModuleId = "Modularyn.Modules.Identity";

    /// <summary>The module's PostgreSQL schema unless the deployment names another.</summary>
    public const string DefaultSchema = "identity";

    /// <summary>The module's migrations, in order, as its manifest declares them.</summary>
    public static IReadOnlyList<ModuleAsset> Migrations { get; } =
        Array.AsReadOnly([new ModuleAsset(IdentityFoundation.Id, SemanticVersion.Parse("1.0.0"))]);

    public static PostgreSqlModule CreateModule(PostgreSqlPersistenceOptions options, string schema = DefaultSchema) =>
        PostgreSqlModule.Create(ModuleId, schema, options, module => new IdentityModuleContext(module));
}
