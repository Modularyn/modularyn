using Xunit;

namespace Modularyn.Framework.Security.Abstractions.Tests;

public sealed class ExecutionPrincipalTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);
    private static readonly PrincipalReference User1 = new(Guid.NewGuid().ToString("D"), IdentityKind.User);
    private static readonly PrincipalReference Support1 = new(Guid.NewGuid().ToString("D"), IdentityKind.User);
    private static readonly PrincipalReference Items = new("svc-items", IdentityKind.Service);
    private static readonly Guid TenantA = Guid.NewGuid();
    private static readonly Guid TenantB = Guid.NewGuid();

    [Fact(DisplayName = "TM-G02-018 actor, effective subject and identity kind distinguish users, delegates and services")]
    public void TM_G02_018_Actor_subject_and_identity_kind_distinguish_users_delegates_and_services()
    {
        var session = Guid.NewGuid();
        var user = ExecutionPrincipal.ForUser(User1, session, Now);
        Assert.Equal(User1, user.Actor);
        Assert.Equal(User1, user.EffectiveSubject);
        Assert.False(user.IsDelegated);
        Assert.Null(user.ServiceScope);
        Assert.Equal(session, user.SessionId);

        var delegation = new DelegatedAuthority(Guid.NewGuid(), ["item.read"], "support case", TenantA, Now, Now.AddHours(1));
        var delegated = ExecutionPrincipal.ForDelegation(Support1, Guid.NewGuid(), Now, User1, delegation);
        Assert.Equal(Support1, delegated.Actor);
        Assert.Equal(User1, delegated.EffectiveSubject);
        Assert.Equal(IdentityKind.User, delegated.Actor.Kind);
        Assert.True(delegated.IsDelegated);
        Assert.Same(delegation, delegated.Delegation);

        var service = ExecutionPrincipal.ForService(Items, ["item.reindex"]);
        Assert.Equal(IdentityKind.Service, service.Actor.Kind);
        Assert.Equal(service.Actor, service.EffectiveSubject);
        Assert.Null(service.SessionId);
        Assert.Null(service.Delegation);
        Assert.Equal(["item.reindex"], service.ServiceScope!);
        Assert.Contains("acting for", delegated.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("acting for", user.ToString(), StringComparison.Ordinal);
    }

    [Fact(DisplayName = "TM-G02-018 a delegation's ceiling covers only its scope, tenant and validity window")]
    public void TM_G02_018_A_delegation_ceiling_covers_only_its_scope_tenant_and_window()
    {
        var notBefore = Now.AddMinutes(5);
        var expiry = Now.AddMinutes(30);
        var delegated = ExecutionPrincipal.ForDelegation(Support1, Guid.NewGuid(), Now, User1,
            new DelegatedAuthority(Guid.NewGuid(), ["item.read"], "support case", TenantA, notBefore, expiry));

        Assert.False(delegated.IsWithinCeiling("item.read", TenantA, notBefore.AddSeconds(-1)));
        Assert.True(delegated.IsWithinCeiling("item.read", TenantA, notBefore));
        Assert.True(delegated.IsWithinCeiling("item.read", TenantA, expiry.AddSeconds(-1)));
        Assert.False(delegated.IsWithinCeiling("item.read", TenantA, expiry));
        Assert.False(delegated.IsWithinCeiling("item.read", TenantA, expiry.AddSeconds(1)));
        Assert.False(delegated.IsWithinCeiling("item.update", TenantA, expiry.AddSeconds(-1)));
        Assert.False(delegated.IsWithinCeiling("item.read", TenantB, expiry.AddSeconds(-1)));
        Assert.False(delegated.IsWithinCeiling("item.read", null, expiry.AddSeconds(-1)));
        Assert.False(delegated.IsWithinCeiling("item.*", TenantA, expiry.AddSeconds(-1)));

        var hostLevel = ExecutionPrincipal.ForDelegation(Support1, Guid.NewGuid(), Now, User1,
            new DelegatedAuthority(Guid.NewGuid(), ["item.read"], "support case", null, Now, expiry));
        Assert.True(hostLevel.IsWithinCeiling("item.read", null, Now));
        Assert.False(hostLevel.IsWithinCeiling("item.read", TenantA, Now));
    }

    [Fact(DisplayName = "TM-G02-018 a service identity's ceiling is its declared scope and an undelegated user has only its grants")]
    public void TM_G02_018_A_service_ceiling_is_its_declared_scope_and_an_undelegated_user_has_only_its_grants()
    {
        var service = ExecutionPrincipal.ForService(Items, ["item.reindex"]);
        Assert.True(service.IsWithinCeiling("item.reindex", TenantA, Now));
        Assert.False(service.IsWithinCeiling("item.read", TenantA, Now));
        Assert.False(service.IsWithinCeiling("item.update", null, Now));

        // An undelegated user has no ceiling beyond its own grants, which authorization checks; malformed names never pass.
        var user = ExecutionPrincipal.ForUser(User1, Guid.NewGuid(), Now);
        Assert.True(user.IsWithinCeiling("item.read", TenantA, Now));
        Assert.False(user.IsWithinCeiling("*", TenantA, Now));
        Assert.False(user.IsWithinCeiling("", TenantA, Now));
    }

    [Fact(DisplayName = "TM-G02-018 construction refuses mixed kinds, self delegation, wildcards and empty scopes")]
    public void TM_G02_018_Construction_refuses_mixed_kinds_self_delegation_wildcards_and_empty_scopes()
    {
        var authority = new DelegatedAuthority(Guid.NewGuid(), ["item.read"], "support case", null, Now, Now.AddHours(1));
        Assert.Throws<ArgumentException>(() => ExecutionPrincipal.ForUser(Items, Guid.NewGuid(), Now));
        Assert.Throws<ArgumentException>(() => ExecutionPrincipal.ForUser(User1, Guid.Empty, Now));
        Assert.Throws<ArgumentException>(() => ExecutionPrincipal.ForDelegation(Items, Guid.NewGuid(), Now, User1, authority));
        Assert.Throws<ArgumentException>(() => ExecutionPrincipal.ForDelegation(Support1, Guid.NewGuid(), Now, Items, authority));
        Assert.Throws<ArgumentException>(() => ExecutionPrincipal.ForDelegation(User1, Guid.NewGuid(), Now, User1, authority));
        Assert.Throws<ArgumentException>(() => ExecutionPrincipal.ForService(User1, ["item.read"]));
        foreach (var scope in new[] { Array.Empty<string>(), ["*"], ["item.*"], ["Item.Read"], ["item read"], ["item..read"], [""] })
        {
            Assert.Throws<ArgumentException>(() => ExecutionPrincipal.ForService(Items, scope));
            Assert.Throws<ArgumentException>(() => new DelegatedAuthority(Guid.NewGuid(), scope, "support case", null, Now, Now.AddHours(1)));
        }
        Assert.Throws<ArgumentException>(() => new DelegatedAuthority(Guid.NewGuid(), ["item.read"], " ", null, Now, Now.AddHours(1)));
        Assert.Throws<ArgumentException>(() => new DelegatedAuthority(Guid.NewGuid(), ["item.read"], "line\nbreak", null, Now, Now.AddHours(1)));
        Assert.Throws<ArgumentException>(() => new DelegatedAuthority(Guid.NewGuid(), ["item.read"], "support case", null, Now, Now));
        Assert.Throws<ArgumentException>(() => new DelegatedAuthority(Guid.Empty, ["item.read"], "support case", null, Now, Now.AddHours(1)));
        Assert.Throws<ArgumentException>(() => new DelegatedAuthority(Guid.NewGuid(), ["item.read"], "support case", Guid.Empty, Now, Now.AddHours(1)));
        Assert.Throws<ArgumentException>(() => new PrincipalReference("with space", IdentityKind.User));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PrincipalReference("id", (IdentityKind)7));
        Assert.True(PermissionNames.IsValid("item.bulk-update"));
        Assert.True(PermissionNames.IsValid("item.read"));
    }
}

public sealed class ExecutionPrincipalScopeTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);

    private static ExecutionPrincipal User() => ExecutionPrincipal.ForUser(new(Guid.NewGuid().ToString("D"), IdentityKind.User), Guid.NewGuid(), Now);

    [Fact(DisplayName = "TM-G02-018 a unit's principal is established once, never replaced, and ends with the unit")]
    public void TM_G02_018_A_unit_s_principal_is_established_once_never_replaced_and_ends_with_the_unit()
    {
        var unit = new ExecutionPrincipalScope();
        var first = User();
        Assert.Null(unit.Current);
        Assert.True(unit.TryEstablish(first));
        Assert.Same(first, unit.Current);
        Assert.False(unit.TryEstablish(User()));
        Assert.Same(first, unit.Current);
        unit.End();
        Assert.Null(unit.Current);
        Assert.False(unit.TryEstablish(first));
        Assert.Null(unit.Current);

        // A unit that ended before anything was established never gains a principal.
        var ended = new ExecutionPrincipalScope();
        ended.End();
        Assert.False(ended.TryEstablish(first));
        Assert.Null(ended.Current);
        Assert.Throws<ArgumentNullException>(() => new ExecutionPrincipalScope().TryEstablish(null!));
    }

    [Fact(DisplayName = "TM-G02-018 concurrent attempts establish exactly one principal, and other units are unaffected")]
    public async Task TM_G02_018_Concurrent_attempts_establish_exactly_one_principal()
    {
        var unit = new ExecutionPrincipalScope();
        var other = new ExecutionPrincipalScope();
        var candidates = Enumerable.Range(0, 32).Select(_ => User()).ToArray();
        var results = await Task.WhenAll(candidates.Select(candidate => Task.Run(() => unit.TryEstablish(candidate), TestContext.Current.CancellationToken)));
        Assert.Single(results, result => result);
        Assert.Same(candidates[Array.IndexOf(results, true)], unit.Current);
        Assert.Null(other.Current);
    }

    [Fact(DisplayName = "TM-G02-018 a service identity scope runs only a service principal and ends its unit once")]
    public async Task TM_G02_018_A_service_identity_scope_runs_only_a_service_principal_and_ends_its_unit_once()
    {
        var service = ExecutionPrincipal.ForService(new("svc-items", IdentityKind.Service), ["item.reindex"]);
        var lifetime = new CountingLifetime();
        var services = new EmptyServices();
        var scope = new ServiceIdentityScope(service, services, lifetime);
        Assert.Same(service, scope.Principal);
        Assert.Same(services, scope.Services);
        Assert.False(scope.Principal.IsWithinCeiling("item.update", null, Now));
        await scope.DisposeAsync();
        await scope.DisposeAsync();
        Assert.Equal(1, lifetime.Disposals);
        Assert.Throws<ArgumentException>(() => new ServiceIdentityScope(User(), services, lifetime));
    }

    private sealed class CountingLifetime : IAsyncDisposable
    {
        internal int Disposals { get; private set; }

        public ValueTask DisposeAsync()
        {
            Disposals++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class EmptyServices : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }
}
