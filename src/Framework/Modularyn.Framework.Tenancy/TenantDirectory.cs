using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Modularyn.Framework.Authorization.Abstractions;
using Modularyn.Framework.Ingress;
using Modularyn.Framework.Modularity.Abstractions;
using Modularyn.Framework.Persistence.Abstractions;
using Modularyn.Framework.Persistence.EfCore;
using Modularyn.Framework.Tenancy.Abstractions;

[assembly: InternalsVisibleTo("Modularyn.Framework.Tenancy.Tests")]

namespace Modularyn.Framework.Tenancy;

public enum TenantState { Active = 1, Suspended = 2 }

public sealed record TenantDirectoryEntry(Guid Id, string Name, TenantState State);

/// <summary>The tenant directory as resolution reads it: tenants and the configured domains that name them.</summary>
public interface ITenantDirectory
{
    ValueTask<TenantDirectoryEntry?> FindTenantAsync(Guid tenantId, CancellationToken cancellationToken);

    /// <summary>The tenant a canonical host names, or null when it names none.</summary>
    ValueTask<Guid?> FindTenantByDomainAsync(string host, CancellationToken cancellationToken);
}

/// <summary>The directory's PostgreSQL module: its own schema, context and migration history through the persistence adapter.</summary>
public static class TenantDirectoryModule
{
    public const string ModuleId = "Modularyn.Framework.Tenancy";
    public const string DefaultSchema = "modularyn_tenancy";

    public static IReadOnlyList<string> MigrationIds { get; } = [TenantDirectoryInitial.Id];

    /// <summary>Add the result to the host's one <see cref="PostgreSqlModuleRegistry"/>, so its migrations are coordinated and gated.</summary>
    public static PostgreSqlModule Create(PostgreSqlPersistenceOptions options, string schema = DefaultSchema) =>
        PostgreSqlModule.Create(ModuleId, schema, options, module => new TenantDirectoryContext(module));

    /// <summary>The directory's host operation, as authorization's registry binds it to this manifest.</summary>
    public static IReadOnlyList<HostOperationDefinition> HostOperations(SemanticVersion version) =>
        [new(ModuleId, version, TenantDirectoryAdministration.Operation)];

    /// <summary>The directory's manifest, declaring its host operation and migrations for composition and the activation gate.</summary>
    public static ModuleManifest Manifest(SemanticVersion version)
    {
        ArgumentNullException.ThrowIfNull(version);
        return new(ModuleId, version, definitions: HostOperations(version).Select(definition => definition.Declaration),
            migrations: MigrationIds.Select(id => new ModuleAsset(id, version)));
    }

    internal static void Require(PostgreSqlModule module)
    {
        ArgumentNullException.ThrowIfNull(module);
        if (module.ContextType != typeof(TenantDirectoryContext))
            throw new ArgumentException("Use the module that TenantDirectoryModule.Create returns.", nameof(module));
    }
}

/// <summary>Reads the directory afresh for every resolution, so a suspended tenant or removed domain takes effect at once.</summary>
public sealed class TenantDirectory : ITenantDirectory
{
    private readonly PostgreSqlModule module;

    public TenantDirectory(PostgreSqlModule module)
    {
        TenantDirectoryModule.Require(module);
        this.module = module;
    }

    public async ValueTask<TenantDirectoryEntry?> FindTenantAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        await using var context = (TenantDirectoryContext)module.CreateContext();
        return await context.Tenants.AsNoTracking().Where(row => row.Id == tenantId)
            .Select(row => new TenantDirectoryEntry(row.Id, row.Name, row.State)).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<Guid?> FindTenantByDomainAsync(string host, CancellationToken cancellationToken)
    {
        if (!TenantDomains.IsCanonicalHost(host)) return null;
        await using var context = (TenantDirectoryContext)module.CreateContext();
        return await context.Domains.AsNoTracking().Where(row => row.Host == host).Select(row => (Guid?)row.TenantId)
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>
/// Directory changes, each a host operation: they run only in an active host-scope context granted for
/// <see cref="OperationId"/>, and every domain must be a configured public origin's host.
/// </summary>
public sealed class TenantDirectoryAdministration
{
    public const string OperationId = "modularyn.tenancy.directory";

    /// <summary>The directory's host operation, which reaches the directory's tenants and domains and nothing else.</summary>
    public static HostOperation Operation { get; } = new(OperationId,
        [new HostReach(TenantDirectoryModule.ModuleId, "Tenants"), new HostReach(TenantDirectoryModule.ModuleId, "TenantDomains")]);

    private readonly PostgreSqlModule module;
    private readonly PublicOrigins? origins;

    public TenantDirectoryAdministration(PostgreSqlModule module, PublicOrigins? origins)
    {
        TenantDirectoryModule.Require(module);
        this.module = module;
        this.origins = origins;
    }

    public async Task<Guid> CreateTenantAsync(TenantContext host, string name, CancellationToken cancellationToken = default)
    {
        RequireAuthority(host);
        if (string.IsNullOrWhiteSpace(name) || name.Length > 200 || name != name.Trim() || name.Any(char.IsControl))
            throw new ArgumentException("A tenant name is 1 to 200 characters with no surrounding whitespace or control characters.", nameof(name));
        var id = Guid.NewGuid();
        await using var work = await EfCoreUnitOfWork.BeginAsync(module, host, cancellationToken: cancellationToken).ConfigureAwait(false);
        work.Context.Add(new TenantRecord { Id = id, Name = name, State = TenantState.Active });
        Require(await work.CompleteAsync(cancellationToken).ConfigureAwait(false));
        return id;
    }

    public async Task AddDomainAsync(TenantContext host, Guid tenantId, string domain, CancellationToken cancellationToken = default)
    {
        RequireAuthority(host);
        if (!TenantDomains.IsCanonicalHost(domain))
            throw new ArgumentException("A tenant domain is a lowercase ASCII host name without a port or trailing dot.", nameof(domain));
        if (origins is null || !origins.Values.Any(origin => string.Equals(new Uri(origin).Host, domain, StringComparison.OrdinalIgnoreCase)))
            throw new TenancyException("MTN020", "A tenant domain must be the host of a configured public origin.");
        await using var work = await EfCoreUnitOfWork.BeginAsync(module, host, cancellationToken: cancellationToken).ConfigureAwait(false);
        var context = (TenantDirectoryContext)work.Context;
        if (!await context.Tenants.AnyAsync(row => row.Id == tenantId, cancellationToken).ConfigureAwait(false))
            throw new TenancyException("MTN021", "The tenant does not exist.");
        if (await context.Domains.AnyAsync(row => row.Host == domain, cancellationToken).ConfigureAwait(false))
            throw new TenancyException("MTN020", "The domain already names a tenant.");
        context.Add(new TenantDomainRecord { Host = domain, TenantId = tenantId });
        Require(await work.CompleteAsync(cancellationToken).ConfigureAwait(false));
    }

    public async Task SetStateAsync(TenantContext host, Guid tenantId, TenantState state, CancellationToken cancellationToken = default)
    {
        RequireAuthority(host);
        if (!Enum.IsDefined(state)) throw new ArgumentOutOfRangeException(nameof(state));
        await using var work = await EfCoreUnitOfWork.BeginAsync(module, host, cancellationToken: cancellationToken).ConfigureAwait(false);
        var row = await ((TenantDirectoryContext)work.Context).Tenants.SingleOrDefaultAsync(row => row.Id == tenantId, cancellationToken).ConfigureAwait(false)
            ?? throw new TenancyException("MTN021", "The tenant does not exist.");
        row.State = state;
        Require(await work.CompleteAsync(cancellationToken).ConfigureAwait(false));
    }

    private static void RequireAuthority(TenantContext host)
    {
        ArgumentNullException.ThrowIfNull(host);
        if (host is not { Scope: TenantScope.Host, IsActive: true, HostGrant.OperationId: OperationId })
            throw new TenancyException("MTN009", "Directory changes require an active host scope granted for directory administration.");
    }

    private static void Require(UnitOfWorkResult result)
    {
        if (result.State != UnitOfWorkState.Committed)
            throw new TenancyException("MTN022", "The directory change did not commit.");
    }
}

/// <summary>A refused tenancy operation, with a stable code and no request values.</summary>
public sealed class TenancyException(string code, string message) : InvalidOperationException(message)
{
    public string Code { get; } = code;
}

internal static class TenantDomains
{
    /// <summary>A lowercase ASCII DNS host: labels of letters, digits and inner hyphens, no port and no trailing dot.</summary>
    internal static bool IsCanonicalHost(string? host)
    {
        if (string.IsNullOrEmpty(host) || host.Length > 253) return false;
        foreach (var label in host.Split('.'))
        {
            if (label.Length is 0 or > 63 || label[0] == '-' || label[^1] == '-') return false;
            if (!label.All(character => character is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-')) return false;
        }
        return true;
    }
}

internal sealed class TenantRecord : IHostScoped, IConcurrencyTracked
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public TenantState State { get; set; }
    public Guid Version { get; set; }
}

internal sealed class TenantDomainRecord : IHostScoped, IConcurrencyTracked
{
    public string Host { get; set; } = "";
    public Guid TenantId { get; set; }
    public Guid Version { get; set; }
}

internal sealed class TenantDirectoryContext(PostgreSqlModule module) : ModuleDbContext(module)
{
    public DbSet<TenantRecord> Tenants => Set<TenantRecord>();
    public DbSet<TenantDomainRecord> Domains => Set<TenantDomainRecord>();

    protected override void ConfigureModule(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<TenantRecord>(entity =>
        {
            entity.ToTable("Tenants");
            entity.HasKey(row => row.Id);
            entity.Property(row => row.Id).ValueGeneratedNever();
            entity.Property(row => row.Name).IsRequired();
            entity.Property(row => row.State).HasConversion<int>();
        });
        modelBuilder.Entity<TenantDomainRecord>(entity =>
        {
            entity.ToTable("TenantDomains");
            entity.HasKey(row => row.Host);
            entity.Property(row => row.Host).ValueGeneratedNever();
            entity.HasOne<TenantRecord>().WithMany().HasForeignKey(row => row.TenantId).OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(row => row.TenantId);
        });
    }
}

[DbContext(typeof(TenantDirectoryContext))]
[Migration(Id)]
internal sealed class TenantDirectoryInitial : ModuleMigration
{
    public const string Id = "20261026000100_TenantDirectory";

    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.EnsureSchema(Schema);
        migrationBuilder.CreateTable("Tenants", schema: Schema, columns: table => new
        {
            Id = table.Column<Guid>(type: "uuid", nullable: false),
            Name = table.Column<string>(type: "text", nullable: false),
            State = table.Column<int>(type: "integer", nullable: false),
            Version = table.Column<Guid>(type: "uuid", nullable: false)
        }, constraints: table => table.PrimaryKey("PK_Tenants", row => row.Id));
        migrationBuilder.CreateTable("TenantDomains", schema: Schema, columns: table => new
        {
            Host = table.Column<string>(type: "text", nullable: false),
            TenantId = table.Column<Guid>(type: "uuid", nullable: false),
            Version = table.Column<Guid>(type: "uuid", nullable: false)
        }, constraints: table =>
        {
            table.PrimaryKey("PK_TenantDomains", row => row.Host);
            table.ForeignKey("FK_TenantDomains_Tenants_TenantId", row => row.TenantId, principalSchema: Schema,
                principalTable: "Tenants", principalColumn: "Id", onDelete: ReferentialAction.Restrict);
        });
        migrationBuilder.CreateIndex("IX_TenantDomains_TenantId", "TenantDomains", "TenantId", schema: Schema);
    }
}
