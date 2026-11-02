"""G-02 result assembly. Standard library only; never executes or alters a test.

Runner reports are untrusted. Retain only planned identities and bounded outcomes,
never display names, output, exception text, attachments or environment values.
"""

import argparse
import copy
import datetime
import hashlib
import json
import re
import sys
import xml.etree.ElementTree as ET
from pathlib import Path


PLAN = "docs/evidence/g02-plan.json"
CORPUS = "docs/acceptance/corpus.json"
MODES = ("single-host", "single-host-container")
SAMPLE = "Modularyn.Tooling.Samples.TeamWorkspace.Tests"
STATES = ("passed", "failed", "skipped", "not-run")


class Refusal(ValueError):
    """Fixed diagnostics only: an input may contain credentials."""


def require(condition, code):
    if not condition:
        raise Refusal(code)


def unique_object(pairs):
    result = {}
    for key, value in pairs:
        require(key not in result, "duplicate JSON property")
        result[key] = value
    return result


def read(path):
    return json.loads(Path(path).read_text(encoding="utf-8-sig"), object_pairs_hook=unique_object)


def encoded(value):
    return (json.dumps(value, ensure_ascii=True, indent=2) + "\n").encode("utf-8")


def digest(value):
    return hashlib.sha256(value).hexdigest()


def safe_path(root, location):
    require(isinstance(location, str) and re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9._-]*(/[A-Za-z0-9][A-Za-z0-9._-]*)*", location),
            "invalid retained location")
    root = Path(root).absolute()
    require(not any(parent.is_symlink() for parent in (root, *root.parents)), "linked bundle root")
    target = root
    for part in location.split("/"):
        require(part not in (".", ".."), "escaping retained location")
        target /= part
        require(not target.is_symlink(), "linked retained location")
    return target


def write_new(root, location, value):
    path = safe_path(root, location)
    path.parent.mkdir(parents=True, exist_ok=True)
    data = value if isinstance(value, bytes) else encoded(value)
    with path.open("xb") as stream:
        stream.write(data)
    return {"location": location, "sha256": digest(data)}


def retained(root, item):
    path = safe_path(root, item["location"])
    require(path.is_file() and digest(path.read_bytes()) == item["sha256"], "retained bytes differ")
    return path


def binding(measurement):
    return {key: copy.deepcopy(measurement[key]) for key in ("sourceCommit", "runId", "deploymentMode", "tuple")}


def validate_measurement(value, commit, mode, run):
    require(set(value) == {"sourceCommit", "runId", "deploymentMode", "tuple", "date", "implementationArtifacts"},
            "unbounded measurement fields")
    require(re.fullmatch(r"[0-9a-f]{40}", commit or "") is not None, "exact commit required")
    require(mode in MODES and re.fullmatch(r"[a-z0-9][a-z0-9._-]{0,79}", run or ""), "invalid run or mode")
    require(value["sourceCommit"] == commit and value["deploymentMode"] == mode and value["runId"] == run,
            "measurement binding differs")
    deployment = value["tuple"]["deployment"]
    require(deployment["topology"] == mode and deployment["architecture"] == "x64"
            and deployment["operatingSystem"]["name"] == "Linux", "measurement platform differs")
    require((mode == MODES[0] and deployment["containerImageDigest"] is None)
            or (mode == MODES[1] and re.fullmatch(r"sha256:[0-9a-f]{64}", deployment["containerImageDigest"] or "")),
            "measurement image differs")
    require(value["tuple"]["database"]["name"] == "PostgreSQL"
            and all(p["mode"] == "real" for p in value["tuple"]["providers"]), "real providers required")
    require(datetime.date.fromisoformat(value["date"]).isoformat() == value["date"], "invalid measurement date")


def planned_tests(plan):
    return [test for case in plan["cases"] for test in case["plannedTests"]]


def row_names(test, mode):
    name = test["name"]
    if ".CompositionAcceptanceTests.AC_001_" in name:
        return [("mode", name + '(mode: "' + mode + '")')]
    if ".G02AC008Tests." in name:
        return [(client, name + '(client: "' + client + '")') for client in ("dotnet", "typescript")]
    return [("method", name)]


def container_reason(test):
    if ".G02AC008Tests." in test["name"]:
        return "The planned method requires both advertised clients; Node.js is absent from the pinned fixture base."
    if test["layer"] == "ui":
        return "The planned browser method requires the pinned browser and its runtime dependencies, absent from the fixture image."
    return None


def sanitize_report(raw, project, plan, mode, report_format):
    """Return every expected row, including missing rows. Data arguments never survive."""
    tests = [t for t in planned_tests(plan) if t["project"] == project]
    expected = {display: (t["name"], row) for t in tests for row, display in row_names(t, mode)}
    observed = {key: [] for key in expected}
    if report_format == "ctrf":
        document = json.loads(raw, object_pairs_hook=unique_object)
        rows = [(r.get("name"), r.get("status")) for r in document["results"]["tests"]]
        mapping = {s: s for s in STATES}
        mapping.update({"pending": "not-run", "other": "not-run"})
    else:
        require("<!DOCTYPE" not in raw and "<!ENTITY" not in raw, "XML entities refused")
        document = ET.fromstring(raw)
        rows = [(r.get("testName"), r.get("outcome")) for r in document.iter() if r.tag.rsplit("}", 1)[-1] == "UnitTestResult"]
        mapping = {"Passed": "passed", "Failed": "failed", "NotExecuted": "skipped", "Inconclusive": "not-run",
                   "Aborted": "not-run", "Timeout": "failed", "Error": "failed"}
    for display, state in rows:
        if display in observed:
            observed[display].append(mapping.get(state, "not-run"))
    result = []
    for display, (method, row) in expected.items():
        statuses = observed[display]
        require(len(statuses) <= 1, "duplicate planned test row")
        result.append({"name": method, "row": row, "status": statuses[0] if statuses else "missing"})
    return result


def method_outcome(test, mode, rows):
    expected = {row for row, _ in row_names(test, mode)}
    actual = [r for r in rows if r["name"] == test["name"]]
    require(len({r["row"] for r in actual}) == len(actual), "duplicate planned test row")
    require(all(r["row"] in expected and r["status"] in (*STATES, "missing") for r in actual), "invalid planned result")
    # Missing or skipped evidence blocks the complete case, even if another row failed.
    if {r["row"] for r in actual} != expected or any(r["status"] in ("missing", "skipped", "not-run") for r in actual):
        return "blocked"
    return "failed" if any(r["status"] == "failed" for r in actual) else "passed"


def case_outcome(statuses, dependencies):
    if "blocked" in statuses:
        return "blocked"
    if "failed" in statuses:
        return "failed"
    if any(d["status"] != "satisfied" or not d["evidenceRecords"] for d in dependencies):
        return "blocked"
    return "passed"


def validate_report(report, measurement, plan):
    require(set(report) == {"binding", "project", "rows"}, "unbounded report fields")
    require(report["binding"] == binding(measurement), "report commit or tuple differs")
    tests = [test for test in planned_tests(plan) if test["project"] == report["project"]]
    require(bool(tests), "unknown report project")
    expected = {(test["name"], row) for test in tests for row, _ in row_names(test, measurement["deploymentMode"])}
    seen = set()
    for row in report["rows"]:
        require(set(row) == {"name", "row", "status"}, "unbounded result fields")
        key = (row["name"], row["row"])
        require(key in expected and key not in seen and row["status"] in (*STATES, "missing"), "invalid planned result")
        seen.add(key)


def projected_tuple(measured, case):
    value = copy.deepcopy(measured)
    if not case["needs"]["filesystemStore"]:
        value["providers"] = [p for p in value["providers"] if p["name"] == "EF Core PostgreSQL"]
    return value


def assemble(repository, directory, commit, mode, run):
    repository, directory = Path(repository), Path(directory)
    plan = read(repository / PLAN)
    corpus_bytes = (repository / CORPUS).read_text(encoding="utf-8-sig").replace("\r\n", "\n").encode()
    corpus = {c["id"]: c for c in json.loads(corpus_bytes)["cases"]}
    owners = {e["id"]: e["owner"] for e in read(repository / "docs/coverage/register.json")["entries"]}
    measurement = read(safe_path(directory, "measurement.json"))
    validate_measurement(measurement, commit, mode, run)
    require(not list(directory.rglob("record.json")) and not (directory / "manifest.json").exists(), "duplicate record or run")
    for item in measurement["implementationArtifacts"]:
        retained(directory, item)
    reports = []
    report_artifacts = []
    for path in sorted((directory / "reports").glob("*.json")):
        path = safe_path(directory, "reports/" + path.name)
        report = read(path)
        validate_report(report, measurement, plan)
        reports.append(report)
        report_artifacts.append({"location": "reports/" + path.name, "sha256": digest(path.read_bytes())})
    entries = []
    for case in plan["cases"]:
        case_id = case["caseId"]
        source = corpus[case_id]
        tests = [{key: t[key] for key in ("project", "name", "layer")} for t in case["plannedTests"]]
        tuple_value = projected_tuple(measurement["tuple"], case)
        results, limitations = [], []
        for test in tests:
            # A second report of an executed row is an ambiguity, never a way to choose a pass.
            rows = [r for report in reports if report["project"] == test["project"] for r in report["rows"]
                    if r["name"] == test["name"] and r["status"] != "missing"]
            status = method_outcome(test, mode, rows)
            results.append({**test, "status": status, "rows": rows})
            if status != "passed":
                reason = container_reason(test) if mode == MODES[1] else None
                limitations.append(test["name"] + ": " + (reason or "Every planned row must execute and pass; see the bounded result states."))
        # No functional record can promote its own security prerequisites. The plan's unmet state is retained.
        dependencies = copy.deepcopy(case["securityDependencies"])
        require(all(d["status"] == "unmet" and not d["evidenceRecords"] for d in dependencies), "security evidence needs Builder review")
        limitations.extend(d["family"] + " prerequisite evidence for this commit and tuple has not been supplied by the Builder."
                           for d in dependencies)
        if case_id in ("AC-003-B01", "AC-003-P01"):
            limitations.append("The advertised .NET and TypeScript client observations are covered by argument about the shared server boundary; these methods execute direct API and server entry points.")
        if case_id == "AC-003-N01":
            limitations.append("The second advertised client is covered by argument about the shared server boundary, not by execution of that client.")
        if mode == MODES[1]:
            limitations.append("Execution uses the test-only composition fixture image; the published sample image's HTTP readiness is checked separately.")
        prefix = run + "/" + case_id.lower() + "/" + mode + "/"
        observations = []
        artifacts = copy.deepcopy(measurement["implementationArtifacts"])
        attachments = []
        observed = [r["name"] + ": " + r["status"] + "." for r in results]
        observation_path = safe_path(directory, "observations/" + case_id.lower() + ".json")
        if case["family"] == "AC-001":
            if observation_path.is_file():
                observation = read(observation_path)
                require(observation["sourceCommit"] == commit and observation["runId"] == run
                        and observation["deploymentMode"] == mode and observation["caseId"] == case_id,
                        "composition observation binding differs")
                for key in ("runtime", "database", "deployment", "providers", "tenancyMode"):
                    require(observation["tuple"][key] == measurement["tuple"][key], "composition tuple differs")
                measured_versions = {item["id"]: item["version"] for item in measurement["tuple"]["moduleVersions"]}
                require(all(measured_versions.get(item["id"]) == item["version"] for item in observation["tuple"]["moduleVersions"]),
                        "composition implementation version differs")
                for item in observation["implementationArtifacts"]:
                    retained(directory, item)
                    if item not in artifacts:
                        artifacts.append(item)
                observations = observation["observations"]
                observed.extend(observation["observedOutcomes"])
                attachments.append({"location": "observations/" + case_id.lower() + ".json", "sha256": digest(observation_path.read_bytes())})
                integration = next(r for r in results if r["layer"] == "real-provider-integration")
                require(integration["status"] != "passed" or observation["integrationOutcome"] == "passed", "composition outcome differs")
            else:
                limitations.append("The composition method retained no detailed observations; case evidence is incomplete.")
                results.append({"name": "composition-observations", "status": "blocked", "rows": []})
        outcome = case_outcome([r["status"] for r in results], dependencies)
        result = {**binding(measurement), "tuple": tuple_value, "caseId": case_id, "tests": results,
                  "observations": observations, "status": outcome}
        result_artifact = write_new(directory, prefix + "result.json", result)
        supporting = [artifact for report, artifact in zip(reports, report_artifacts)
                      if report["project"] in {t["project"] for t in tests}]
        record = {
            "format": "modularyn.evidence-record", "formatVersion": 1,
            "recordId": "urn:modularyn:evidence:g02:" + case_id.lower() + ":" + mode + ":" + run,
            "kind": "verification", "gate": "G-02", "subjects": [
                {"id": identity, "kind": "requirement", "owner": owners[identity]} for identity in source["requirements"]],
            "caseId": case_id, "family": case["family"], "profile": "reference",
            "workOrder": next((t["workOrder"] for t in case["plannedTests"] if t["layer"] == "real-provider-integration"),
                              case["plannedTests"][0]["workOrder"]),
            "sourceCommit": commit, "corpus": {"location": CORPUS, "sha256": digest(corpus_bytes)},
            "tuple": tuple_value, "implementationArtifacts": artifacts, "tests": tests,
            "fixtureInputs": source["preconditions"], "expectedOutcomes": source["expectedOutcome"],
            "observedOutcomes": observed, "verification": {"status": outcome, "date": measurement["date"],
                                                           "results": [result_artifact, *attachments, *supporting]},
            "documentation": ["docs/evidence/README.md", "docs/acceptance/" + case["family"] + ".md"],
            "knownLimitations": limitations, "securityDependencies": dependencies,
        }
        if case["family"] == "AC-001":
            record["documentation"].append("src/Tooling/Modularyn.Tooling.Samples.TeamWorkspace.Host/README.md")
        entry = write_new(directory, prefix + "record.json", record)
        entries.append({"caseId": case_id, "mode": mode, "tupleSha256": digest(encoded(tuple_value)), **entry})
    manifest = {"format": "modularyn.evidence-bundle", "formatVersion": 1, **binding(measurement),
                "corpusSha256": digest(corpus_bytes),
                "planSha256": digest((repository / PLAN).read_text(encoding="utf-8-sig").replace("\r\n", "\n").encode()),
                "measurement": {"location": "measurement.json", "sha256": digest((directory / "measurement.json").read_bytes())},
                "reports": report_artifacts, "records": entries}
    write_new(directory, "manifest.json", manifest)
    return manifest


def collect(args):
    raw = Path(args.report)
    try:
        plan = read(Path(args.repository) / PLAN)
        measurement = read(safe_path(args.directory, "measurement.json"))
        validate_measurement(measurement, args.commit, args.mode, args.run)
        rows = sanitize_report(raw.read_text(encoding="utf-8-sig"), args.project, plan, args.mode, args.report_format)
        if args.methods:
            selected = set(Path(args.methods).read_text().splitlines())
            rows = [row for row in rows if row["name"] in selected]
        value = {"binding": binding(measurement), "project": args.project, "rows": rows}
        validate_report(value, measurement, plan)
        write_new(args.directory, "reports/" + args.label + ".json", value)
    finally:
        # Raw diagnostics are never bundle artifacts or CI log output.
        raw.unlink(missing_ok=True)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("command", choices=("assemble", "collect", "select"))
    parser.add_argument("--repository", default=".")
    parser.add_argument("--directory", required=True)
    parser.add_argument("--commit", required=True)
    parser.add_argument("--mode", choices=MODES, required=True)
    parser.add_argument("--run", required=True)
    parser.add_argument("--project")
    parser.add_argument("--report")
    parser.add_argument("--report-format", choices=("ctrf", "trx"), default="ctrf")
    parser.add_argument("--label")
    parser.add_argument("--methods")
    args = parser.parse_args()
    try:
        if args.command == "collect":
            collect(args)
        elif args.command == "assemble":
            assemble(args.repository, args.directory, args.commit, args.mode, args.run)
        else:
            plan = read(Path(args.repository) / PLAN)
            seen = set()
            for path in (Path(args.directory) / "reports").glob("*.json"):
                report = read(path)
                if report["project"] == args.project:
                    seen.update(row["name"] for row in report["rows"] if row["status"] != "missing")
            for test in planned_tests(plan):
                if test["project"] == args.project and test["name"] not in seen:
                    if args.mode != MODES[1] or not container_reason(test):
                        print(test["name"])
    except Exception:
        print("Evidence assembly refused invalid, incomplete or conflicting input.", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
