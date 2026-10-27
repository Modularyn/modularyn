using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Modularyn.Framework.Persistence.EfCore;

namespace Modularyn.Modules.Identity.Persistence.EfCore;

/// <summary>
/// Creates the identity module's tables in its own schema. Written by hand, without design-time tools; a real-provider
/// test compares every created column with the context's mapping.
/// </summary>
[DbContext(typeof(IdentityModuleContext))]
[Migration(Id)]
public sealed class IdentityFoundation : ModuleMigration
{
    public const string Id = "20261026120000_IdentityFoundation";

    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.EnsureSchema(Schema);
        migrationBuilder.CreateTable("Accounts", schema: Schema, columns: table => new
        {
            Id = table.Column<Guid>(type: "uuid", nullable: false),
            UserName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
            NormalizedUserName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
            PasswordHash = table.Column<string>(type: "text", nullable: false),
            SecurityStamp = table.Column<string>(type: "text", nullable: false),
            ConcurrencyStamp = table.Column<string>(type: "text", nullable: false),
            Disabled = table.Column<bool>(type: "boolean", nullable: false)
        }, constraints: table => table.PrimaryKey("PK_Accounts", row => row.Id));
        migrationBuilder.CreateIndex("IX_Accounts_NormalizedUserName", "Accounts", "NormalizedUserName", schema: Schema, unique: true);

        migrationBuilder.CreateTable("Sessions", schema: Schema, columns: table => new
        {
            Id = table.Column<Guid>(type: "uuid", nullable: false),
            TokenHash = table.Column<byte[]>(type: "bytea", nullable: false),
            AccountId = table.Column<Guid>(type: "uuid", nullable: false),
            AuthenticatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
            LastSeenAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
            AbsoluteExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
            SecurityStamp = table.Column<string>(type: "text", nullable: false),
            EndedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
            EndReason = table.Column<int>(type: "integer", nullable: true)
        }, constraints: table =>
        {
            table.PrimaryKey("PK_Sessions", row => row.Id);
            table.ForeignKey("FK_Sessions_Accounts_AccountId", row => row.AccountId, "Accounts", "Id", principalSchema: Schema,
                onDelete: ReferentialAction.Cascade);
        });
        migrationBuilder.CreateIndex("IX_Sessions_TokenHash", "Sessions", "TokenHash", schema: Schema, unique: true);
        migrationBuilder.CreateIndex("IX_Sessions_AccountId", "Sessions", "AccountId", schema: Schema);

        migrationBuilder.CreateTable("VerificationThrottle", schema: Schema, columns: table => new
        {
            Key = table.Column<byte[]>(type: "bytea", nullable: false),
            Kind = table.Column<int>(type: "integer", nullable: false),
            Failures = table.Column<int>(type: "integer", nullable: false),
            WindowStartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
            LastFailureAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
            BlockedUntil = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
            UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
        }, constraints: table => table.PrimaryKey("PK_VerificationThrottle", row => row.Key));

        migrationBuilder.CreateTable("Delegations", schema: Schema, columns: table => new
        {
            Id = table.Column<Guid>(type: "uuid", nullable: false),
            ActorId = table.Column<string>(type: "text", nullable: false),
            ActorKind = table.Column<int>(type: "integer", nullable: false),
            SubjectId = table.Column<string>(type: "text", nullable: false),
            SubjectKind = table.Column<int>(type: "integer", nullable: false),
            Scope = table.Column<string>(type: "text", nullable: false),
            Purpose = table.Column<string>(type: "text", nullable: false),
            Tenant = table.Column<Guid>(type: "uuid", nullable: true),
            IssuedById = table.Column<string>(type: "text", nullable: false),
            IssuedByKind = table.Column<int>(type: "integer", nullable: false),
            IssuedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
            NotBefore = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
            ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
            RevokedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
            RevokedById = table.Column<string>(type: "text", nullable: true),
            RevokedByKind = table.Column<int>(type: "integer", nullable: true),
            Version = table.Column<Guid>(type: "uuid", nullable: false)
        }, constraints: table => table.PrimaryKey("PK_Delegations", row => row.Id));
        migrationBuilder.CreateIndex("IX_Delegations_ActorId", "Delegations", "ActorId", schema: Schema);

        migrationBuilder.CreateTable("AuthenticatorSeeds", schema: Schema, columns: table => new
        {
            AccountId = table.Column<Guid>(type: "uuid", nullable: false),
            KeyVersion = table.Column<int>(type: "integer", nullable: false),
            Nonce = table.Column<byte[]>(type: "bytea", nullable: false),
            Ciphertext = table.Column<byte[]>(type: "bytea", nullable: false),
            Tag = table.Column<byte[]>(type: "bytea", nullable: false),
            CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
        }, constraints: table =>
        {
            table.PrimaryKey("PK_AuthenticatorSeeds", row => row.AccountId);
            table.ForeignKey("FK_AuthenticatorSeeds_Accounts_AccountId", row => row.AccountId, "Accounts", "Id", principalSchema: Schema,
                onDelete: ReferentialAction.Cascade);
        });

        migrationBuilder.CreateTable("RecoveryCodes", schema: Schema, columns: table => new
        {
            Id = table.Column<Guid>(type: "uuid", nullable: false),
            AccountId = table.Column<Guid>(type: "uuid", nullable: false),
            SetId = table.Column<Guid>(type: "uuid", nullable: false),
            Salt = table.Column<byte[]>(type: "bytea", nullable: false),
            Verifier = table.Column<byte[]>(type: "bytea", nullable: false),
            Iterations = table.Column<int>(type: "integer", nullable: false),
            CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
            ConsumedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
            InvalidatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
            Version = table.Column<Guid>(type: "uuid", nullable: false)
        }, constraints: table =>
        {
            table.PrimaryKey("PK_RecoveryCodes", row => row.Id);
            table.ForeignKey("FK_RecoveryCodes_Accounts_AccountId", row => row.AccountId, "Accounts", "Id", principalSchema: Schema,
                onDelete: ReferentialAction.Cascade);
        });
        migrationBuilder.CreateIndex("IX_RecoveryCodes_AccountId", "RecoveryCodes", "AccountId", schema: Schema);

        migrationBuilder.CreateTable("TenantMemberships", schema: Schema, columns: table => new
        {
            AccountId = table.Column<Guid>(type: "uuid", nullable: false),
            TenantId = table.Column<Guid>(type: "uuid", nullable: false),
            AddedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
        }, constraints: table =>
        {
            table.PrimaryKey("PK_TenantMemberships", row => new { row.AccountId, row.TenantId });
            table.ForeignKey("FK_TenantMemberships_Accounts_AccountId", row => row.AccountId, "Accounts", "Id", principalSchema: Schema,
                onDelete: ReferentialAction.Cascade);
        });
        migrationBuilder.CreateIndex("IX_TenantMemberships_TenantId", "TenantMemberships", "TenantId", schema: Schema);

        migrationBuilder.CreateTable("PasskeyCredentials", schema: Schema, columns: table => new
        {
            Id = table.Column<Guid>(type: "uuid", nullable: false),
            AccountId = table.Column<Guid>(type: "uuid", nullable: false),
            CredentialId = table.Column<byte[]>(type: "bytea", nullable: false),
            RelyingPartyId = table.Column<string>(type: "text", nullable: false),
            PublicKey = table.Column<byte[]>(type: "bytea", nullable: false),
            Algorithm = table.Column<int>(type: "integer", nullable: false),
            CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
            KeyVersion = table.Column<int>(type: "integer", nullable: false),
            IntegrityTag = table.Column<byte[]>(type: "bytea", nullable: false)
        }, constraints: table =>
        {
            table.PrimaryKey("PK_PasskeyCredentials", row => row.Id);
            table.ForeignKey("FK_PasskeyCredentials_Accounts_AccountId", row => row.AccountId, "Accounts", "Id", principalSchema: Schema,
                onDelete: ReferentialAction.Cascade);
        });
        migrationBuilder.CreateIndex("IX_PasskeyCredentials_CredentialId", "PasskeyCredentials", "CredentialId", schema: Schema, unique: true);
        migrationBuilder.CreateIndex("IX_PasskeyCredentials_AccountId", "PasskeyCredentials", "AccountId", schema: Schema);
    }
}
