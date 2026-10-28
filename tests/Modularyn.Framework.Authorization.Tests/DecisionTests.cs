using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Modularyn.Framework.Authorization.Abstractions;
using Modularyn.Framework.Modularity.Abstractions;
using Modularyn.Framework.Security.Abstractions;
using Modularyn.Framework.Tenancy;
using Modularyn.Framework.Tenancy.Abstractions;
using Modularyn.Framework.Tenancy.Tests;
using Xunit;

namespace Modularyn.Framework.Authorization.Tests;

// Unit tests of the evaluator's decisions over in-memory authority; the PostgreSQL tests repeat them on the real store.
public sealed class DecisionTests(ITestOutputHelper output)
{
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    private static AuthorizationRequest Request(Unit unit, string permission, ResourceReference? resource = null, bool current = false) =>
        new(unit.Context, unit.Principal, permission) { Resource = resource, RequireCurrent = current };

    [Fact]
    public async Task TM_G02_019_Every_decision_denies_by_default_and_refuses_when_its_data_is_unavailable()
    {
        await using var authorization = UnitAuthorization.Create();
        var tenant = authorization.Directory.Add("Tenant A");
        var other = authorization.Directory.Add("Tenant B");
        foreach (var subject in new[] { "user-1", "user-2", "support-1" }) authorization.Seams.Membership.Add(subject, tenant);
        authorization.Authority.Grant(tenant, "user-1", Items.Read, Items.Export);
        var evaluator = authorization.Get<IPermissionEvaluator>();
        var unit = await authorization.EnterAsync("user-1", tenant);
        Assert.True((await evaluator.AuthorizeAsync(Request(unit, Items.Read), Cancellation)).Permitted);

        // Nothing is permitted without a principal, for a permission no composed module declares, or for a principal that
        // is not the actor the context was established for.
        Assert.Equal(AuthorizationCodes.Unauthenticated, (await evaluator.AuthorizeAsync(new(unit.Context, null, Items.Read), Cancellation)).Code);
        Assert.Equal(AuthorizationCodes.UndefinedPermission, (await evaluator.AuthorizeAsync(Request(unit, "other.read"), Cancellation)).Code);
        var stranger = ExecutionPrincipal.ForUser(new PrincipalReference("user-2", IdentityKind.User), Guid.NewGuid(), DateTimeOffset.UtcNow);
        Assert.Equal(AuthorizationCodes.PrincipalMismatch, (await evaluator.AuthorizeAsync(new(unit.Context, stranger, Items.Read), Cancellation)).Code);
        Assert.Equal(AuthorizationCodes.NotHeld, (await evaluator.AuthorizeAsync(Request(unit, Items.Update), Cancellation)).Code);

        // Host scope carries no tenant permissions, and an ended unit decides nothing.
        authorization.Seams.Authority.Grant("operator-1", "test.operation");
        var (hostScope, host) = await Units.EnterHostAsync(authorization.Services, "operator-1", "test.operation");
        await using (hostScope)
            Assert.Equal(AuthorizationCodes.NoTenantContext, (await evaluator.AuthorizeAsync(new(host, unit.Principal, Items.Read), Cancellation)).Code);
        var ended = await authorization.EnterAsync("user-1", tenant, unit.Principal);
        await ended.DisposeAsync();
        Assert.Equal(AuthorizationCodes.NoTenantContext, (await evaluator.AuthorizeAsync(Request(ended, Items.Read), Cancellation)).Code);

        // A delegation covers only its scope, tenant and window; a service identity only its declared scope.
        var delegation = ExecutionPrincipal.ForDelegation(new PrincipalReference("support-1", IdentityKind.User), Guid.NewGuid(), DateTimeOffset.UtcNow,
            new PrincipalReference("user-1", IdentityKind.User), new DelegatedAuthority(Guid.NewGuid(), [Items.Read], "Support case", tenant,
                DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(10)));
        var delegated = await authorization.EnterAsync("support-1", tenant, delegation);
        Assert.True((await evaluator.AuthorizeAsync(Request(delegated, Items.Read), Cancellation)).Permitted);
        Assert.Equal(AuthorizationCodes.OutsideCeiling, (await evaluator.AuthorizeAsync(Request(delegated, Items.Export), Cancellation)).Code);
        // The delegation's subject must still be a member of the tenant, checked now.
        authorization.Seams.Membership.Remove("user-1", tenant);
        Assert.Equal(AuthorizationCodes.SubjectNotMember, (await evaluator.AuthorizeAsync(Request(delegated, Items.Read), Cancellation)).Code);
        authorization.Seams.Membership.Add("user-1", tenant);
        authorization.Seams.Membership.Add("support-1", other);
        var elsewhere = await authorization.EnterAsync("support-1", other, delegation);
        Assert.Equal(AuthorizationCodes.OutsideCeiling, (await evaluator.AuthorizeAsync(Request(elsewhere, Items.Read), Cancellation)).Code);
        var job = ExecutionPrincipal.ForService(new PrincipalReference(Items.Service, IdentityKind.Service), [Items.Reindex]);
        Assert.Equal(AuthorizationCodes.OutsideCeiling, (await evaluator.AuthorizeAsync(new(unit.Context, job, Items.Read), Cancellation)).Code);

        // A current-profile decision rechecks the session now; an ended one is refused even where a bounded decision is not.
        authorization.Freshness.End(unit.Principal.SessionId!.Value);
        Assert.Equal(AuthorizationCodes.PrincipalNotCurrent, (await evaluator.AuthorizeAsync(Request(unit, Items.Read, current: true), Cancellation)).Code);
        Assert.Equal(AuthorizationCodes.PrincipalNotCurrent, (await evaluator.AuthorizeAsync(Request(unit, Items.Export), Cancellation)).Code);

        // Unavailable authority, membership or freshness refuses, transiently; nothing falls back to a permit.
        var fresh = await authorization.EnterAsync("user-1", tenant);
        authorization.Get<TenantCache>().InvalidateTenant(tenant);
        authorization.Authority.Unavailable = true;
        var unavailable = await evaluator.AuthorizeAsync(Request(fresh, Items.Read), Cancellation);
        Assert.Equal((AuthorizationCodes.Unavailable, true), (unavailable.Code, unavailable.IsTransient));
        Assert.Equal(AuthorizationCodes.Unavailable, (await evaluator.AuthorizeAsync(Request(fresh, Items.Export), Cancellation)).Code);
        authorization.Authority.Unavailable = false;
        authorization.Seams.Membership.Failing = true;
        Assert.Equal(AuthorizationCodes.Unavailable, (await evaluator.AuthorizeAsync(Request(fresh, Items.Export), Cancellation)).Code);
        authorization.Seams.Membership.Failing = false;
        await using (var withoutFreshness = UnitAuthorization.Create(services => services.RemoveAll<IPrincipalFreshness>()))
        {
            var tenantC = withoutFreshness.Directory.Add("Tenant C");
            withoutFreshness.Seams.Membership.Add("user-1", tenantC);
            withoutFreshness.Authority.Grant(tenantC, "user-1", Items.Export);
            var lone = await withoutFreshness.EnterAsync("user-1", tenantC);
            Assert.Equal(AuthorizationCodes.Unavailable, (await withoutFreshness.Get<IPermissionEvaluator>().AuthorizeAsync(Request(lone, Items.Export), Cancellation)).Code);
        }
        // The worker entry point defers work whose authority cannot be read, and denies work whose producer lacks it.
        var runner = authorization.Get<AuthorizedWorkRunner>();
        var reindex = ExecutionPrincipal.ForService(new PrincipalReference(Items.Service, IdentityKind.Service), [Items.Update]);
        var propagated = PropagatedTenantContext.Capture(fresh.Context).ToJson();
        authorization.Authority.Unavailable = true;
        Assert.Equal((TenantWorkDisposition.Deferred, AuthorizationCodes.Unavailable), Disposition(await runner.RunAsync(reindex, propagated, Items.Update, Never, Cancellation)));
        authorization.Authority.Unavailable = false;
        Assert.Equal((TenantWorkDisposition.Denied, AuthorizationCodes.NotHeld), Disposition(await runner.RunAsync(reindex, propagated, Items.Update, Never, Cancellation)));
        output.WriteLine("Every refusal above carried its stable code; none permitted.");
    }

    [Fact]
    public async Task TM_G02_019_An_endpoint_that_requires_a_permission_never_runs_without_an_evaluator()
    {
        await using var server = await ContractServer.StartAsync(evaluator: false);
        var tenant = server.Directory.Add("Tenant A");
        server.Seams.Membership.Add("user-1", tenant);
        var answer = await server.CallAsync(HttpMethod.Put, "/t/" + TenantIdentifier.Format(tenant) + "/items/item-1", "user-1");
        Assert.Equal(HttpStatusCode.InternalServerError, answer.Status);
        Assert.Contains(server.Events.Events, entry => entry.Id == 8303);
    }

    [Fact]
    public async Task TM_G02_023_A_declared_permission_holds_where_no_authorization_middleware_intercepts_the_request()
    {
        // A pipeline with no authorization middleware at all: nothing intercepts, so only the endpoints' own declarations decide.
        await using var server = await ContractServer.StartAsync(interception: false);
        var tenant = server.Directory.Add("Tenant A");
        server.Seams.Membership.Add("user-1", tenant);
        var path = "/t/" + TenantIdentifier.Format(tenant);
        Assert.Equal((HttpStatusCode.Forbidden, AuthorizationCodes.NotHeld), Pair(await server.CallAsync(HttpMethod.Put, path + "/items/item-1", "user-1")));
        Assert.Equal((HttpStatusCode.Forbidden, AuthorizationCodes.NotHeld), Pair(await server.CallAsync(HttpMethod.Post, path + "/integration/items/item-1", "user-1")));
        // The endpoint protected only by a policy is open here, which is why the startup report names it.
        Assert.Equal(HttpStatusCode.OK, (await server.CallAsync(HttpMethod.Get, path + "/legacy/items/item-1", "user-1")).Status);
        Assert.Contains(server.Events.Events, entry => entry.Id == 8304 && entry.Message.Contains(ContractServer.LegacyRoute, StringComparison.Ordinal));

        // Where the middleware does intercept, the policy is decided by the same evaluator: refused, then permitted after a grant.
        await using var intercepted = await ContractServer.StartAsync();
        var other = intercepted.Directory.Add("Tenant B");
        intercepted.Seams.Membership.Add("user-1", other);
        var legacy = "/t/" + TenantIdentifier.Format(other) + "/legacy/items/item-1";
        Assert.Equal(HttpStatusCode.Forbidden, (await intercepted.CallAsync(HttpMethod.Get, legacy, "user-1")).Status);
        intercepted.Authority.Grant(other, "user-1", Items.Read);
        intercepted.Get<TenantCache>().InvalidateTenant(other);
        Assert.Equal(HttpStatusCode.OK, (await intercepted.CallAsync(HttpMethod.Get, legacy, "user-1")).Status);
    }

    private static (HttpStatusCode, string?) Pair(Answer answer) => (answer.Status, answer.Code);

    [Fact]
    public async Task TM_G02_020_A_resource_is_authorized_after_resolution_in_the_effective_tenant()
    {
        await using var authorization = UnitAuthorization.Create();
        var tenant = authorization.Directory.Add("Tenant A");
        var other = authorization.Directory.Add("Tenant B");
        foreach (var subject in new[] { "user-1", "user-2" }) authorization.Seams.Membership.Add(subject, tenant);
        authorization.Authority.Grant(tenant, "user-1", Items.Read);
        authorization.Authority.Grant(tenant, "user-2", Items.Read);
        var evaluator = authorization.Get<IPermissionEvaluator>();
        var user2 = await authorization.EnterAsync("user-2", tenant);
        ResourceReference Item(string id, string owner, Guid? inTenant = null) => new(inTenant ?? tenant, Items.ResourceType, id, new ActorReference(ActorKind.User, owner));

        // Another owner's resource is refused with the not-found contract; one's own, or one granted, is permitted.
        Assert.Equal(AuthorizationCodes.ResourceNotFound, (await evaluator.AuthorizeAsync(Request(user2, Items.Read, Item("item-1", "user-1")), Cancellation)).Code);
        Assert.True((await evaluator.AuthorizeAsync(Request(user2, Items.Read, Item("item-3", "user-2")), Cancellation)).Permitted);
        authorization.Authority.Share(tenant, "user-2", Items.ResourceType, "item-4", Items.Read);
        Assert.True((await evaluator.AuthorizeAsync(Request(user2, Items.Read, Item("item-4", "user-1")), Cancellation)).Permitted);
        // A grant for one permission covers no other, and one past its expiry covers nothing.
        authorization.Authority.Grant(tenant, "user-2", Items.Update);
        authorization.Get<TenantCache>().InvalidateTenant(tenant);
        Assert.Equal(AuthorizationCodes.ResourceNotFound, (await evaluator.AuthorizeAsync(Request(user2, Items.Update, Item("item-4", "user-1")), Cancellation)).Code);
        authorization.Authority.Share(tenant, "user-2", Items.ResourceType, "item-5", Items.Read, DateTimeOffset.UtcNow.AddMinutes(-1));
        Assert.Equal(AuthorizationCodes.ResourceNotFound, (await evaluator.AuthorizeAsync(Request(user2, Items.Read, Item("item-5", "user-1")), Cancellation)).Code);
        // A resource that belongs to another tenant is never found, even one the subject owns.
        Assert.Equal(AuthorizationCodes.ResourceNotFound, (await evaluator.AuthorizeAsync(Request(user2, Items.Read, Item("item-3", "user-2", other)), Cancellation)).Code);
        // Ownership is not the tenant permission: without item.export, an owner's own item cannot be exported.
        Assert.Equal(AuthorizationCodes.NotHeld, (await evaluator.AuthorizeAsync(Request(user2, Items.Export, Item("item-3", "user-2")), Cancellation)).Code);
    }

    [Fact]
    public async Task TM_G02_033_Bounded_authority_is_cached_per_principal_and_subject_and_invalidated_on_change()
    {
        await using var authorization = UnitAuthorization.Create();
        var tenant = authorization.Directory.Add("Tenant A");
        foreach (var subject in new[] { "user-1", "user-2", "support-1" }) authorization.Seams.Membership.Add(subject, tenant);
        authorization.Authority.Grant(tenant, "user-1", Items.Read);
        var evaluator = authorization.Get<IPermissionEvaluator>();
        var user1 = await authorization.EnterAsync("user-1", tenant);
        var user2 = await authorization.EnterAsync("user-2", tenant);
        Assert.True((await evaluator.AuthorizeAsync(Request(user1, Items.Read), Cancellation)).Permitted);
        var reads = authorization.Authority.Reads;
        Assert.Equal(AuthorizationSource.Cache, (await evaluator.AuthorizeAsync(Request(user1, Items.Read), Cancellation)).Source);
        Assert.Equal(reads, authorization.Authority.Reads);
        // Another principal never receives user-1's entry, and a security-sensitive decision never uses one.
        Assert.Equal(AuthorizationCodes.NotHeld, (await evaluator.AuthorizeAsync(Request(user2, Items.Read), Cancellation)).Code);
        Assert.Equal(AuthorizationSource.Store, (await evaluator.AuthorizeAsync(Request(user1, Items.Read, current: true), Cancellation)).Source);
        // Under delegation the entry is the subject's authority for the delegate's context: keyed by both.
        var cache = authorization.Get<TenantCache>();
        var delegation = ExecutionPrincipal.ForDelegation(new PrincipalReference("support-1", IdentityKind.User), Guid.NewGuid(), DateTimeOffset.UtcNow,
            new PrincipalReference("user-1", IdentityKind.User), new DelegatedAuthority(Guid.NewGuid(), [Items.Read], "Support case", tenant,
                DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(10)));
        var delegated = await authorization.EnterAsync("support-1", tenant, delegation);
        var subject1 = new ActorReference(ActorKind.User, "user-1");
        Assert.NotEqual(cache.KeyFor(user1.Context, TenantAuthorityCache.Entry(subject1, AuthorizationHost.Bound)),
            cache.KeyFor(delegated.Context, TenantAuthorityCache.Entry(subject1, AuthorizationHost.Bound)));
        Assert.True((await evaluator.AuthorizeAsync(Request(delegated, Items.Read), Cancellation)).Permitted);
        // A change invalidates every entry of the tenant at once, both principals' included.
        authorization.Authority.Revoke(tenant, "user-1", Items.Read);
        Assert.Equal(AuthorizationSource.Cache, (await evaluator.AuthorizeAsync(Request(user1, Items.Read), Cancellation)).Source);
        var before = cache.KeyFor(user1.Context, TenantAuthorityCache.Entry(subject1, AuthorizationHost.Bound));
        cache.InvalidateTenant(tenant);
        Assert.NotEqual(before, cache.KeyFor(user1.Context, TenantAuthorityCache.Entry(subject1, AuthorizationHost.Bound)));
        Assert.Equal(AuthorizationCodes.NotHeld, (await evaluator.AuthorizeAsync(Request(user1, Items.Read), Cancellation)).Code);
        Assert.Equal(AuthorizationCodes.NotHeld, (await evaluator.AuthorizeAsync(Request(delegated, Items.Read), Cancellation)).Code);
    }

    [Fact]
    public async Task TM_G02_002_Permissions_and_host_operations_must_match_the_composed_manifests()
    {
        var version = SemanticVersion.Parse("1.0.0");
        var manifest = new ModuleManifest(Items.ModuleId, version, definitions: Items.Permissions.Select(definition => definition.Declaration));
        Assert.NotNull(new AuthorizationRegistry([manifest], Items.Permissions, []).FindPermission(Items.Read));
        // A definition its manifest does not declare, a declaration without a definition, another version, or a second owner is refused.
        Assert.Throws<InvalidOperationException>(() => new AuthorizationRegistry([manifest], Items.Permissions.Skip(1), []));
        Assert.Throws<InvalidOperationException>(() => new AuthorizationRegistry([new ModuleManifest(Items.ModuleId, version)], Items.Permissions, []));
        Assert.Throws<InvalidOperationException>(() => new AuthorizationRegistry([manifest],
            Items.Permissions.Select(definition => new PermissionDefinition(definition.ModuleId, definition.Name, SemanticVersion.Parse("2.0.0"), definition.Freshness)), []));
        Assert.Throws<InvalidOperationException>(() => new AuthorizationRegistry([manifest], Items.Permissions.Append(new PermissionDefinition("Other", Items.Read, version)), []));
        // A host operation reaches only modules the composition includes.
        var reaching = new HostOperationDefinition(Items.ModuleId, version, new HostOperation("test.reach", [new HostReach("Absent.Module", "Rows")]));
        Assert.Throws<InvalidOperationException>(() => new AuthorizationRegistry(
            [new ModuleManifest(Items.ModuleId, version, definitions: [reaching.Declaration])], [], [reaching]));

        // As a composition validator it refuses activation while a selected manifest and the definitions disagree.
        var registry = new AuthorizationRegistry([manifest], Items.Permissions, []);
        var environment = new CompositionEnvironment(SemanticVersion.Parse("10.0.14"), version);
        Assert.Empty(await registry.ValidateAsync(new([manifest], environment), Cancellation));
        var drifted = new ModuleManifest(Items.ModuleId, version, definitions: Items.Permissions.Skip(1).Select(definition => definition.Declaration));
        Assert.Equal("MDC310", Assert.Single(await registry.ValidateAsync(new([drifted], environment), Cancellation)).Code);
    }

    [Fact]
    public void TM_G02_048_Authorization_options_are_validated_before_startup()
    {
        var registry = UnitAuthorization.Registry();
        string[] Failures(Dictionary<string, string?> values)
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<ITenantDirectory>(new MemoryDirectory());
            services.AddSingleton<TenantCache>();
            services.AddScoped<TenantExecution>();
            services.AddSingleton<TenantWorkRunner>();
            services.AddModularynAuthorization(new ConfigurationBuilder().AddInMemoryCollection(values).Build(),
                AuthorizationModule.Create(new Persistence.EfCore.PostgreSqlPersistenceOptions("Host=localhost;Database=unused")), registry);
            using var provider = services.BuildServiceProvider();
            try
            {
                _ = provider.GetRequiredService<IOptions<AuthorizationSecurityOptions>>().Value;
                return [];
            }
            catch (OptionsValidationException exception)
            {
                return [.. exception.Failures];
            }
        }
        Assert.Empty(Failures([]));
        Assert.Empty(Failures(new() { ["Modularyn:Authorization:FreshnessBound"] = "00:05:00", ["Modularyn:Authorization:HostOperators:0:Subject"] = "operator-1",
            ["Modularyn:Authorization:HostOperators:0:Operations:0"] = AuthorizationModule.TenantOwnersOperation.Id }));
        Assert.Single(Failures(new() { ["Modularyn:Authorization:FreshnessBound"] = "00:10:00" }));
        Assert.Single(Failures(new() { ["Modularyn:Authorization:FreshnessBound"] = "00:00:00.5" }));
        var undeclared = Failures(new() { ["Modularyn:Authorization:HostOperators:0:Subject"] = "operator-1",
            ["Modularyn:Authorization:HostOperators:0:Operations:0"] = "modularyn.unknown" });
        Assert.Contains("Operations:0", Assert.Single(undeclared), StringComparison.Ordinal);
        Assert.DoesNotContain("modularyn.unknown", undeclared[0], StringComparison.Ordinal);
        Assert.NotEmpty(Failures(new() { ["Modularyn:Authorization:HostOperators:0:Subject"] = "operator-1" }));
        Assert.NotEmpty(Failures(new() { ["Modularyn:Authorization:Unknown"] = "1" }));
    }

    private static Task Never(TenantWorkUnit unit, AuthorizationDecision decision, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("Refused work must not run.");

    private static (TenantWorkDisposition, string?) Disposition(TenantWorkOutcome outcome) => (outcome.Disposition, outcome.Code);
}
