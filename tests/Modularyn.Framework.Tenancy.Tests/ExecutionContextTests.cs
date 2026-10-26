using Microsoft.Extensions.DependencyInjection;
using Modularyn.Framework.Tenancy.Abstractions;
using Xunit;

namespace Modularyn.Framework.Tenancy.Tests;

// TM-G02-026: a unit's context is established once, never changes, and ends with its scope on every path, including
// a refusal, an exception and cancellation. Nothing ambient carries it.
public sealed class ExecutionContextTests
{
    private readonly MemoryDirectory directory = new();
    private readonly TestSeams seams = new();
    private readonly Guid tenantA;

    public ExecutionContextTests()
    {
        tenantA = directory.Add("Tenant A");
        seams.Membership.Add("user-a", tenantA);
    }

    private TenantResolutionRequest Request() => TenantResolutionRequest.ForMember(TestIdentity.User("user-a"), [Candidates.Header(tenantA)]);

    [Fact]
    public async Task TM_G02_026_A_context_is_established_once_and_ends_with_its_scope()
    {
        await using var services = UnitServices.Build(directory, seams);
        TenantContext context;
        TenantExecution execution;
        await using (var scope = services.CreateAsyncScope())
        {
            execution = scope.ServiceProvider.GetRequiredService<TenantExecution>();
            Assert.Same(execution, scope.ServiceProvider.GetRequiredService<TenantExecution>());
            Assert.Null(execution.Context);
            context = (await execution.EstablishAsync(Request(), TestContext.Current.CancellationToken)).Context!;
            Assert.Same(context, execution.Context);
            await Assert.ThrowsAsync<InvalidOperationException>(() => execution.EstablishAsync(Request(), TestContext.Current.CancellationToken).AsTask());
            await Assert.ThrowsAsync<InvalidOperationException>(() => execution.EstablishPropagatedAsync(PropagatedTenantContext.Capture(context),
                TestContext.Current.CancellationToken).AsTask());
            Assert.Same(context, execution.Context);
            Assert.True(context.IsActive);
        }
        Assert.False(context.IsActive);
        Assert.True(execution.HasEnded);
        Assert.Null(execution.Context);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => execution.EstablishAsync(Request(), TestContext.Current.CancellationToken).AsTask());
    }

    [Fact]
    public async Task TM_G02_026_A_refused_unit_stays_without_a_context()
    {
        await using var services = UnitServices.Build(directory, seams);
        await using var scope = services.CreateAsyncScope();
        var execution = scope.ServiceProvider.GetRequiredService<TenantExecution>();
        var refused = await execution.EstablishAsync(TenantResolutionRequest.ForMember(TestIdentity.User("user-b"), [Candidates.Header(tenantA)]),
            TestContext.Current.CancellationToken);
        Assert.Equal(TenancyCodes.NotMember, refused.Code);
        Assert.Null(execution.Context);
        // A unit cannot retry with other candidates after a refusal.
        await Assert.ThrowsAsync<InvalidOperationException>(() => execution.EstablishAsync(Request(), TestContext.Current.CancellationToken).AsTask());
        Assert.Null(execution.Context);
    }

    [Fact]
    public async Task TM_G02_026_Exceptions_and_cancellation_end_the_unit_s_context()
    {
        await using var services = UnitServices.Build(directory, seams);
        var runner = services.GetRequiredService<TenantWorkRunner>();
        var reference = await CaptureAsync(services);
        TenantContext? failed = null;
        await Assert.ThrowsAsync<InvalidOperationException>(() => runner.RunAsync(reference, (unit, _) =>
        {
            failed = unit.Context;
            throw new InvalidOperationException("Work failed.");
        }, TestContext.Current.CancellationToken));
        Assert.False(failed!.IsActive);

        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        TenantContext? cancelled = null;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runner.RunAsync(reference, async (unit, token) =>
        {
            cancelled = unit.Context;
            cancellation.Cancel();
            await Task.Delay(Timeout.Infinite, token);
        }, cancellation.Token));
        Assert.False(cancelled!.IsActive);

        // Cancellation while a seam is answering leaves no context behind.
        await using var blocked = UnitServices.Build(directory, seams, services => services.AddSingleton<ITenantMembership>(new BlockingMembership()));
        await using var scope = blocked.CreateAsyncScope();
        var execution = scope.ServiceProvider.GetRequiredService<TenantExecution>();
        using var during = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var establishing = execution.EstablishAsync(Request(), during.Token).AsTask();
        during.CancelAfter(TimeSpan.FromMilliseconds(50));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => establishing);
        Assert.Null(execution.Context);
    }

    private sealed class BlockingMembership : ITenantMembership
    {
        public async ValueTask<bool> IsMemberAsync(ActorReference actor, Guid tenantId, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return true;
        }
    }

    private async Task<PropagatedTenantContext> CaptureAsync(IServiceProvider services)
    {
        var (scope, context) = await Units.EnterAsync(services, "user-a", tenantA);
        await using (scope) return PropagatedTenantContext.Capture(context);
    }
}
