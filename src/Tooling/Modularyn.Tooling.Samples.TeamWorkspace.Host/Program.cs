using Microsoft.Extensions.Configuration;

namespace Modularyn.Tooling.Samples.TeamWorkspace.Host;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Length > 1 || args.Length == 1 && args[0] is not ("--migrate" or "--describe-deployment")) return 2;
        try
        {
            if (args is ["--describe-deployment"])
            {
                Console.Out.Write(DeploymentArtifacts.Describe().ToJson());
                return 0;
            }
            var configuration = new ConfigurationBuilder().AddEnvironmentVariables().Build();
            await using var app = await TeamWorkspaceHost.BuildAsync(configuration);
            if (args is ["--migrate"]) await TeamWorkspaceHost.MigrateAsync(app, CancellationToken.None);
            else await app.RunAsync();
            return 0;
        }
        catch (Exception)
        {
            // Configuration/provider exceptions may embed credentials. Only a fixed startup diagnostic leaves this process.
            Console.Error.WriteLine("Team workspace startup refused; check deployment configuration and migration readiness.");
            return 1;
        }
    }
}
