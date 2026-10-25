using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;

namespace Modularyn.Repository.Tests;

internal static partial class ArchitectureContract
{
    internal static List<string> Validate(IReadOnlyDictionary<string, string> files, string repository = "modularyn")
    {
        ArchitecturePolicy policy;
        try
        {
            policy = ArchitecturePolicy.Load(files);
        }
        catch (Exception exception) when (IsContractError(exception))
        {
            return ["DEP013: The dependency companion, guide and names contract must agree. " + exception.Message];
        }

        var errors = new List<string>();
        try
        {
            var (nodes, edges) = Discover(files, repository, policy, errors);
            CheckGraph(policy, nodes, edges, errors);
        }
        catch (Exception exception) when (IsContractError(exception))
        {
            errors.Add(policy.Diagnostic("DEP002", "Invalid reference declaration: " + exception.Message));
        }
        return errors;
    }

    private static bool IsContractError(Exception exception) => exception is ContractViolation or JsonException or XmlException
        or KeyNotFoundException or InvalidOperationException or ArgumentException or FormatException;

    private static void CheckGraph(ArchitecturePolicy policy, IReadOnlyList<ArchitectureNode> nodes,
        IReadOnlyList<ArchitectureEdge> edges, List<string> errors)
    {
        foreach (var edge in edges)
        {
            var source = edge.Source;
            var target = edge.Target;
            var detail = source.Id + " -> " + target.Id + " (" + edge.Kind + ").";
            void Reject(string code) => errors.Add(policy.Diagnostic(code, detail));

            if (!policy.ReferenceKinds(source.Edition, target.Edition).Contains(edge.Kind, StringComparer.Ordinal))
                Reject(source.Edition == "community" && target.Edition == "paid" ? "DEP003" : "DEP004");
            if (source.Edition == "paid" && target.Edition == "community")
            {
                if (edge.Kind == "PackageReference" && (edge.Version is null || !Regex.IsMatch(edge.Version, policy.Text("paidCommunityVersionPattern"),
                    RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)))) Reject("DEP005");
            }
            if (source.IsTest) continue;
            if (target.IsTest)
            {
                Reject("DEP012");
                continue;
            }
            if (!policy.Targets("familyReferences", source.Family).Contains(target.Family, StringComparer.Ordinal)) Reject("DEP006");
            if (source.Role == "domain" && (policy.List("domainForbiddenRoles").Contains(target.Role, StringComparer.Ordinal)
                || policy.List("domainForbiddenFamilies").Contains(target.Family, StringComparer.Ordinal)
                || target.Id.Split('.').Any(segment => policy.List("domainForbiddenSegments").Contains(segment, StringComparer.Ordinal))))
                Reject("DEP007");

            if (target.Module.Length > 0)
            {
                if (source.Module == target.Module)
                {
                    if (!policy.Targets("sameModuleReferences", source.Role).Contains(target.Role, StringComparer.Ordinal)) Reject("DEP008");
                }
                else if (source.Module.Length == 0)
                {
                    if (target.Role != policy.Text("eventContractRole")) Reject("DEP009");
                }
                else if (!policy.Targets("otherModuleReferences", source.Role).Contains(target.Role, StringComparer.Ordinal)) Reject("DEP009");
            }
            else if (source.Module.Length > 0)
            {
                if (!policy.Targets("supportFamilyReferences", source.Role).Contains(target.Family, StringComparer.Ordinal)) Reject("DEP010");
                if (target.Family == "Framework" && (source.Role is "domain" or "application-contracts"))
                {
                    var suffixes = policy.List(source.Role == "domain" ? "domainFrameworkSuffixes" : "contractsFrameworkSuffixes");
                    if (!suffixes.Any(suffix => target.Id.EndsWith(suffix, StringComparison.Ordinal))) Reject("DEP010");
                }
            }
        }

        foreach (var scope in policy.List("cycleScopes"))
        {
            string Key(ArchitectureNode node) => scope == "module" && node.Module.Length > 0 ? node.Module : node.Id;
            var graph = nodes.Concat(edges.Select(edge => edge.Target)).Select(Key).Distinct(StringComparer.Ordinal)
                .ToDictionary(key => key, _ => new SortedSet<string>(StringComparer.Ordinal), StringComparer.Ordinal);
            foreach (var edge in edges)
            {
                // Only role edges internal to a module disappear from the collapsed graph.
                if (scope == "module" && edge.Source.Module.Length > 0 && edge.Source.Module == edge.Target.Module) continue;
                graph[Key(edge.Source)].Add(Key(edge.Target));
            }
            var visited = new HashSet<string>(StringComparer.Ordinal);
            var active = new HashSet<string>(StringComparer.Ordinal);
            var chain = new List<string>();
            foreach (var node in graph.Keys.Order(StringComparer.Ordinal)) Visit(node);

            void Visit(string node)
            {
                if (active.Contains(node))
                {
                    var cycle = chain.Skip(chain.IndexOf(node)).Append(node);
                    errors.Add(policy.Diagnostic("DEP011", scope + ": " + string.Join(" -> ", cycle) + "."));
                    return;
                }
                if (!visited.Add(node)) return;
                active.Add(node);
                chain.Add(node);
                foreach (var target in graph[node]) Visit(target);
                chain.RemoveAt(chain.Count - 1);
                active.Remove(node);
            }
        }
    }
}
