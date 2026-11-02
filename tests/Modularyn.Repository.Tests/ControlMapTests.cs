using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace Modularyn.Repository.Tests;

public sealed class ControlMapTests
{
    [Fact]
    public void Every_TM_G02_control_has_evidence_or_a_named_residual_risk()
    {
        var files = RepositoryFiles.Current;
        Assert.Empty(ControlMapContract.Validate(files[ControlMapContract.MapPath], files));
    }

    [Theory]
    [InlineData("missing-control", "the map must name every TM-G02 control once")]
    [InlineData("duplicate-control", "the map must name every TM-G02 control once")]
    [InlineData("reordered-control", "the map must name every TM-G02 control once")]
    [InlineData("unmapped-control", "a control needs evidence or a residual")]
    [InlineData("undeclared-test", "existing test must name a declared test method in its project and class")]
    [InlineData("wrong-project", "existing test must name a declared test method in its project and class")]
    [InlineData("duplicate-test", "duplicate cited test")]
    [InlineData("entry-residual-unknown", "the entry's residuals must be exactly those that name the control")]
    [InlineData("entry-residual-dropped", "the entry's residuals must be exactly those that name the control")]
    [InlineData("residual-unknown-control", "residual names an unknown control")]
    [InlineData("residual-unknown-order", "residual names an unknown order")]
    [InlineData("residual-id", "residual ids must be unique and ascending")]
    [InlineData("residual-blank", "statement must be nonblank")]
    [InlineData("residual-source", "residual source must exist")]
    [InlineData("residual-acceptance", "acceptance must be pending or name its acceptance record")]
    [InlineData("order-unstated", "SEC-04: every security order states its residuals")]
    [InlineData("readme-unlisted", "the README must list every residual")]
    [InlineData("unknown-field", "fields must be exactly")]
    public void Control_map_drift_is_rejected_for_its_specific_reason(string mutation, string diagnostic)
    {
        var files = new Dictionary<string, string>(RepositoryFiles.Current, StringComparer.Ordinal);
        var map = JsonNode.Parse(files[ControlMapContract.MapPath])!;
        var entries = map["entries"]!.AsArray();
        var residuals = map["residuals"]!.AsArray();
        JsonNode Entry(string control) => entries.Single(entry => (string)entry!["control"]! == control)!;
        var withResidual = entries.First(entry => entry!["residuals"]!.AsArray().Count > 0 && entry["tests"]!.AsArray().Count > 0)!;
        switch (mutation)
        {
            case "missing-control": entries.RemoveAt(10); break;
            case "duplicate-control": entries.Add(entries[0]!.DeepClone()); break;
            case "reordered-control":
                var moved = entries[0]!;
                entries.RemoveAt(0);
                entries.Add(moved);
                break;
            case "unmapped-control":
                // TM-G02-005 has no evidence; without its residual it is mapped to nothing.
                Entry("TM-G02-005")["residuals"] = new JsonArray();
                foreach (var residual in residuals)
                    residual!["controls"] = new JsonArray(residual["controls"]!.AsArray().Select(item => (string)item!)
                        .Where(control => control != "TM-G02-005").Select(control => JsonValue.Create(control)).ToArray<JsonNode?>());
                break;
            case "undeclared-test": entries[0]!["tests"]![0]!["name"] = (string)entries[0]!["tests"]![0]!["name"]! + "_Renamed"; break;
            case "wrong-project":
                entries[0]!["tests"]![0]!["project"] = "tests/Modularyn.Framework.Tenancy.Tests/Modularyn.Framework.Tenancy.Tests.csproj";
                break;
            case "duplicate-test": withResidual["tests"]!.AsArray().Add(withResidual["tests"]![0]!.DeepClone()); break;
            case "entry-residual-unknown": withResidual["residuals"]!.AsArray().Add("RR-G02-999"); break;
            case "entry-residual-dropped": withResidual["residuals"]!.AsArray().RemoveAt(0); break;
            case "residual-unknown-control": residuals[0]!["controls"]!.AsArray().Add("TM-W-001"); break;
            case "residual-unknown-order": residuals[0]!["orders"]!.AsArray().Add("CODE-06"); break;
            case "residual-id": residuals[1]!["id"] = (string)residuals[0]!["id"]!; break;
            case "residual-blank": residuals[0]!["statement"] = " "; break;
            case "residual-source": residuals[0]!["sources"]!.AsArray().Add("docs/no-such-file.md"); break;
            case "residual-acceptance": residuals[0]!["acceptance"] = "accepted"; break;
            case "order-unstated":
                foreach (var residual in residuals)
                    residual!["orders"] = new JsonArray(residual["orders"]!.AsArray().Select(item => (string)item!)
                        .Where(order => order != "SEC-04").Select(order => JsonValue.Create(order)).ToArray<JsonNode?>());
                break;
            case "readme-unlisted":
                files[ControlMapContract.ReadmePath] = files[ControlMapContract.ReadmePath].Replace("`" + (string)residuals[0]!["id"]! + "`", "", StringComparison.Ordinal);
                break;
            case "unknown-field": map["status"] = "qualified"; break;
            default: throw new ArgumentOutOfRangeException(nameof(mutation));
        }
        files[ControlMapContract.MapPath] = map.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        var diagnostics = ControlMapContract.Validate(files[ControlMapContract.MapPath], files);
        Assert.Contains(diagnostics, message => message.Contains(diagnostic, StringComparison.Ordinal));
    }
}
