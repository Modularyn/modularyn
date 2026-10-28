// Generated from the endpoint inventory by ApiGenerator.
#nullable enable
using System.Text.Json;
using Modularyn.Framework.Sdk.Client;

namespace Modularyn.Framework.Sdk.Client;

public sealed record BootstrapConfiguration(IReadOnlyDictionary<string, JsonElement> Settings, IReadOnlyDictionary<string, JsonElement> Features);

public sealed class ModularynClient(ApiClient transport)
{
    public const int OldestVersion = 1;
    public const int CurrentVersion = 2;
    public Task<BootstrapConfiguration> GetConfigurationAsync(string tenant, CancellationToken cancellationToken = default) =>
        transport.SendAsync<BootstrapConfiguration>("GET", "/api/v{version}/t/{tenant}/configuration", new Dictionary<string, string> { ["tenant"] = tenant }, null, null, cancellationToken);
}
