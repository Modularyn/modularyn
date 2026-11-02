using Xunit;

namespace Modularyn.Tooling.Samples.TeamWorkspace.Tests;

// The migration lock and the shared test lock cover the database, so real-provider fixtures run one at a time.
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class TeamWorkspacePostgreSqlCollection
{
    public const string Name = "PostgreSQL team workspace";
}

/// <summary>
/// The sample's end-to-end smoke scenario through the real host, Kestrel over TLS and PostgreSQL. It is one sequential
/// story: each step builds on the tenant, members, note and attachment the earlier steps created.
/// </summary>
[Collection(TeamWorkspacePostgreSqlCollection.Name)]
[Trait("Requires", "PostgreSQL")]
public sealed class SmokeTests
{
    [Fact]
    public async Task The_team_workspace_runs_end_to_end_against_PostgreSQL()
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(3));
        await SmokeScenario.RunAsync(timeout.Token);
    }
}
