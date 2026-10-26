using Modularyn.Framework.Modularity.Abstractions;
using Xunit;

namespace Modularyn.Framework.Modularity.Tests;

public sealed class CompositionTests
{
    private static readonly SemanticVersion One = SemanticVersion.Parse("1.0.0");
    private static readonly CompositionEnvironment Environment = new(SemanticVersion.Parse("10.0.0"), One);
    private static readonly VersionRange CompatibleB = new(SemanticVersion.Parse("2.0.0"), true, SemanticVersion.Parse("2.2.0"), true);
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Theory(DisplayName = "AC-001-B01 inclusive compatibility edges")]
    [InlineData("1.9.9", false)]
    [InlineData("2.0.0", true)]
    [InlineData("2.2.0", true)]
    [InlineData("2.2.1", false)]
    public async Task AC_001_B01_Only_the_inclusive_edges_satisfy_the_same_constraint(string candidate, bool expected)
    {
        var selection = new CompositionSelection([
            new("A", One, [new("B", CompatibleB)]),
            new("B", SemanticVersion.Parse(candidate))], Environment);
        var result = await new ModuleComposer().ComposeAsync(selection, Cancellation);
        Assert.Equal(expected, result.Succeeded);
        if (expected)
        {
            Assert.Empty(result.Diagnostics);
            Assert.Equal(new[] { "B", "A" }, result.Plan!.Modules.Select(module => module.Id));
            Assert.Equal(candidate, result.Plan.Modules[0].Version.ToString());
        }
        else
        {
            Assert.Null(result.Plan);
            var diagnostic = Assert.Single(result.Diagnostics);
            Assert.Equal("MDC003", diagnostic.Code);
            Assert.Equal(new[] { "A", "B" }, diagnostic.ModuleIds);
            Assert.Contains("[2.0.0, 2.2.0]", diagnostic.Message, StringComparison.Ordinal);
            Assert.Contains($"candidate 'B' is {candidate}", diagnostic.Message, StringComparison.Ordinal);
            Assert.Contains("Select a version satisfying", diagnostic.Message, StringComparison.Ordinal);
        }
    }

    [Theory(DisplayName = "AC-001-P01 discovery order does not alter the resolved graph")]
    [InlineData("ABC")]
    [InlineData("ACB")]
    [InlineData("BAC")]
    [InlineData("BCA")]
    [InlineData("CAB")]
    [InlineData("CBA")]
    public async Task AC_001_P01_Resolves_each_exact_version_in_dependency_order(string discovery)
    {
        var graph = Graph().ToDictionary(module => module.Id, StringComparer.Ordinal);
        var selection = new CompositionSelection(discovery.Select(id => graph[id.ToString()]), Environment);
        var result = await new ModuleComposer().ComposeAsync(selection, Cancellation);
        Assert.True(result.Succeeded);
        Assert.Empty(result.Diagnostics);
        Assert.Equal(new[] { "C", "B", "A" }, result.Plan!.Modules.Select(module => module.Id));
        Assert.Equal(new[] { "1.0.0", "2.1.0", "1.0.0" }, result.Plan.Modules.Select(module => module.Version.ToString()));
        Assert.Equal(new[] { "B" }, result.Plan.Dependencies["A"]);
        Assert.Equal(new[] { "C" }, result.Plan.Dependencies["B"]);
        Assert.Empty(result.Plan.Dependencies["C"]);
        Assert.Same(selection, result.Plan.Selection);
    }

    [Fact(DisplayName = "AC-001-N01 complete dependency cycle")]
    public async Task AC_001_N01_Names_the_complete_cycle_and_a_corrective_action()
    {
        var graph = Graph();
        graph[2] = new("C", One, [new("A", VersionRange.Any)]);
        var result = await Compose(graph);
        Assert.Null(result.Plan);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal("MDC002", diagnostic.Code);
        Assert.Equal(new[] { "A", "B", "C", "A" }, diagnostic.ModuleIds);
        Assert.Contains("A -> B -> C -> A", diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains("Remove or redirect", diagnostic.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false, false, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    public async Task Optional_dependencies_may_be_absent_but_selected_versions_must_be_compatible(bool present, bool incompatible, bool valid)
    {
        var modules = new List<ModuleManifest> { new("A", One, [new("B", CompatibleB, optional: true)]) };
        if (present) modules.Add(new("B", SemanticVersion.Parse(incompatible ? "3.0.0" : "2.1.0")));
        var result = await Compose(modules);
        Assert.Equal(valid, result.Succeeded);
        if (!valid) Assert.Equal("MDC003", Assert.Single(result.Diagnostics).Code);
        else Assert.Equal(present ? new[] { "B", "A" } : new[] { "A" }, result.Plan!.Modules.Select(module => module.Id));
    }

    [Fact]
    public async Task Present_optional_dependencies_participate_in_cycles()
    {
        var result = await Compose([new("A", One, [new("B", VersionRange.Any, true)]), new("B", One, [new("A", VersionRange.Any)])]);
        Assert.Equal("MDC002", Assert.Single(result.Diagnostics).Code);
        Assert.Null(result.Plan);
    }

    [Theory]
    [InlineData("contract")]
    [InlineData("capability")]
    [InlineData("provider")]
    public async Task Requirements_resolve_versions_and_order_providers_before_consumers(string kind)
    {
        var (consumer, provider) = ProvisionPair(kind, "1.0.0");
        var result = await Compose([consumer, provider]);
        Assert.True(result.Succeeded);
        Assert.Equal(new[] { "Z", "A" }, result.Plan!.Modules.Select(module => module.Id));
        Assert.Equal(new[] { "Z" }, result.Plan.Dependencies["A"]);
        var missing = await Compose([consumer]);
        Assert.Equal("MDC007", Assert.Single(missing.Diagnostics).Code);
        Assert.Contains($"missing {kind} 'Shared'", missing.Diagnostics[0].Message, StringComparison.Ordinal);
        var (_, incompatible) = ProvisionPair(kind, "2.0.0");
        var mismatch = await Compose([consumer, incompatible]);
        var diagnostic = Assert.Single(mismatch.Diagnostics);
        Assert.Equal("MDC008", diagnostic.Code);
        Assert.Contains("[1.0.0, 1.0.0]", diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains("'Z' 2.0.0", diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Multiple_nonexclusive_capability_providers_are_ordered_and_allowed()
    {
        var result = await Compose([
            new("A", One, requiredCapabilities: [new("Shared", VersionRange.Any)]),
            new("B", One, providedCapabilities: [new("Shared", One)]),
            new("C", One, providedCapabilities: [new("Shared", One)])]);
        Assert.True(result.Succeeded);
        Assert.Equal(new[] { "B", "C", "A" }, result.Plan!.Modules.Select(module => module.Id));
    }

    [Fact]
    public async Task Provision_edges_participate_in_cycle_detection()
    {
        var result = await Compose([
            new("A", One, [new("B", VersionRange.Any)], providedContracts: [new("Shared", One)]),
            new("B", One, requiredContracts: [new("Shared", VersionRange.Any)])]);
        Assert.Null(result.Plan);
        Assert.Equal(new[] { "A", "B", "A" }, Assert.Single(result.Diagnostics).ModuleIds);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Exclusive_capabilities_conflict_even_if_only_one_declaration_is_exclusive(bool bothExclusive)
    {
        var result = await Compose([
            new("A", One, providedCapabilities: [new("Shared", One, true)]),
            new("B", One, providedCapabilities: [new("Shared", One, bothExclusive)])]);
        Assert.Null(result.Plan);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal("MDC004", diagnostic.Code);
        Assert.Equal(new[] { "A", "B" }, diagnostic.ModuleIds);
        Assert.Contains("Exclusive capability 'Shared'", diagnostic.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("GET", "/records", "GET", "/records", false)]
    [InlineData("get", "/Records/", "GET", "/records", false)]
    [InlineData("GET", "/records/{id}", "GET", "/records/{name}", false)]
    [InlineData("GET", "/records/{id:int}", "GET", "/records/{key:int}", false)]
    [InlineData("*", "/records", "POST", "/records", false)]
    [InlineData("GET", "/records", "POST", "/records", true)]
    [InlineData("GET", "/records", "GET", "/other", true)]
    public async Task Route_conflicts_respect_methods_case_slashes_and_parameter_names(string leftMethod, string leftPath, string rightMethod, string rightPath, bool valid)
    {
        var result = await Compose([new("A", One, routes: [new(leftMethod, leftPath)]), new("B", One, routes: [new(rightMethod, rightPath)])]);
        Assert.Equal(valid, result.Succeeded);
        if (valid) Assert.Empty(result.Diagnostics);
        else
        {
            var diagnostic = Assert.Single(result.Diagnostics);
            Assert.Equal("MDC005", diagnostic.Code);
            Assert.Contains(leftPath, diagnostic.Message, StringComparison.Ordinal);
            Assert.Contains(rightPath, diagnostic.Message, StringComparison.Ordinal);
            Assert.Equal(new[] { "A", "B" }, diagnostic.ModuleIds);
        }
    }

    [Fact]
    public async Task Duplicate_modules_and_owned_declarations_are_refused()
    {
        var duplicate = await Compose([new("A", One), new("A", SemanticVersion.Parse("2.0.0"))]);
        Assert.Equal("MDC006", Assert.Single(duplicate.Diagnostics).Code);
        Assert.Null(duplicate.Plan);
        var declarations = await Compose([new("A", One, definitions: [new("Entry", One), new("Entry", One)])]);
        Assert.Equal("MDC011", Assert.Single(declarations.Diagnostics).Code);
        Assert.Contains("definition 'Entry'", declarations.Diagnostics[0].Message, StringComparison.Ordinal);
        Assert.Null(declarations.Plan);
    }

    [Fact]
    public async Task Runtime_and_framework_ranges_are_validated_before_planning()
    {
        var result = await Compose([new("A", One, runtimeVersions: VersionRange.Exact(One), frameworkVersions: VersionRange.Exact(SemanticVersion.Parse("2.0.0")))]);
        Assert.Null(result.Plan);
        Assert.Equal(new[] { "MDC009", "MDC010" }, result.Diagnostics.Select(item => item.Code));
        Assert.Contains("candidate runtime is 10.0.0", result.Diagnostics[0].Message, StringComparison.Ordinal);
        Assert.Contains("candidate framework is 1.0.0", result.Diagnostics[1].Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Validation_extensions_are_ordered_stable_and_cannot_issue_a_plan_after_a_refusal()
    {
        var calls = new List<string>();
        var validators = new[] { new RecordingValidator("late", 20, calls), new RecordingValidator("first", 10, calls, refuse: true), new RecordingValidator("second", 10, calls) };
        var result = await new ModuleComposer(validators).ComposeAsync(new([new("A", One)], Environment), Cancellation);
        Assert.Equal(new[] { "first", "second", "late" }, calls);
        Assert.Null(result.Plan);
        Assert.Equal("MDC900", Assert.Single(result.Diagnostics).Code);
        Assert.All(validators, validator => Assert.Equal(Cancellation, validator.Token));
    }

    [Fact]
    public async Task Empty_selection_is_valid_and_precancelled_composition_runs_no_extensions()
    {
        Assert.Empty((await Compose([])).Plan!.Modules);
        var calls = new List<string>();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Cancellation);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ModuleComposer([new RecordingValidator("validator", 0, calls)])
            .ComposeAsync(new([], Environment), cancellation.Token));
        Assert.Empty(calls);
    }

    [Fact]
    public async Task Long_graphs_do_not_depend_on_recursive_stack_depth()
    {
        var modules = Enumerable.Range(0, 3000).Select(index => new ModuleManifest($"M{index:D4}", One,
            index == 2999 ? [] : [new($"M{index + 1:D4}", VersionRange.Any)]));
        var result = await Compose(modules);
        Assert.True(result.Succeeded);
        Assert.Equal("M2999", result.Plan!.Modules[0].Id);
        Assert.Equal("M0000", result.Plan.Modules[^1].Id);
    }

    private static Task<CompositionResult> Compose(IEnumerable<ModuleManifest> modules) => new ModuleComposer().ComposeAsync(new(modules, Environment), Cancellation);
    private static ModuleManifest[] Graph() => [new("A", One, [new("B", CompatibleB)]), new("B", SemanticVersion.Parse("2.1.0"), [new("C", VersionRange.Exact(One))]), new("C", One)];

    private static (ModuleManifest Consumer, ModuleManifest Provider) ProvisionPair(string kind, string version)
    {
        ModuleProvision[] provided = [new("Shared", SemanticVersion.Parse(version))];
        ModuleRequirement[] required = [new("Shared", VersionRange.Exact(One))];
        return kind switch
        {
            "contract" => (new("A", One, requiredContracts: required), new("Z", One, providedContracts: provided)),
            "capability" => (new("A", One, requiredCapabilities: required), new("Z", One, providedCapabilities: provided)),
            _ => (new("A", One, requiredProviders: required), new("Z", One, providedProviders: provided))
        };
    }

    private sealed class RecordingValidator(string name, int order, List<string> calls, bool refuse = false) : ICompositionValidator
    {
        public int Order => order;
        public CancellationToken Token { get; private set; }
        public ValueTask<IReadOnlyList<CompositionDiagnostic>> ValidateAsync(CompositionSelection selection, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Token = cancellationToken;
            calls.Add(name);
            return ValueTask.FromResult<IReadOnlyList<CompositionDiagnostic>>(refuse
                ? [new("MDC900", "Module 'A' requires configuration 'Mode'. Supply Mode before activation.", ["A"])] : []);
        }
    }
}
