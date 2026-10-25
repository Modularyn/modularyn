using System.Xml.Linq;

namespace Modularyn.Repository.Tests;

internal static partial class ArchitectureContract
{
    private static readonly string[] ReferenceItems = ["ProjectReference", "PackageReference", "FrameworkReference", "Reference", "COMReference"];

    private static (List<ArchitectureNode> Nodes, List<ArchitectureEdge> Edges) Discover(
        IReadOnlyDictionary<string, string> files, string repository, ArchitecturePolicy policy, List<string> errors)
    {
        void Reject(string code, string detail) => errors.Add(policy.Diagnostic(code, detail));
        if (repository is not ("modularyn" or "modularyn-pro"))
            throw new InvalidOperationException("Unknown source repository.");
        var paths = files.Keys.Where(path => path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)).Order(StringComparer.Ordinal).ToArray();
        var solution = XDocument.Parse(files["Modularyn.slnx"]);
        var listed = solution.Descendants().Where(element => element.Name.LocalName == "Project")
            .Select(element => (string?)element.Attribute("Path") ?? "").ToArray();
        if (solution.Root?.Name.LocalName != "Solution" || paths.Length == 0 || paths.Length != listed.Length
            || !paths.ToHashSet(StringComparer.Ordinal).SetEquals(listed))
            Reject("DEP001", "Modularyn.slnx must list every project exactly once.");

        var versions = ReadVersions(files, policy, errors);
        foreach (var (path, content) in files.Where(pair => pair.Key.EndsWith(".props", StringComparison.OrdinalIgnoreCase)
            || pair.Key.EndsWith(".targets", StringComparison.OrdinalIgnoreCase)))
        {
            var document = XDocument.Parse(content);
            if (document.Descendants().Any(element => ReferenceItems.Contains(element.Name.LocalName, StringComparer.OrdinalIgnoreCase)
                || element.Name.LocalName.Equals("Import", StringComparison.OrdinalIgnoreCase)
                || element.Name.LocalName.Equals("Sdk", StringComparison.OrdinalIgnoreCase)
                || element.Name.LocalName.Equals("Target", StringComparison.OrdinalIgnoreCase)
                || element.Name.LocalName.Equals("UsingTask", StringComparison.OrdinalIgnoreCase)))
                Reject("DEP002", path + ": auxiliary build files cannot introduce references, imports or custom targets.");
        }

        var nodes = new List<ArchitectureNode>();
        var documents = new Dictionary<string, XDocument>(StringComparer.Ordinal);
        foreach (var path in paths)
        {
            var document = XDocument.Parse(files[path]);
            documents.Add(path, document);
            try
            {
                var node = ProjectIdentity(path, document, repository, policy);
                nodes.Add(node);
                if (nodes.Count(candidate => candidate.Id == node.Id) != 1) Reject("DEP001", "Duplicate project identity: " + node.Id);
            }
            catch (Exception exception) when (IsContractError(exception))
            {
                Reject("DEP001", path + ": " + exception.Message);
            }
        }
        var byPath = nodes.ToDictionary(node => node.Path, StringComparer.Ordinal);
        foreach (var node in nodes.Where(node => node.IsTest && node.Path != policy.TestProject))
        {
            var tested = node.Id[..^6];
            if (!nodes.Any(candidate => !candidate.IsTest && (candidate.Id == tested
                || tested == policy.Identifier("toolAssembly") && candidate.Id == policy.Identifier("toolPackage"))))
                Reject("DEP001", node.Id + ": test project must have its product project in the solution.");
        }

        var edges = new List<ArchitectureEdge>();
        foreach (var node in nodes)
        {
            var document = documents[node.Path];
            if ((string?)document.Root?.Attribute("Sdk") != "Microsoft.NET.Sdk"
                || document.Descendants().Any(element => element.Name.LocalName.Equals("Import", StringComparison.OrdinalIgnoreCase)
                    || element.Name.LocalName.Equals("Sdk", StringComparison.OrdinalIgnoreCase)
                    || element.Name.LocalName.Equals("Target", StringComparison.OrdinalIgnoreCase)
                    || element.Name.LocalName.Equals("UsingTask", StringComparison.OrdinalIgnoreCase)))
                Reject("DEP002", node.Id + ": use the ordinary SDK without explicit imports or custom targets.");
            CheckPlatformConfiguration(node, document, policy, errors);
            foreach (var reference in document.Descendants().Where(element => ReferenceItems.Contains(element.Name.LocalName, StringComparer.OrdinalIgnoreCase)))
            {
                var kind = reference.Name.LocalName;
                var include = (string?)reference.Attribute("Include") ?? "";
                if (!ReferenceItems.Contains(kind, StringComparer.Ordinal))
                {
                    Reject("DEP002", node.Id + ": reference item names must use canonical casing.");
                    continue;
                }
                if (kind is "Reference" or "COMReference")
                {
                    Reject("DEP002", node.Id + ": assembly and COM references bypass the package/project graph.");
                    continue;
                }
                if (!Literal(include) || reference.Attributes().Any(attribute => attribute.Name.LocalName is "Update" or "Remove" or "Exclude")
                    || reference.Parent?.Name.LocalName != "ItemGroup" || reference.Parent?.Parent != document.Root)
                {
                    Reject("DEP002", node.Id + ": reference requires one literal Include in a project ItemGroup.");
                    continue;
                }
                if ((node.Role is "domain" or "application-contracts") && policy.ForbiddenPlatform(include))
                    Reject(node.Role == "domain" ? "DEP007" : "DEP010", node.Id + " -> " + include + " (" + kind + ").");
                if (kind == "ProjectReference")
                {
                    var targetPath = Resolve(node.Path, include);
                    if (targetPath is not null && byPath.TryGetValue(targetPath, out var target))
                    {
                        edges.Add(new(node, target, kind));
                        continue;
                    }
                    // Classify a missing external target as well, so edition violations name their actual rule.
                    var filename = include.Replace('\\', '/').Split('/')[^1];
                    if (filename.EndsWith(".csproj", StringComparison.Ordinal))
                    {
                        var id = filename[..^7];
                        if (id == policy.Identifier("toolAssembly")) id = policy.Identifier("toolPackage");
                        if (policy.IsModularyn(id) && !id.EndsWith(".Tests", StringComparison.Ordinal))
                        {
                            try { edges.Add(new(node, policy.Package(id), kind)); }
                            catch (Exception exception) when (IsContractError(exception)) { Reject("DEP001", exception.Message); }
                        }
                    }
                    Reject("DEP002", node.Id + ": project reference must resolve to a discovered local project: " + include);
                    continue;
                }
                if (kind == "FrameworkReference")
                {
                    if (policy.IsModularyn(include)) Reject("DEP002", node.Id + ": Modularyn references must be project or package references.");
                    continue;
                }

                var version = ReferenceVersion(reference, include, versions, node.Id, policy, errors);
                if (policy.IsModularyn(include))
                {
                    try { edges.Add(new(node, policy.Package(include), kind, version)); }
                    catch (Exception exception) when (IsContractError(exception)) { Reject("DEP001", exception.Message); }
                }
            }
        }
        return (nodes, edges);
    }

    private static ArchitectureNode ProjectIdentity(string path, XDocument document, string repository, ArchitecturePolicy policy)
    {
        if (document.Root?.Name.LocalName != "Project") throw new InvalidOperationException("Expected a project document.");
        var filename = path.Split('/')[^1];
        if (!filename.EndsWith(".csproj", StringComparison.Ordinal)) throw new InvalidOperationException("Project extension must be .csproj.");
        var name = filename[..^7];
        if (Property(document, "AssemblyName", name) != name || Property(document, "RootNamespace", name) != name)
            throw new InvalidOperationException("Assembly and namespace must match the project filename.");
        if (path.StartsWith("tests/", StringComparison.Ordinal))
        {
            if (!name.EndsWith(".Tests", StringComparison.Ordinal) || path != "tests/" + name + "/" + name + ".csproj"
                || Property(document, "IsPackable", "true") != "false" || Property(document, "PackageId", name) != name)
                throw new InvalidOperationException("Test project identity and layout must match the names contract.");
            return new(name, "Tests", "", "", repository == "modularyn-pro" ? "paid" : "community", repository, path, true);
        }
        var id = Property(document, "PackageId", name);
        if (id != (name == policy.Identifier("toolAssembly") ? policy.Identifier("toolPackage") : name))
            throw new InvalidOperationException("Package identity must match the project, with only the named tool exception.");
        var node = policy.Package(id) with { Path = path };
        if (node.Repository != repository || path != policy.SourcePath(node))
            throw new InvalidOperationException("Source family directory and repository must match the package identity.");
        return node;
    }

    private static string Property(XDocument document, string key, string fallback)
    {
        var values = document.Descendants().Where(element => element.Name.LocalName == key).ToArray();
        if (values.Length > 1 || values.Any(value => value.AncestorsAndSelf().Any(element => element.Attribute("Condition") is not null)
            || value.Parent?.Name.LocalName != "PropertyGroup" || value.Parent?.Parent != document.Root || !Literal(value.Value)))
            throw new InvalidOperationException("Project identity must be unique, literal and unconditional: " + key);
        return values.Length == 0 ? fallback : values[0].Value;
    }

    private static Dictionary<string, string> ReadVersions(IReadOnlyDictionary<string, string> files, ArchitecturePolicy policy, List<string> errors)
    {
        var versions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var document = XDocument.Parse(files["Directory.Packages.props"]);
        foreach (var item in document.Descendants().Where(element => element.Name.LocalName == "PackageVersion"))
        {
            var id = (string?)item.Attribute("Include") ?? "";
            var value = (string?)item.Attribute("Version");
            if (!Literal(id) || item.Attributes().Any(attribute => attribute.Name.LocalName is "Update" or "Remove")
                || item.Elements().Any() || value is null || item.AncestorsAndSelf().Any(element => element.Attribute("Condition") is not null)
                || item.Parent?.Name.LocalName != "ItemGroup" || item.Parent?.Parent != document.Root || !versions.TryAdd(id, value))
                errors.Add(policy.Diagnostic("DEP002", "Central versions must have one unconditional Include and Version: " + id));
        }
        return versions;
    }

    private static string? ReferenceVersion(XElement reference, string id, Dictionary<string, string> versions,
        string source, ArchitecturePolicy policy, List<string> errors)
    {
        var attributes = reference.Attributes().Where(attribute => attribute.Name.LocalName is "Version" or "VersionOverride").ToArray();
        var elements = reference.Elements().Where(element => element.Name.LocalName is "Version" or "VersionOverride").ToArray();
        var explicitValue = attributes.Select(attribute => attribute.Value).Concat(elements.Select(element => element.Value)).ToArray();
        if (explicitValue.Length > 1 || attributes.Any(attribute => attribute.Name.LocalName == "VersionOverride")
            || elements.Any(element => element.Name.LocalName == "VersionOverride" || element.Attribute("Condition") is not null)
            || explicitValue.Length > 0 && versions.ContainsKey(id))
        {
            errors.Add(policy.Diagnostic("DEP002", source + ": ambiguous package version: " + id));
            return null;
        }
        return explicitValue.Length == 1 ? explicitValue[0] : versions.GetValueOrDefault(id);
    }

    private static void CheckPlatformConfiguration(ArchitectureNode node, XDocument document, ArchitecturePolicy policy, List<string> errors)
    {
        if (node.Role is not ("domain" or "application-contracts")) return;
        var sdk = (string?)document.Root?.Attribute("Sdk") ?? "";
        var forbidden = policy.List("domainForbiddenSdks").Contains(sdk, StringComparer.OrdinalIgnoreCase)
            || document.Descendants().Any(element => policy.List("domainForbiddenProperties").Contains(element.Name.LocalName, StringComparer.Ordinal)
                && !element.Value.Equals("false", StringComparison.OrdinalIgnoreCase));
        if (forbidden) errors.Add(policy.Diagnostic(node.Role == "domain" ? "DEP007" : "DEP010", node.Id + ": adapter platform configuration."));
    }

    private static bool Literal(string value) => !string.IsNullOrWhiteSpace(value) && value.Trim() == value
        && !value.Any(character => character is '$' or '@' or '%' or '*' or '?' or ';' or ':' or '\r' or '\n');

    private static string? Resolve(string source, string include)
    {
        var normalized = include.Replace('\\', '/');
        if (normalized.StartsWith('/')) return null;
        var parts = source.Split('/').SkipLast(1).ToList();
        foreach (var part in normalized.Split('/'))
        {
            if (part is "" or ".") continue;
            if (part == "..")
            {
                if (parts.Count == 0) return null;
                parts.RemoveAt(parts.Count - 1);
            }
            else parts.Add(part);
        }
        return string.Join('/', parts);
    }
}
