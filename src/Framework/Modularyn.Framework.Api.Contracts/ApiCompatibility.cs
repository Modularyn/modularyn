using System.Text.Json;

namespace Modularyn.Framework.Api.Contracts;

public static class ApiCompatibility
{
    /// <summary>While version windows overlap, existing bindings and fields retain their declared meaning.</summary>
    public static IReadOnlyList<string> BreakingChanges(ApiInventory previous, ApiInventory current)
    {
        previous.Validate();
        current.Validate();
        var changes = new List<string>();
        if (current.CurrentVersion < previous.CurrentVersion) changes.Add("The current version moved backwards.");
        if (current.OldestVersion > previous.CurrentVersion) return changes;
        foreach (var endpoint in previous.Endpoints)
        {
            var next = current.Endpoints.SingleOrDefault(candidate => candidate.Name == endpoint.Name);
            if (next is null || JsonSerializer.Serialize(next with { Deprecated = endpoint.Deprecated }) != JsonSerializer.Serialize(endpoint))
                changes.Add("An existing endpoint changed: " + endpoint.Name);
        }
        foreach (var model in previous.Models)
        {
            var next = current.Models.SingleOrDefault(candidate => candidate.Name == model.Name);
            if (next is null || JsonSerializer.Serialize(next) != JsonSerializer.Serialize(model))
                changes.Add("An existing model changed: " + model.Name);
        }
        return changes;
    }
}
