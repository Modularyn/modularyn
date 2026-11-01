// Generated from the endpoint inventory by ApiGenerator.
#nullable enable
using System.Text.Json;
using Modularyn.Framework.Sdk.Client;

namespace Modularyn.Framework.Sdk.Client.Tests;

public sealed record BootstrapConfiguration(IReadOnlyDictionary<string, JsonElement> Settings, IReadOnlyDictionary<string, JsonElement> Features);
public sealed record ItemView(Guid Id, string Title, string? Note, Guid Version);
public sealed record CreateItemRequest(string Title, string? Note);
public sealed record UpdateItemRequest(string Title, string? Note, Guid Version);
public sealed record OperationView(Guid OperationId, string State, ItemView? Item);
public sealed record CheckpointView(string State);

public sealed class FixtureClient(ApiClient transport)
{
    public const int OldestVersion = 1;
    public const int CurrentVersion = 2;
    public Task<BootstrapConfiguration> GetConfigurationAsync(string tenant, CancellationToken cancellationToken = default) =>
        transport.SendAsync<BootstrapConfiguration>("GET", "/api/v{version}/t/{tenant}/configuration", new Dictionary<string, string> { ["tenant"] = tenant }, null, null, cancellationToken);
    public Task<ItemView> GetItemAsync(string tenant, string id, CancellationToken cancellationToken = default) =>
        transport.SendAsync<ItemView>("GET", "/api/v{version}/t/{tenant}/items/{id}", new Dictionary<string, string> { ["tenant"] = tenant, ["id"] = id }, null, null, cancellationToken);
    public Task<ItemView> CreateItemAsync(string tenant, CreateItemRequest body, Guid operationId, CancellationToken cancellationToken = default) =>
        transport.SendAsync<ItemView>("POST", "/api/v{version}/t/{tenant}/items", new Dictionary<string, string> { ["tenant"] = tenant }, body, operationId, cancellationToken);
    public Task<ItemView> UpdateItemAsync(string tenant, string id, UpdateItemRequest body, Guid operationId, CancellationToken cancellationToken = default) =>
        transport.SendAsync<ItemView>("PUT", "/api/v{version}/t/{tenant}/items/{id}", new Dictionary<string, string> { ["tenant"] = tenant, ["id"] = id }, body, operationId, cancellationToken);
    public Task<OperationView> GetOperationAsync(string tenant, string id, CancellationToken cancellationToken = default) =>
        transport.SendAsync<OperationView>("GET", "/api/v{version}/t/{tenant}/operations/{id}", new Dictionary<string, string> { ["tenant"] = tenant, ["id"] = id }, null, null, cancellationToken);
    public Task<CheckpointView> GetCheckpointAsync(string tenant, string id, CancellationToken cancellationToken = default) =>
        transport.SendAsync<CheckpointView>("GET", "/api/v{version}/t/{tenant}/checkpoints/{id}", new Dictionary<string, string> { ["tenant"] = tenant, ["id"] = id }, null, null, cancellationToken);
}
