using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Modularyn.Framework.Persistence.Abstractions;
using Modularyn.Framework.Tenancy.Abstractions;

namespace Modularyn.Framework.Persistence.EfCore;

/// <summary>
/// Tenant classification of a module model. Every root entity declares <see cref="ITenantScoped"/> or
/// <see cref="IHostScoped"/>; tenant-scoped types carry the tenant in every key, index and tenant-to-tenant reference,
/// so no uniqueness rule or reference can span tenants.
/// </summary>
internal static class TenantModel
{
    internal const string TenantMember = nameof(ITenantScoped.TenantId);

    /// <summary>Refuses undeclared, doubly declared or owned declarations, and configures each root tenant property.</summary>
    internal static void Classify(ModelBuilder builder)
    {
        foreach (var entity in builder.Model.GetEntityTypes().ToArray())
        {
            var tenant = typeof(ITenantScoped).IsAssignableFrom(entity.ClrType);
            var host = typeof(IHostScoped).IsAssignableFrom(entity.ClrType);
            if (entity.IsOwned())
            {
                if (tenant || host)
                    Refuse("MPS030", $"Owned type '{entity.DisplayName()}' follows its owner's scope; declare ITenantScoped or IHostScoped on the owner only.");
                continue;
            }
            if (entity.HasSharedClrType || entity.IsPropertyBag)
                Refuse("MPS030", $"'{entity.DisplayName()}' is a shared-type entity. Map an explicit entity type that declares ITenantScoped or IHostScoped.");
            if (tenant == host)
                Refuse("MPS030", $"Entity '{entity.DisplayName()}' must declare exactly one of ITenantScoped and IHostScoped.");
            if (!tenant || entity.BaseType is not null) continue;
            var property = entity.FindProperty(TenantMember);
            if (property is null || property.IsShadowProperty() || property.ClrType != typeof(Guid) || property.PropertyInfo is null)
                Refuse("MPS031", $"Tenant-scoped entity '{entity.DisplayName()}' must map ITenantScoped.TenantId as a public, non-nullable Guid property.");
            builder.Entity(entity.ClrType).Property(TenantMember).IsRequired().ValueGeneratedNever();
        }
    }

    /// <summary>Requires the tenant in keys, indexes and references between tenant-scoped types.</summary>
    internal static void Validate(IReadOnlyModel model)
    {
        foreach (var entity in model.GetEntityTypes())
        {
            var tenant = TenantProperty(entity);
            if (tenant is not null)
            {
                foreach (var key in entity.GetDeclaredKeys())
                    if (!key.Properties.Contains(tenant))
                        Refuse("MPS032", $"Every key of tenant-scoped '{entity.DisplayName()}' must include its tenant.");
                foreach (var index in entity.GetDeclaredIndexes())
                    if (!index.Properties.Contains(tenant))
                        Refuse("MPS032", $"Every index of tenant-scoped '{entity.DisplayName()}' must include its tenant.");
            }
            foreach (var foreignKey in entity.GetDeclaredForeignKeys())
            {
                if (foreignKey.IsOwnership) continue;
                var principalTenant = TenantProperty(foreignKey.PrincipalEntityType);
                if (principalTenant is null) continue;
                if (tenant is null)
                    Refuse("MPS032", $"Host-scoped '{entity.DisplayName()}' cannot reference tenant-scoped '{foreignKey.PrincipalEntityType.DisplayName()}'.");
                var position = IndexOf(foreignKey.PrincipalKey.Properties, principalTenant);
                if (position < 0 || !ReferenceEquals(foreignKey.Properties[position], tenant))
                    Refuse("MPS032", $"A reference from '{entity.DisplayName()}' to '{foreignKey.PrincipalEntityType.DisplayName()}' must carry the same tenant.");
            }
        }
    }

    /// <summary>
    /// The property that carries a type's tenant: the root's <c>TenantId</c>, or, for an owned type, the ownership key
    /// property that copies its owner's tenant. Null for host-scoped data.
    /// </summary>
    internal static IReadOnlyProperty? TenantProperty(IReadOnlyEntityType type)
    {
        if (type.IsOwned())
        {
            var ownership = type.FindOwnership();
            if (ownership is null) return null;
            var owner = TenantProperty(ownership.PrincipalEntityType);
            if (owner is null) return null;
            var position = IndexOf(ownership.PrincipalKey.Properties, owner);
            return position < 0 ? null : ownership.Properties[position];
        }
        var root = type.GetRootType();
        return typeof(ITenantScoped).IsAssignableFrom(root.ClrType) ? root.FindProperty(TenantMember) : null;
    }

    /// <summary>Tenant-carrying tables in the module schema, with their tenant columns, for migration validation.</summary>
    internal static IReadOnlyDictionary<string, string> TenantTables(IReadOnlyModel model, string schema)
    {
        var tables = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entity in model.GetEntityTypes())
        {
            var tenant = TenantProperty(entity);
            var table = entity.GetTableName();
            if (tenant is null || table is null || (entity.GetSchema() ?? schema) != schema) continue;
            var column = tenant.GetColumnName(StoreObjectIdentifier.Table(table, entity.GetSchema()));
            if (column is not null) tables.TryAdd(table, column);
        }
        return tables;
    }

    private static int IndexOf(IReadOnlyList<IReadOnlyProperty> properties, IReadOnlyProperty property)
    {
        for (var index = 0; index < properties.Count; index++)
            if (ReferenceEquals(properties[index], property)) return index;
        return -1;
    }

    private static void Refuse(string code, string message) => throw new PersistenceContractException(code, message);
}
