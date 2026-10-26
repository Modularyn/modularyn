using System.Net;
using System.Security.Claims;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Modularyn.Framework.Tenancy.Abstractions;
using Xunit;

namespace Modularyn.Framework.Tenancy.Tests;

// TM-G02-025: host names, routes, headers and claims are only candidates. The effective tenant exists only after the
// candidates agree, the directory names an active tenant, the request's domain is that tenant's or none, and the actor
// is a current member. These unit tests use an in-memory directory; the HTTP test runs on PostgreSQL behind the ingress.
public sealed class TenantResolutionTests
{
    private const string HostA = "tenant-a.example.test";
    private const string HostB = "tenant-b.example.test";
    private readonly MemoryDirectory directory = new();
    private readonly TestSeams seams = new();
    private readonly Guid tenantA;
    private readonly Guid tenantB;
    private readonly Guid suspended;

    public TenantResolutionTests()
    {
        tenantA = directory.Add("Tenant A", domain: HostA);
        tenantB = directory.Add("Tenant B", domain: HostB);
        suspended = directory.Add("Suspended", TenantState.Suspended);
        seams.Membership.Add("user-a", tenantA);
        seams.Membership.Add("user-a", suspended);
        seams.Membership.Add("user-b", tenantB);
    }

    private async Task<TenantDecision> ResolveAsync(ClaimsPrincipal principal, params TenantCandidate[] candidates) =>
        await ResolveAsync(seams, principal, candidates);

    private async Task<TenantDecision> ResolveAsync(TestSeams with, ClaimsPrincipal principal, params TenantCandidate[] candidates)
    {
        await using var services = UnitServices.Build(directory, with);
        await using var scope = services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<TenantExecution>()
            .EstablishAsync(TenantResolutionRequest.ForMember(principal, candidates), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task TM_G02_025_Each_candidate_source_establishes_a_current_member_s_tenant()
    {
        var a = TenantIdentifier.Format(tenantA);
        foreach (var candidates in new[]
        {
            new[] { Candidates.Host(HostA) }, [Candidates.Header(a)], [Candidates.Route(a)], [Candidates.Claim(a)],
            [Candidates.Host("app.example.test"), Candidates.Header(a)],
            [Candidates.Host(HostA), Candidates.Header(a), Candidates.Route(a), Candidates.Claim(a)]
        })
        {
            var decision = await ResolveAsync(TestIdentity.User("user-a"), candidates);
            Assert.True(decision.Succeeded, decision.Code);
            Assert.Equal((TenantScope.Tenant, tenantA, "user-a", TenantContextSource.Request),
                (decision.Context!.Scope, decision.Context.TenantId!.Value, decision.Context.Actor.Subject, decision.Context.Source));
            Assert.Null(decision.Context.HostGrant);
        }
    }

    [Theory]
    [InlineData("upper")]
    [InlineData("braces")]
    [InlineData("whitespace")]
    [InlineData("percent")]
    [InlineData("separator")]
    [InlineData("compact")]
    [InlineData("empty")]
    [InlineData("text")]
    public async Task TM_G02_025_Only_the_canonical_identifier_is_accepted_and_a_variant_is_never_normalized(string variant)
    {
        var a = TenantIdentifier.Format(tenantA);
        var value = variant switch
        {
            "upper" => a.ToUpperInvariant(),
            "braces" => "{" + a + "}",
            "whitespace" => " " + a,
            "percent" => a.Replace("-", "%2D", StringComparison.Ordinal),
            "separator" => a + "/",
            "compact" => tenantA.ToString("N"),
            "empty" => Guid.Empty.ToString("D"),
            _ => "tenant-a"
        };
        foreach (var source in new[] { TenantCandidateSource.Header, TenantCandidateSource.Route, TenantCandidateSource.Claim })
        {
            var decision = await ResolveAsync(TestIdentity.User("user-a"), new TenantCandidate(source, value));
            Assert.Equal(TenancyCodes.Malformed, decision.Code);
        }
        Assert.Equal(TenancyCodes.Malformed, (await ResolveAsync(TestIdentity.User("user-a"), Candidates.Host("Tenant-A.example.test"))).Code);
        Assert.Equal(TenancyCodes.Malformed, (await ResolveAsync(TestIdentity.User("user-a"), Candidates.Host(HostA + "."))).Code);
        Assert.Equal(0, directory.Lookups);
    }

    [Fact]
    public async Task TM_G02_025_Ambiguous_conflicting_or_missing_candidates_are_refused()
    {
        var a = TenantIdentifier.Format(tenantA);
        var b = TenantIdentifier.Format(tenantB);
        var user = TestIdentity.User("user-a");
        Assert.Equal(TenancyCodes.Ambiguous, (await ResolveAsync(user, Candidates.Header(a), Candidates.Header(b))).Code);
        Assert.Equal(TenancyCodes.Ambiguous, (await ResolveAsync(user, Candidates.Claim(a), Candidates.Claim(b))).Code);
        Assert.Equal(TenancyCodes.Conflict, (await ResolveAsync(user, Candidates.Header(a), Candidates.Route(b))).Code);
        Assert.Equal(TenancyCodes.Conflict, (await ResolveAsync(user, Candidates.Host(HostA), Candidates.Header(b))).Code);
        Assert.Equal(TenancyCodes.Conflict, (await ResolveAsync(user, Candidates.Claim(b), Candidates.Header(a))).Code);
        // Another tenant's domain cannot be paired with an identifier of the caller's own tenant.
        Assert.Equal(TenancyCodes.Conflict, (await ResolveAsync(user, Candidates.Host(HostB), Candidates.Header(a))).Code);
        Assert.Equal(TenancyCodes.NoCandidate, (await ResolveAsync(user, Candidates.Host("app.example.test"))).Code);
        Assert.Equal(TenancyCodes.NoCandidate, (await ResolveAsync(user)).Code);
        Assert.Equal(0, seams.Membership.Checks);
    }

    [Fact]
    public async Task TM_G02_025_Unknown_suspended_and_foreign_tenants_are_refused_before_any_context_exists()
    {
        var user = TestIdentity.User("user-a");
        Assert.Equal(TenancyCodes.UnknownTenant, (await ResolveAsync(user, Candidates.Header(Guid.NewGuid()))).Code);
        Assert.Equal(TenancyCodes.UnknownTenant, (await ResolveAsync(user, Candidates.Header(suspended))).Code);
        Assert.Equal(TenancyCodes.NotMember, (await ResolveAsync(user, Candidates.Header(tenantB))).Code);
        Assert.Equal(TenancyCodes.NotMember, (await ResolveAsync(user, Candidates.Host(HostB))).Code);
        // Membership is checked now, not taken from a claim: a principal claiming tenant B is still not its member.
        Assert.Equal(TenancyCodes.NotMember, (await ResolveAsync(TestIdentity.User("user-a", TenantIdentifier.Format(tenantB)))).Code);
    }

    [Fact]
    public async Task TM_G02_025_An_unauthenticated_principal_is_refused_before_any_lookup()
    {
        Assert.Equal(TenancyCodes.Unauthenticated, (await ResolveAsync(TestIdentity.Anonymous(), Candidates.Header(tenantA))).Code);
        Assert.Equal(TenancyCodes.Unauthenticated, (await ResolveAsync(TestIdentity.Anonymous(), Candidates.Host(HostA))).Code);
        Assert.Equal(0, directory.Lookups);
        Assert.Equal(0, seams.Membership.Checks);
    }

    [Fact]
    public async Task TM_G02_025_Absent_or_failing_seams_and_an_unavailable_directory_refuse()
    {
        var user = TestIdentity.User("user-a");
        Assert.Equal(TenancyCodes.SeamUnavailable, (await ResolveWithAsync(identity: false, membership: true, user, Candidates.Header(tenantA))).Code);
        Assert.Equal(TenancyCodes.SeamUnavailable, (await ResolveWithAsync(identity: true, membership: false, user, Candidates.Header(tenantA))).Code);
        seams.Membership.Failing = true;
        Assert.Equal(TenancyCodes.SeamUnavailable, (await ResolveAsync(user, Candidates.Header(tenantA))).Code);
        seams.Membership.Failing = false;
        directory.Unavailable = true;
        var unavailable = await ResolveAsync(user, Candidates.Header(tenantA));
        Assert.Equal(TenancyCodes.DirectoryUnavailable, unavailable.Code);
        Assert.False(unavailable.Terminal);
        directory.Unavailable = false;
        Assert.True((await ResolveAsync(user, Candidates.Header(tenantA))).Succeeded);
    }

    private async Task<TenantDecision> ResolveWithAsync(bool identity, bool membership, ClaimsPrincipal principal, params TenantCandidate[] candidates)
    {
        var services = new ServiceCollection();
        services.AddSingleton<ITenantDirectory>(directory);
        services.AddSingleton(TimeProvider.System);
        services.AddScoped<TenantExecution>();
        seams.Register(services, identity, membership);
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<TenantExecution>()
            .EstablishAsync(TenantResolutionRequest.ForMember(principal, candidates), TestContext.Current.CancellationToken);
    }
}

[Collection(TenancyPostgreSqlCollection.Name)]
[Trait("Requires", "PostgreSQL")]
public sealed class TenancyHttpTests
{
    [Fact]
    public async Task TM_G02_025_Requests_run_in_the_validated_tenant_or_are_refused_alike_before_any_handler()
    {
        await using var database = await TenancyDatabase.CreateAsync();
        await using (var seeding = database.Services()) await database.SeedAsync(seeding);
        await using var server = await TenancyServer.StartAsync(database);
        var a = TenantIdentifier.Format(database.TenantA);
        var b = TenantIdentifier.Format(database.TenantB);
        var unknown = TenantIdentifier.Format(Guid.NewGuid());

        async Task<HttpStatusCode> Status(string path, string host, string? subject, params (string Name, string Value)[] headers)
        {
            var response = await server.SendAsync(HttpMethod.Get, path, host, subject, request =>
            {
                foreach (var (name, value) in headers) request.Headers.TryAddWithoutValidation(name, value);
            });
            if (response.StatusCode != HttpStatusCode.OK) Assert.Empty(await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken));
            return response.StatusCode;
        }

        Assert.Equal(HttpStatusCode.OK, await Status("/context", TenancyDatabase.HostA, "user-a"));
        Assert.Equal(HttpStatusCode.OK, await Status("/context", TenancyDatabase.AppHost, "user-a", (TenancyHttp.HeaderName, a)));
        Assert.Equal(HttpStatusCode.OK, await Status("/t/" + a + "/items", TenancyDatabase.AppHost, "user-a"));
        var handled = server.Handled;

        // Another tenant, an unknown tenant and a tenant the caller is not a member of are refused with one contract.
        Assert.Equal(HttpStatusCode.NotFound, await Status("/context", TenancyDatabase.HostA, "user-a", (TenancyHttp.HeaderName, b)));
        Assert.Equal(HttpStatusCode.NotFound, await Status("/context", TenancyDatabase.HostA, "user-a", (TenancyHttp.HeaderName, unknown)));
        Assert.Equal(HttpStatusCode.NotFound, await Status("/context", TenancyDatabase.AppHost, "user-a", (TenancyHttp.HeaderName, unknown)));
        Assert.Equal(HttpStatusCode.NotFound, await Status("/context", TenancyDatabase.HostB, "user-a"));
        Assert.Equal(HttpStatusCode.NotFound, await Status("/t/" + b + "/items", TenancyDatabase.AppHost, "user-a"));
        Assert.Equal(HttpStatusCode.NotFound, await Status("/t/" + unknown + "/items", TenancyDatabase.AppHost, "user-a"));
        Assert.Equal(HttpStatusCode.NotFound, await Status("/context", TenancyDatabase.HostA, "user-a", (TenancyServer.ClaimHeader, b)));
        Assert.Equal(HttpStatusCode.NotFound, await Status("/context", TenancyDatabase.AppHost, "user-a"));
        // Malformed or ambiguous input is refused as input; an unauthenticated caller before any lookup.
        Assert.Equal(HttpStatusCode.BadRequest, await Status("/context", TenancyDatabase.HostA, "user-a", (TenancyHttp.HeaderName, a.ToUpperInvariant())));
        Assert.Equal(HttpStatusCode.BadRequest, await Status("/context", TenancyDatabase.HostA, "user-a", (TenancyHttp.HeaderName, a), (TenancyHttp.HeaderName, b)));
        Assert.Equal(HttpStatusCode.Unauthorized, await Status("/context", TenancyDatabase.HostA, null));
        // The ingress refuses an unconfigured host before tenancy runs.
        Assert.Equal(HttpStatusCode.BadRequest, await Status("/context", "attacker.example.net", "user-a"));
        Assert.Equal(handled, server.Handled);

        // An unavailable seam answers like any other refusal, and is logged as a warning for operators.
        database.Seams.Membership.Failing = true;
        Assert.Equal(HttpStatusCode.NotFound, await Status("/context", TenancyDatabase.HostA, "user-a"));
        database.Seams.Membership.Failing = false;
        Assert.Contains(server.Events.Recorded, item => item == (8101, LogLevel.Warning));
        Assert.DoesNotContain(server.Events.Recorded, item => item.Id == 8100 && item.Level != LogLevel.Information);

        // Where no endpoint declares a tenant, candidates establish nothing.
        var plain = await server.SendAsync(HttpMethod.Get, "/plain", TenancyDatabase.HostA, "user-a",
            request => request.Headers.Add(TenancyHttp.HeaderName, b));
        Assert.Equal("none", await plain.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        // The directory is read afresh: suspending tenant A refuses its next request, and reactivating it restores access.
        database.Seams.Authority.Grant("host-1", TenantDirectoryAdministration.OperationId);
        var administration = server.Services.GetRequiredService<TenantDirectoryAdministration>();
        var (scope, host) = await Units.EnterHostAsync(server.Services, "host-1", TenantDirectoryAdministration.OperationId);
        await using (scope)
        {
            await administration.SetStateAsync(host, database.TenantA, TenantState.Suspended, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.NotFound, await Status("/context", TenancyDatabase.HostA, "user-a"));
            await administration.SetStateAsync(host, database.TenantA, TenantState.Active, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, await Status("/context", TenancyDatabase.HostA, "user-a"));
        }
    }

    [Theory]
    [InlineData(TenancyPipeline.TenancyBeforeRouting)]
    [InlineData(TenancyPipeline.TenancyOmitted)]
    public async Task TM_G02_025_A_declared_endpoint_refuses_to_run_without_a_context_whatever_the_pipeline_order(TenancyPipeline pipeline)
    {
        await using var database = await TenancyDatabase.CreateAsync();
        await using (var seeding = database.Services()) await database.SeedAsync(seeding);
        database.Seams.Authority.Grant("host-1", TenancyServer.HostReport);
        await using var server = await TenancyServer.StartAsync(database, pipeline);
        foreach (var (path, host, subject) in new[]
        {
            ("/context", TenancyDatabase.HostA, "user-a"), ("/items", TenancyDatabase.HostA, "user-a"),
            ("/t/" + TenantIdentifier.Format(database.TenantA) + "/items", TenancyDatabase.AppHost, "user-a"),
            ("/host/report", TenancyDatabase.AppHost, "host-1")
        })
        {
            var response = await server.SendAsync(HttpMethod.Get, path, host, subject);
            Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
            Assert.Empty(await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken));
        }
        Assert.Equal(0, server.Handled);
        Assert.Empty(server.Observed);
        // Each refusal is the endpoint guard's, not an unhandled failure.
        Assert.Equal(4, server.Events.Recorded.Count(item => item == (8102, LogLevel.Error)));
        Assert.DoesNotContain(server.Events.Recorded, item => item.Level == LogLevel.Critical);
        // Endpoints that declare nothing are unaffected.
        var plain = await server.SendAsync(HttpMethod.Get, "/plain", TenancyDatabase.HostA, "user-a");
        Assert.Equal("none", await plain.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task TM_G02_025_In_the_documented_order_declared_endpoints_run_in_their_declared_context()
    {
        await using var database = await TenancyDatabase.CreateAsync();
        await using (var seeding = database.Services()) await database.SeedAsync(seeding);
        database.Seams.Authority.Grant("host-1", TenancyServer.HostReport);
        await using var server = await TenancyServer.StartAsync(database, TenancyPipeline.Standard);
        Assert.Equal(HttpStatusCode.OK, (await server.SendAsync(HttpMethod.Get, "/context", TenancyDatabase.HostA, "user-a")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await server.SendAsync(HttpMethod.Get, "/items", TenancyDatabase.HostA, "user-a")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await server.SendAsync(HttpMethod.Get, "/host/report", TenancyDatabase.AppHost, "host-1")).StatusCode);
        Assert.Equal(["Tenant", "Tenant", "Host"], server.Observed.Select(snapshot => snapshot.Scope));
    }

    [Fact]
    public async Task TM_G02_026_Each_request_gets_its_own_context_which_ends_with_the_request_on_every_path()
    {
        await using var database = await TenancyDatabase.CreateAsync();
        await using (var seeding = database.Services()) await database.SeedAsync(seeding);
        await using var server = await TenancyServer.StartAsync(database);
        Assert.Equal(HttpStatusCode.OK, (await server.SendAsync(HttpMethod.Get, "/context", TenancyDatabase.HostA, "user-a")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await server.SendAsync(HttpMethod.Get, "/context", TenancyDatabase.HostB, "user-b")).StatusCode);
        Assert.Equal(HttpStatusCode.InternalServerError, (await server.SendAsync(HttpMethod.Get, "/throw", TenancyDatabase.HostA, "user-a")).StatusCode);
        var contexts = server.Contexts.ToArray();
        Assert.Equal(3, contexts.Length);
        Assert.Equal(3, contexts.Select(context => context.UnitId).Distinct().Count());
        Assert.All(contexts, context => Assert.False(context.IsActive));
        Assert.Equal(new Guid?[] { database.TenantA, database.TenantB, database.TenantA }, contexts.Select(context => context.TenantId));
    }
}
