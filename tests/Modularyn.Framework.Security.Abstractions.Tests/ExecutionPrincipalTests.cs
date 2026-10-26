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

public sealed class ExecutionScopeTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);
    private readonly AmbientExecutionPrincipalAccessor accessor = new();

    private static ExecutionPrincipal User() => ExecutionPrincipal.ForUser(new(Guid.NewGuid().ToString("D"), IdentityKind.User), Guid.NewGuid(), Now);

    [Fact(DisplayName = "TM-G02-018 a scope ends with its handle and never leaks into later work on a reused worker")]
    public async Task TM_G02_018_A_scope_ends_with_its_handle_and_never_leaks_into_later_work()
    {
        Assert.Null(accessor.Current);
        var outer = User();
        var inner = User();
        using (ExecutionScope.Begin(outer))
        {
            Assert.Same(outer, accessor.Current);
            Assert.Same(outer, await Task.Run(() => accessor.Current, TestContext.Current.CancellationToken));
            using (ExecutionScope.Begin(inner)) Assert.Same(inner, accessor.Current);
            Assert.Same(outer, accessor.Current);
        }
        Assert.Null(accessor.Current);

        // Work queued after the scope ends, on the same pool, sees no principal.
        var observed = await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => Task.Run(() => accessor.Current, TestContext.Current.CancellationToken)));
        Assert.All(observed, Assert.Null);

        // An out-of-order disposal never revives a disposed principal.
        var first = ExecutionScope.Begin(outer);
        var second = ExecutionScope.Begin(inner);
        first.Dispose();
        Assert.Same(inner, accessor.Current);
        second.Dispose();
        Assert.Null(accessor.Current);
        second.Dispose();
        Assert.Null(accessor.Current);
    }

    [Fact(DisplayName = "TM-G02-018 a service identity scope replaces the ambient principal and carries none of its authority")]
    public void TM_G02_018_A_service_identity_scope_replaces_the_ambient_principal()
    {
        var administrator = User();
        using (ExecutionScope.Begin(administrator))
        {
            var service = ExecutionPrincipal.ForService(new("svc-items", IdentityKind.Service), ["item.reindex"]);
            using (var scope = new ServiceIdentityScope(service))
            {
                Assert.Same(service, accessor.Current);
                Assert.Same(service, scope.Principal);
                Assert.NotEqual(administrator.Actor, accessor.Current!.Actor);
                Assert.False(accessor.Current.IsWithinCeiling("item.update", null, Now));
            }
            Assert.Same(administrator, accessor.Current);
        }
        Assert.Throws<ArgumentException>(() => new ServiceIdentityScope(administrator));
    }
}
