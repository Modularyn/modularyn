using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Modularyn.Framework.Modularity;
using Modularyn.Framework.Modularity.Abstractions;
using Modularyn.Framework.Persistence.EfCore;
using Modularyn.Framework.Security.Abstractions;
using Modularyn.Framework.Tenancy;
using Modularyn.Modules.Identity.Application.Contracts;
using Modularyn.Modules.Identity.Testing;
using Npgsql;
using Xunit;

namespace Modularyn.Modules.Identity.HttpApi.Tests;

// Identity's seams under SEC-04's tenancy middleware: the session's actor, its current memberships, and a delegation's
// tenant claim, with the real tenant directory on PostgreSQL.
[Trait("Requires", "PostgreSQL")]
[Collection(IdentityPostgreSqlCollection.Name)]
public sealed class TenancyIntegrationTests(ITestOutputHelper output)
{
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact(DisplayName = "TM-G02-025 a session resolves a tenant only for a current member, and a delegation only in its own tenant")]
    public async Task TM_G02_025_A_session_resolves_a_tenant_only_for_a_current_member()
    {
        await using var database = await IdentityDatabase.CreateAsync();
        var directorySchema = "tn_directory_" + Guid.NewGuid().ToString("N");
        var directory = TenantDirectoryModule.Create(new PostgreSqlPersistenceOptions(database.ConnectionString), directorySchema);
        try
        {
            await database.ExecuteAsync("CREATE SCHEMA " + IdentityDatabase.Quote(directorySchema));
            var composition = await new ModuleComposer().ComposeAsync(new([TenantDirectoryModule.Manifest(SemanticVersion.Parse("1.0.0"))], IdentityDatabase.Environment), Cancellation);
            Assert.Empty((await new PostgreSqlMigrationCoordinator(new PostgreSqlModuleRegistry([directory])).ApplyAsync(composition.Plan!, cancellationToken: Cancellation)).Diagnostics);
            var tenantA = Guid.NewGuid();
            var tenantB = Guid.NewGuid();
            foreach (var (tenant, name) in new[] { (tenantA, "Tenant A"), (tenantB, "Tenant B") })
                await database.ExecuteAsync("INSERT INTO " + IdentityDatabase.Quote(directorySchema) + ".\"Tenants\" (\"Id\", \"Name\", \"State\", \"Version\") VALUES (@id, @name, 1, @version)",
                    new NpgsqlParameter("id", tenant), new NpgsqlParameter("name", name), new NpgsqlParameter("version", Guid.NewGuid()));

            await using var server = await IdentityTestServer.StartAsync(database, services: services => services.AddModularynTenancy(directory), endpoints: application =>
            {
                application.UseModularynTenancy();
                application.MapGet("/t/{tenant}/whoami", (TenantExecution execution) => execution.Context is { } context
                    ? Results.Json(new { tenant = context.TenantId, actor = context.Actor.Subject }) : Results.StatusCode(StatusCodes.Status500InternalServerError))
                    .RequireTenantMember();
                // A tenant context authorizes nothing in identity: a member's own security-state change from a tenant
                // endpoint, offering that context as its authority, is refused before anything changes.
                application.MapPost("/t/{tenant}/security-state", async (TenantExecution execution, IAccountService accounts, CancellationToken cancellation) =>
                {
                    if (execution.Context is not { IsActive: true } context) return Results.StatusCode(StatusCodes.Status500InternalServerError);
                    try
                    {
                        await accounts.SecurityStateChangedAsync(IdentityAuthority.Host(context), Guid.Parse(context.Actor.Subject), cancellation);
                        return Results.NoContent();
                    }
                    catch (IdentityAuthorityException exception) when (exception.Code == IdentityAuthorityCodes.HostOperationRequired)
                    {
                        return Results.StatusCode(StatusCodes.Status403Forbidden);
                    }
                }).RequireTenantMember();
            });
            var accounts = server.Get<IAccountService>();
            var memberships = server.Get<ITenantMemberships>();
            var passwords = new Dictionary<string, string>();
            var ids = new Dictionary<string, Guid>();
            foreach (var name in new[] { "member-1", "outsider-1", "support-1", "user-1" })
            {
                passwords[name] = Generated.Password();
                ids[name] = (await accounts.CreateLocalAccountAsync(await server.Administrator.AccountsAsync(), name, new SecretText(passwords[name]), Cancellation)).AccountId!.Value;
            }
            Assert.True(await memberships.AddMemberAsync(await server.Administrator.MembershipsAsync(), ids["member-1"], tenantA, Cancellation));
            Assert.True(await memberships.AddMemberAsync(await server.Administrator.MembershipsAsync(), ids["user-1"], tenantA, Cancellation));
            Assert.True(await memberships.AddMemberAsync(await server.Administrator.MembershipsAsync(), ids["support-1"], tenantA, Cancellation));
            Assert.True(await memberships.AddMemberAsync(await server.Administrator.MembershipsAsync(), ids["support-1"], tenantB, Cancellation));
            var browsers = new Dictionary<string, Browser>();
            foreach (var name in passwords.Keys)
            {
                browsers[name] = server.Browser();
                Assert.Equal(HttpStatusCode.OK, (await browsers[name].SignInAsync(name, passwords[name])).StatusCode);
            }
            async Task<(HttpStatusCode Status, string? Actor, Guid? Tenant)> WhoAmI(Browser browser, Guid tenant, Guid? delegation = null)
            {
                var response = await browser.SendAsync(HttpMethod.Get, "/t/" + tenant.ToString("D") + "/whoami", delegation);
                if (response.StatusCode != HttpStatusCode.OK) return (response.StatusCode, null, null);
                using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Cancellation));
                return (response.StatusCode, body.RootElement.GetProperty("actor").GetString(), body.RootElement.GetProperty("tenant").GetGuid());
            }

            Assert.Equal((HttpStatusCode.OK, ids["member-1"].ToString("D"), tenantA), await WhoAmI(browsers["member-1"], tenantA));
            Assert.Equal(HttpStatusCode.NotFound, (await WhoAmI(browsers["member-1"], tenantB)).Status);
            Assert.Equal(HttpStatusCode.NotFound, (await WhoAmI(browsers["outsider-1"], tenantA)).Status);
            Assert.Equal(HttpStatusCode.Unauthorized, (await WhoAmI(server.Browser(), tenantA)).Status);

            // A removed membership is refused at the next request.
            Assert.True(await memberships.RemoveMemberAsync(await server.Administrator.MembershipsAsync(), ids["member-1"], tenantA, Cancellation));
            Assert.Equal(HttpStatusCode.NotFound, (await WhoAmI(browsers["member-1"], tenantA)).Status);

            // A delegation into tenant A resolves the actor's own membership and confines the request to tenant A.
            var user1 = (await server.Get<ISessionService>().ValidateAsync(Token(browsers["user-1"]), Cancellation))!.ToPrincipal();
            var grant = (await server.Get<IDelegationService>().IssueAsync(new DelegationRequest(ids["support-1"], ids["user-1"], ["item.read"], "Support case",
                tenantA, server.Clock.Now.AddMinutes(10)), user1, Cancellation)).GrantId!.Value;
            Assert.Equal((HttpStatusCode.OK, ids["support-1"].ToString("D"), tenantA), await WhoAmI(browsers["support-1"], tenantA, grant));
            Assert.Equal(HttpStatusCode.NotFound, (await WhoAmI(browsers["support-1"], tenantB, grant)).Status);
            Assert.Equal((HttpStatusCode.OK, ids["support-1"].ToString("D"), tenantB), await WhoAmI(browsers["support-1"], tenantB));

            // A tenant context is no authority for an identity write: the member's request is refused and nothing changes. An
            // identity administrator's host scope rotates the stamp and ends the member's sessions.
            var stamp = await database.ScalarAsync<string>("SELECT \"SecurityStamp\" FROM " + database.Table("Accounts") + " WHERE \"Id\" = @id",
                new NpgsqlParameter("id", ids["user-1"]));
            Assert.Equal(HttpStatusCode.Forbidden, (await browsers["user-1"].SendAsync(HttpMethod.Post, "/t/" + tenantA.ToString("D") + "/security-state",
                content: new StringContent(""))).StatusCode);
            Assert.Equal(stamp, await database.ScalarAsync<string>("SELECT \"SecurityStamp\" FROM " + database.Table("Accounts") + " WHERE \"Id\" = @id",
                new NpgsqlParameter("id", ids["user-1"])));
            Assert.Equal(HttpStatusCode.OK, (await WhoAmI(browsers["user-1"], tenantA)).Status);
            Assert.True(await accounts.SecurityStateChangedAsync(await server.Administrator.AccountsAsync(), ids["user-1"], Cancellation));
            Assert.NotEqual(stamp, await database.ScalarAsync<string>("SELECT \"SecurityStamp\" FROM " + database.Table("Accounts") + " WHERE \"Id\" = @id",
                new NpgsqlParameter("id", ids["user-1"])));
            Assert.Equal(HttpStatusCode.Unauthorized, (await WhoAmI(browsers["user-1"], tenantA)).Status);
            output.WriteLine("Members resolved their tenant; others, a removed membership and a delegation aimed at another tenant were refused; "
                + "a tenant context was refused as authority for a security-state change, which an identity administrator's host scope made.");
        }
        finally
        {
            await database.ExecuteAsync("DROP SCHEMA IF EXISTS " + IdentityDatabase.Quote(directorySchema) + " CASCADE");
        }
    }

    private static SessionToken Token(Browser browser) =>
        SessionToken.TryParse(browser.Session, out var token) ? token : throw new InvalidOperationException("The browser holds no session.");
}
