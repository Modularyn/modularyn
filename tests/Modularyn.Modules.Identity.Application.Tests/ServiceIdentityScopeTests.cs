using Microsoft.Extensions.DependencyInjection;
using Modularyn.Framework.Security.Abstractions;
using Xunit;

namespace Modularyn.Modules.Identity.Application.Tests;

// TM-G02-018 and TM-G02-026's principal part: a service identity's principal lives in its unit's own service scope.
// Nothing ambient carries it, so concurrent units, work begun without the unit's services and later work on the same
// pool never see it, and it ends with its unit.
public sealed class ServiceIdentityScopeTests
{
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    private static UnitIdentity Create() => UnitIdentity.Create(UnitIdentity.TestLimits(), services => services
        .AddModularynServiceIdentity("svc-items", ["item.reindex"])
        .AddModularynServiceIdentity("svc-mail", ["mail.send"]));

    [Fact(DisplayName = "TM-G02-018 concurrent service-identity units each see only their own principal, which ends with the unit")]
    public async Task TM_G02_018_Concurrent_units_see_only_their_own_principal_which_ends_with_the_unit()
    {
        await using var identity = Create();
        var scopes = identity.Get<IServiceIdentityScopes>();
        var factory = identity.Get<IServiceScopeFactory>();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var units = Enumerable.Range(0, 24).Select(async index =>
        {
            var name = index % 2 == 0 ? "svc-items" : "svc-mail";
            IExecutionPrincipalAccessor principals;
            await using (var unit = scopes.Begin(name))
            {
                principals = unit.Services.GetRequiredService<IExecutionPrincipalAccessor>();
                await gate.Task.WaitAsync(Cancellation);
                Assert.Equal(name, principals.Current!.Actor.Id);
                // Work begun inside the unit without its services, in a scope of its own, has no principal.
                Assert.Null(await Task.Run(async () =>
                {
                    await using var other = factory.CreateAsyncScope();
                    return other.ServiceProvider.GetRequiredService<IExecutionPrincipalAccessor>().Current;
                }, Cancellation));
                await Task.Yield();
                Assert.Equal(name, principals.Current!.Actor.Id);
            }
            return principals.Current;
        }).ToArray();
        gate.SetResult();
        Assert.All(await Task.WhenAll(units), Assert.Null);

        // Later work on the same pool, in new scopes, sees no principal from any of them.
        var later = await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => Task.Run(async () =>
        {
            await using var scope = factory.CreateAsyncScope();
            return scope.ServiceProvider.GetRequiredService<IExecutionPrincipalAccessor>().Current;
        }, Cancellation)));
        Assert.All(later, Assert.Null);
    }

    [Fact(DisplayName = "TM-G02-018 a unit's principal cannot be replaced, and an undeclared service identity is refused")]
    public async Task TM_G02_018_A_unit_s_principal_cannot_be_replaced_and_an_undeclared_identity_is_refused()
    {
        await using var identity = Create();
        var scopes = identity.Get<IServiceIdentityScopes>();
        await using (var unit = scopes.Begin("svc-items"))
        {
            var holder = unit.Services.GetRequiredService<ExecutionPrincipalScope>();
            Assert.False(holder.TryEstablish(ExecutionPrincipal.ForService(new("svc-mail", IdentityKind.Service), ["mail.send"])));
            Assert.Equal("svc-items", unit.Services.GetRequiredService<IExecutionPrincipalAccessor>().Current!.Actor.Id);
        }
        Assert.Throws<InvalidOperationException>(() => scopes.Begin("svc-admin"));
        Assert.Throws<InvalidOperationException>(() => scopes.Begin("SVC-ITEMS"));
    }

    private sealed class CapturingSingleton(IExecutionPrincipalAccessor principals)
    {
        internal IExecutionPrincipalAccessor Principals { get; } = principals;
    }

    [Fact(DisplayName = "TM-G02-018 a singleton cannot capture a unit's principal, even in a host that does not validate scopes")]
    public async Task TM_G02_018_A_singleton_cannot_capture_a_unit_s_principal_even_without_scope_validation()
    {
        // A production host does not validate scopes, so the container would hand a singleton the root provider's holder.
        await using var identity = UnitIdentity.Create(UnitIdentity.TestLimits(), services => services.AddSingleton<CapturingSingleton>(), validateScopes: false);
        Assert.Contains("unit of work", Assert.Throws<InvalidOperationException>(() => identity.Get<CapturingSingleton>()).Message, StringComparison.Ordinal);
        Assert.Throws<InvalidOperationException>(() => identity.Get<IExecutionPrincipalAccessor>());
        Assert.Throws<InvalidOperationException>(() => identity.Get<ExecutionPrincipalScope>());

        // Inside a scope the holder is the unit's own.
        await using var unit = identity.Get<IServiceScopeFactory>().CreateAsyncScope();
        var holder = unit.ServiceProvider.GetRequiredService<ExecutionPrincipalScope>();
        Assert.Same(holder, unit.ServiceProvider.GetRequiredService<IExecutionPrincipalAccessor>());
        Assert.Null(holder.Current);
    }
}
