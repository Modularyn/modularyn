using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Modularyn.Framework.Security.Abstractions;
using Modularyn.Modules.Identity.Application.Contracts;
using Modularyn.Modules.Identity.Testing;
using Npgsql;
using Xunit;

namespace Modularyn.Modules.Identity.Application.Tests;

/// <summary>
/// AC-003-B02: delegated authority bounded at its scope and expiry edges, with actor and subject recorded, and background
/// work under its own service identity. The fixture's grants stand in for authorization, which is SEC-05's: a decision
/// needs both the execution's ceiling and the effective subject's grant.
/// </summary>
[Collection(IdentityPostgreSqlCollection.Name)]
public sealed class G02AC003Tests(ITestOutputHelper output)
{
    private static readonly Guid Tenant = Guid.NewGuid();
    private const string Purpose = "Support case for user-1's item listing";
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AC_003_B02_DelegationHonorsScopeExpiryAndActorIdentity_Unit()
    {
        await using var identity = UnitIdentity.Create(configure: services => services.AddModularynServiceIdentity("svc-items", ["item.reindex"]));
        var (user1Id, user1Password) = await identity.AccountAsync("user-1");
        var (support1Id, support1Password) = await identity.AccountAsync("support-1");
        var (_, support2Password) = await identity.AccountAsync("support-2");
        var user1 = (await identity.SignInAsync("user-1", user1Password)).Principal;
        var support1 = (await identity.SignInAsync("support-1", support1Password)).Principal;
        var support2 = (await identity.SignInAsync("support-2", support2Password)).Principal;
        var grants = new Dictionary<string, string[]> { [user1.Actor.Id] = ["item.read", "item.update"] };
        var delegations = identity.Get<IDelegationService>();
        // Inside the sessions' idle timeout, so only the delegation's own window decides.
        var expiry = identity.Clock.Now.AddMinutes(10);

        var issued = await delegations.IssueAsync(new DelegationRequest(support1Id, user1Id, ["item.read"], Purpose, Tenant, expiry), user1, Cancellation);
        Assert.Equal(DelegationIssueStatus.Issued, issued.Status);
        var grant = issued.GrantId!.Value;
        var record = await delegations.DescribeAsync(grant, Cancellation);
        Assert.Equal(support1.Actor, record!.Actor);
        Assert.Equal(user1.Actor, record.Subject);
        Assert.Equal(user1.Actor, record.IssuedBy);
        Assert.Equal(["item.read"], record.Scope);
        Assert.Equal((Purpose, Tenant, expiry), (record.Purpose, record.Tenant, record.ExpiresAt));
        output.WriteLine($"Delegation {grant:D}: actor {record.Actor.Id}, subject {record.Subject.Id}, scope item.read, tenant {Tenant}, expires {expiry:O}.");

        // At E minus one second the read is permitted and records actor, effective subject and identity kind; the update is outside the scope.
        identity.Clock.Set(expiry.AddSeconds(-1));
        var delegated = await delegations.ResolveAsync(grant, support1, Cancellation);
        Assert.NotNull(delegated);
        Assert.Equal((support1.Actor, user1.Actor, IdentityKind.User, IdentityKind.User),
            (delegated.Actor, delegated.EffectiveSubject, delegated.Actor.Kind, delegated.EffectiveSubject.Kind));
        Assert.Equal(support1.SessionId, delegated.SessionId);
        Assert.True(Permits(delegated, "item.read", grants, identity.Clock.Now));
        Assert.False(Permits(delegated, "item.update", grants, identity.Clock.Now));
        Assert.True(Permits(user1, "item.update", grants, identity.Clock.Now));
        output.WriteLine($"E-1s: read as {delegated} permitted; update refused as outside the delegated scope.");

        // The current-user view distinguishes the actor from the subject.
        var view = CurrentPrincipalView.Describe(delegated);
        Assert.True(view.Delegated);
        Assert.NotEqual(view.Actor, view.EffectiveSubject);
        Assert.Equal((support1.Actor, user1.Actor, grant), (view.Actor, view.EffectiveSubject, view.DelegationId!.Value));
        Assert.False(CurrentPrincipalView.Describe(support1).Delegated);

        // At E plus one second the grant no longer resolves, and a principal resolved earlier is outside its ceiling.
        identity.Clock.Set(expiry.AddSeconds(1));
        Assert.Null(await delegations.ResolveAsync(grant, support1, Cancellation));
        Assert.False(Permits(delegated, "item.read", grants, identity.Clock.Now));
        Assert.Equal(FreshnessStatus.DelegationEnded, await identity.Get<IPrincipalFreshness>().RecheckAsync(delegated, Cancellation));
        output.WriteLine("E+1s: the delegation no longer resolves and the earlier principal is outside its ceiling.");

        // A second delegation, revoked before its expiry, stops resolving at once, and a principal resolved before is no longer fresh.
        var second = await delegations.IssueAsync(new DelegationRequest(support1Id, user1Id, ["item.read"], Purpose, Tenant, identity.Clock.Now.AddHours(1)), user1, Cancellation);
        var beforeRevocation = await delegations.ResolveAsync(second.GrantId!.Value, support1, Cancellation);
        Assert.NotNull(beforeRevocation);
        Assert.True(await delegations.RevokeAsync(second.GrantId.Value, user1, Cancellation));
        Assert.Null(await delegations.ResolveAsync(second.GrantId.Value, support1, Cancellation));
        Assert.Equal(FreshnessStatus.DelegationEnded, await identity.Get<IPrincipalFreshness>().RecheckAsync(beforeRevocation, Cancellation));
        Assert.Equal(user1.Actor, (await delegations.DescribeAsync(second.GrantId.Value, Cancellation))!.RevokedBy);
        output.WriteLine("A revoked delegation stopped resolving before its expiry.");

        // A delegation belongs to its actor, cannot be issued for someone else or from a delegated execution, and is never chained.
        var third = await delegations.IssueAsync(new DelegationRequest(support1Id, user1Id, ["item.read"], Purpose, Tenant, identity.Clock.Now.AddHours(1)), user1, Cancellation);
        Assert.Null(await delegations.ResolveAsync(third.GrantId!.Value, support2, Cancellation));
        var asSupport = await delegations.ResolveAsync(third.GrantId.Value, support1, Cancellation);
        Assert.Null(await delegations.ResolveAsync(third.GrantId.Value, asSupport!, Cancellation));
        Assert.Equal(DelegationIssueStatus.IssuerNotPermitted,
            (await delegations.IssueAsync(new DelegationRequest(support1Id, user1Id, ["item.read"], Purpose, Tenant, identity.Clock.Now.AddHours(1)), support1, Cancellation)).Status);
        Assert.Equal(DelegationIssueStatus.IssuerNotPermitted,
            (await delegations.IssueAsync(new DelegationRequest(support1Id, user1Id, ["item.update"], Purpose, Tenant, identity.Clock.Now.AddHours(1)), asSupport!, Cancellation)).Status);
        Assert.Equal(DelegationIssueStatus.InvalidWindow,
            (await delegations.IssueAsync(new DelegationRequest(support1Id, user1Id, ["item.read"], Purpose, Tenant, identity.Clock.Now.AddDays(2)), user1, Cancellation)).Status);

        // The item job runs as svc-items with item.reindex only, in a service scope of its own, even when begun from a
        // request whose principal is user-1. The request keeps its principal throughout, and the job's ends with the job.
        var scopes = identity.Get<IServiceIdentityScopes>();
        await using (var request = identity.Get<IServiceScopeFactory>().CreateAsyncScope())
        {
            Assert.True(request.ServiceProvider.GetRequiredService<ExecutionPrincipalScope>().TryEstablish(user1));
            var requestPrincipals = request.ServiceProvider.GetRequiredService<IExecutionPrincipalAccessor>();
            ExecutionPrincipal? observed;
            IExecutionPrincipalAccessor jobPrincipals;
            await using (var job = scopes.Begin("svc-items"))
            {
                jobPrincipals = job.Services.GetRequiredService<IExecutionPrincipalAccessor>();
                observed = jobPrincipals.Current;
                Assert.Same(job.Principal, observed);
                Assert.Equal(new PrincipalReference("svc-items", IdentityKind.Service), observed!.Actor);
                Assert.Equal(observed.Actor, observed.EffectiveSubject);
                Assert.Null(observed.SessionId);
                Assert.Null(observed.Delegation);
                Assert.Equal(["item.reindex"], observed.ServiceScope!);
                Assert.True(Permits(observed, "item.reindex", grants, identity.Clock.Now));
                Assert.False(Permits(observed, "item.read", grants, identity.Clock.Now));
                Assert.False(Permits(observed, "item.update", grants, identity.Clock.Now));
                Assert.Equal(FreshnessStatus.Current, await identity.Get<IPrincipalFreshness>().RecheckAsync(observed, Cancellation));
                Assert.Same(user1, requestPrincipals.Current);
            }
            Assert.Null(jobPrincipals.Current);
            Assert.Same(user1, requestPrincipals.Current);
            output.WriteLine($"The item job ran as {observed} with scope item.reindex only.");
        }
        Assert.Throws<InvalidOperationException>(() => scopes.Begin("svc-admin"));
    }

    [Fact]
    [Trait("Requires", "PostgreSQL")]
    public async Task AC_003_B02_DelegationHonorsScopeExpiryAndActorIdentity_Isolation()
    {
        await using var database = await IdentityDatabase.CreateAsync();
        var grants = new ConcurrentDictionary<string, string[]>(StringComparer.Ordinal);
        await using var server = await IdentityTestServer.StartAsync(database,
            services: services => services.AddModularynServiceIdentity("svc-items", ["item.reindex"]), endpoints: application => MapItems(application, grants));
        var accounts = server.Get<IAccountService>();
        var passwords = new Dictionary<string, string>();
        var ids = new Dictionary<string, Guid>();
        foreach (var name in new[] { "user-1", "support-1", "support-2" })
        {
            passwords[name] = Generated.Password();
            ids[name] = (await accounts.CreateLocalAccountAsync(await server.Administrator.AccountsAsync(), name, new SecretText(passwords[name]), Cancellation)).AccountId!.Value;
        }
        grants[ids["user-1"].ToString("D")] = ["item.read", "item.update"];
        var browsers = new Dictionary<string, Browser>();
        foreach (var name in passwords.Keys)
        {
            browsers[name] = server.Browser();
            Assert.Equal(HttpStatusCode.OK, (await browsers[name].SignInAsync(name, passwords[name])).StatusCode);
        }

        // user-1 delegates item.read in tenant-a to support-1, from user-1's own session.
        var user1 = (await server.Get<ISessionService>().ValidateAsync(Token(browsers["user-1"]), Cancellation))!.ToPrincipal();
        var delegations = server.Get<IDelegationService>();
        var expiry = server.Clock.Now.AddMinutes(10);
        var grant = (await delegations.IssueAsync(new DelegationRequest(ids["support-1"], ids["user-1"], ["item.read"], Purpose, Tenant, expiry), user1, Cancellation)).GrantId!.Value;
        Assert.Equal(1L, await database.ScalarAsync<long>("SELECT count(*) FROM " + database.Table("Delegations")
            + " WHERE \"Id\" = @id AND \"ActorId\" = @actor AND \"SubjectId\" = @subject AND \"Scope\" = 'item.read' AND \"Purpose\" = @purpose"
            + " AND \"Tenant\" = @tenant AND \"ExpiresAt\" = @expiry AND \"IssuedById\" = @subject AND \"RevokedAt\" IS NULL",
            new NpgsqlParameter("id", grant), new NpgsqlParameter("actor", ids["support-1"].ToString("D")), new NpgsqlParameter("subject", ids["user-1"].ToString("D")),
            new NpgsqlParameter("purpose", Purpose), new NpgsqlParameter("expiry", expiry), new NpgsqlParameter("tenant", Tenant)));

        // Before E: the read is permitted as user-1, recorded with support-1 as actor; the update is refused as outside the scope.
        server.Clock.Set(expiry.AddSeconds(-1));
        var read = await ItemAsync(browsers["support-1"], HttpMethod.Get, grant);
        Assert.Equal((HttpStatusCode.OK, ids["support-1"].ToString("D"), ids["user-1"].ToString("D"), true), (read.Status, read.Actor, read.Subject, read.Delegated));
        Assert.Equal(("user", "user"), (read.ActorKind, read.SubjectKind));
        Assert.Equal(HttpStatusCode.Forbidden, (await ItemAsync(browsers["support-1"], HttpMethod.Put, grant)).Status);
        var current = await browsers["support-1"].CurrentUserAsync(grant);
        Assert.Equal(ids["support-1"].ToString("D"), current!.Value.GetProperty("actor").GetProperty("id").GetString());
        Assert.Equal(ids["user-1"].ToString("D"), current.Value.GetProperty("effectiveSubject").GetProperty("id").GetString());
        Assert.True(current.Value.GetProperty("delegated").GetBoolean());
        Assert.Equal("item.read", Assert.Single(current.Value.GetProperty("delegatedScope").EnumerateArray()).GetString());
        output.WriteLine($"E-1s: {read.Method} item-1 {(int)read.Status} with actor {read.Actor} and subject {read.Subject}; update refused 403.");

        // Without the delegation, support-1 acts only as itself and holds no item grant; the delegation never widens its own authority.
        Assert.Equal(HttpStatusCode.Forbidden, (await ItemAsync(browsers["support-1"], HttpMethod.Get, null)).Status);
        Assert.False((await browsers["support-1"].CurrentUserAsync())!.Value.GetProperty("delegated").GetBoolean());

        // Another session cannot present support-1's delegation, and user-1's own session is unaffected.
        Assert.Equal(HttpStatusCode.Unauthorized, (await ItemAsync(browsers["support-2"], HttpMethod.Get, grant)).Status);
        var own = await ItemAsync(browsers["user-1"], HttpMethod.Get, null);
        Assert.Equal((HttpStatusCode.OK, ids["user-1"].ToString("D"), ids["user-1"].ToString("D"), false), (own.Status, own.Actor, own.Subject, own.Delegated));

        // After E the request is not authenticated at all, so it cannot fall back to support-1's own authority either.
        server.Clock.Set(expiry.AddSeconds(1));
        Assert.Equal(HttpStatusCode.Unauthorized, (await ItemAsync(browsers["support-1"], HttpMethod.Get, grant)).Status);
        Assert.Null(await browsers["support-1"].CurrentUserAsync(grant));
        output.WriteLine("E+1s: the delegated read was refused 401.");

        // A second delegation, revoked before its expiry, is refused at the next request.
        var second = (await delegations.IssueAsync(new DelegationRequest(ids["support-1"], ids["user-1"], ["item.read"], Purpose, Tenant, server.Clock.Now.AddHours(1)),
            user1, Cancellation)).GrantId!.Value;
        Assert.Equal(HttpStatusCode.OK, (await ItemAsync(browsers["support-1"], HttpMethod.Get, second)).Status);
        Assert.True(await delegations.RevokeAsync(second, user1, Cancellation));
        Assert.Equal(HttpStatusCode.Unauthorized, (await ItemAsync(browsers["support-1"], HttpMethod.Get, second)).Status);
        Assert.Equal(1L, await database.ScalarAsync<long>("SELECT count(*) FROM " + database.Table("Delegations")
            + " WHERE \"Id\" = @id AND \"RevokedAt\" IS NOT NULL AND \"RevokedById\" = @by", new NpgsqlParameter("id", second),
            new NpgsqlParameter("by", ids["user-1"].ToString("D"))));
        output.WriteLine("A revoked delegation was refused 401 at the next request.");

        // The item job, started from support-1's delegated request, runs as svc-items with item.reindex only.
        var third = (await delegations.IssueAsync(new DelegationRequest(ids["support-1"], ids["user-1"], ["item.read"], Purpose, Tenant, server.Clock.Now.AddHours(1)),
            user1, Cancellation)).GrantId!.Value;
        var job = await browsers["support-1"].SendAsync(HttpMethod.Post, "/items/reindex", third, new StringContent(""));
        Assert.Equal(HttpStatusCode.OK, job.StatusCode);
        using var context = JsonDocument.Parse(await job.Content.ReadAsStringAsync(Cancellation));
        var root = context.RootElement;
        Assert.Equal(("svc-items", "service", "svc-items"), (root.GetProperty("actor").GetString(), root.GetProperty("actorKind").GetString(), root.GetProperty("subject").GetString()));
        Assert.False(root.GetProperty("hasSession").GetBoolean());
        Assert.False(root.GetProperty("delegated").GetBoolean());
        Assert.Equal(["item.reindex"], root.GetProperty("scope").EnumerateArray().Select(item => item.GetString()!).ToArray());
        Assert.Equal((true, false, false), (root.GetProperty("reindex").GetBoolean(), root.GetProperty("read").GetBoolean(), root.GetProperty("update").GetBoolean()));
        Assert.Equal(ids["support-1"].ToString("D"), root.GetProperty("requestActor").GetString());
        output.WriteLine($"The item job ran as svc-items (service) with scope item.reindex; the request around it ran as {root.GetProperty("requestActor").GetString()}.");
    }

    private static SessionToken Token(Browser browser) =>
        SessionToken.TryParse(browser.Session, out var token) ? token : throw new InvalidOperationException("The browser holds no session.");

    private static bool Permits(ExecutionPrincipal principal, string permission, IReadOnlyDictionary<string, string[]> grants, DateTimeOffset now) =>
        principal.IsWithinCeiling(permission, Tenant, now)
        && (principal.ServiceScope is not null || grants.TryGetValue(principal.EffectiveSubject.Id, out var held) && held.Contains(permission, StringComparer.Ordinal));

    private sealed record ItemDecision(HttpMethod Method, HttpStatusCode Status, string? Actor, string? ActorKind, string? Subject, string? SubjectKind, bool Delegated);

    private static async Task<ItemDecision> ItemAsync(Browser browser, HttpMethod method, Guid? delegation)
    {
        var response = await browser.SendAsync(method, "/items/item-1", delegation, method == HttpMethod.Get ? null : new StringContent(""));
        if (response.StatusCode != HttpStatusCode.OK) return new(method, response.StatusCode, null, null, null, null, false);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Cancellation));
        var root = body.RootElement;
        return new(method, response.StatusCode, root.GetProperty("actor").GetString(), root.GetProperty("actorKind").GetString(),
            root.GetProperty("subject").GetString(), root.GetProperty("subjectKind").GetString(), root.GetProperty("delegated").GetBoolean());
    }

    private static void MapItems(WebApplication application, IReadOnlyDictionary<string, string[]> grants)
    {
        IResult Decide(IExecutionPrincipalAccessor principals, TimeProvider clock, string permission)
        {
            if (principals.Current is not { } principal) return Results.StatusCode(StatusCodes.Status401Unauthorized);
            if (!Permits(principal, permission, grants, clock.GetUtcNow())) return Results.StatusCode(StatusCodes.Status403Forbidden);
            return Results.Json(new
            {
                actor = principal.Actor.Id, actorKind = Kind(principal.Actor), subject = principal.EffectiveSubject.Id, subjectKind = Kind(principal.EffectiveSubject),
                delegated = principal.IsDelegated
            });
        }
        application.MapGet("/items/item-1", (IExecutionPrincipalAccessor principals, TimeProvider clock) => Decide(principals, clock, "item.read"));
        application.MapPut("/items/item-1", (IExecutionPrincipalAccessor principals, TimeProvider clock) => Decide(principals, clock, "item.update"));
        application.MapPost("/items/reindex", async (IExecutionPrincipalAccessor principals, IServiceIdentityScopes scopes, TimeProvider clock) =>
        {
            if (principals.Current is null) return Results.StatusCode(StatusCodes.Status401Unauthorized);
            await using var job = scopes.Begin("svc-items");
            var principal = job.Services.GetRequiredService<IExecutionPrincipalAccessor>().Current!;
            var now = clock.GetUtcNow();
            return Results.Json(new
            {
                actor = principal.Actor.Id, actorKind = Kind(principal.Actor), subject = principal.EffectiveSubject.Id, hasSession = principal.SessionId is not null,
                delegated = principal.IsDelegated, scope = principal.ServiceScope!.Order(StringComparer.Ordinal).ToArray(),
                reindex = Permits(principal, "item.reindex", grants, now), read = Permits(principal, "item.read", grants, now),
                update = Permits(principal, "item.update", grants, now), requestActor = principals.Current?.Actor.Id
            });
        });
    }

    private static string Kind(PrincipalReference reference) => reference.Kind == IdentityKind.User ? "user" : "service";
}
