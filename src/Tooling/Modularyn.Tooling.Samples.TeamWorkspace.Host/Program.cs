using Microsoft.Extensions.Configuration;

namespace Modularyn.Tooling.Samples.TeamWorkspace.Host;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        // The provisioning command's one argument is an account identifier; any other form, or any further argument, is refused.
        Guid account = default;
        var provisioning = args is [HostOperatorProvisioning.Command, var named]
            && Guid.TryParseExact(named, "D", out account) && account != Guid.Empty;
        if (!provisioning && (args.Length > 1 || args.Length == 1 && args[0] is not ("--migrate" or "--describe-deployment"))) return 2;
        try
        {
            if (args is ["--describe-deployment"])
            {
                Console.Out.Write(DeploymentArtifacts.Describe().ToJson());
                return 0;
            }
            var configuration = new ConfigurationBuilder().AddEnvironmentVariables().Build();
            if (provisioning)
            {
                await using var provisioner = await TeamWorkspaceHost.BuildProvisioningAsync(configuration);
                var outcome = await HostOperatorProvisioning.RunAsync(provisioner, account, CancellationToken.None);
                (outcome == ProvisioningOutcome.Provisioned ? Console.Out : Console.Error).WriteLine(HostOperatorProvisioning.Describe(outcome));
                return outcome == ProvisioningOutcome.Provisioned ? 0 : 3;
            }
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
