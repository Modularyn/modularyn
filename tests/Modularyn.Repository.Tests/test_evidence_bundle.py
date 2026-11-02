"""Offline metadata tests. All generated inputs stay in the checkout and are deleted.

Synthetic reports prove assembler rules only; they are never provider evidence.
"""

import copy
import json
import re
import shutil
import unittest
import uuid
from pathlib import Path
from types import SimpleNamespace

import evidence_bundle as bundle


ROOT = Path(__file__).resolve().parents[2]


def schema_valid(value, schema, root):
    """Offline evaluation of the shipped schema vocabulary; CI also runs the C# contracts."""
    if "$ref" in schema:
        target = root
        for part in schema["$ref"][2:].split("/"):
            target = target[part]
        if not schema_valid(value, target, root):
            return False
    if "const" in schema and value != schema["const"]:
        return False
    if "enum" in schema and value not in schema["enum"]:
        return False
    if "oneOf" in schema and sum(schema_valid(value, choice, root) for choice in schema["oneOf"]) != 1:
        return False
    kinds = {"object": dict, "array": list, "string": str, "integer": int, "boolean": bool, "null": type(None)}
    if "type" in schema and type(value) is not kinds[schema["type"]]:
        return False
    if isinstance(value, dict):
        if any(key not in value for key in schema.get("required", [])):
            return False
        properties = schema.get("properties", {})
        if schema.get("additionalProperties") is False and any(key not in properties for key in value):
            return False
        if not all(schema_valid(item, properties[key], root) for key, item in value.items() if key in properties):
            return False
    if isinstance(value, list):
        if not schema.get("minItems", 0) <= len(value) <= schema.get("maxItems", len(value)):
            return False
        if schema.get("uniqueItems") and len({json.dumps(item, sort_keys=True) for item in value}) != len(value):
            return False
        if "items" in schema and not all(schema_valid(item, schema["items"], root) for item in value):
            return False
    if isinstance(value, str):
        if len(value) < schema.get("minLength", 0):
            return False
        if "pattern" in schema and re.search(schema["pattern"], value) is None:
            return False
    return True


class Run:
    def __init__(self, mode="single-host"):
        self.directory = ROOT / "artifacts" / ("evidence-metadata-" + uuid.uuid4().hex)
        self.directory.mkdir(parents=True)
        self.plan = bundle.read(ROOT / bundle.PLAN)
        self.mode = mode
        self.commit = "1" * 40
        self.run = "metadata-validation"
        template = bundle.read(ROOT / "docs/evidence/examples/ac-009-r01-single-host-container.json")
        self.tuple = template["tuple"]
        self.tuple["moduleVersions"] = [{"id": package, "version": "1.0.0"} for package in sorted({
            Path(t["project"]).stem[:-6] for t in bundle.planned_tests(self.plan)})]
        self.tuple["deployment"].update(topology=mode, containerImageDigest="sha256:" + "4" * 64 if mode == bundle.MODES[1] else None)
        self.tuple["deployment"]["operatingSystem"]["version"] = "6.8.0"
        self.tuple["ui"] = [{"name": "Razor", "version": self.tuple["runtime"]["aspNetCoreVersion"]}]
        for provider in self.tuple["providers"]:
            provider["version"] = "10.0.3"
        artifact = bundle.write_new(self.directory, "implementation/metadata.dll", b"Metadata validation only.\n")
        self.measurement = {"sourceCommit": self.commit, "runId": self.run, "deploymentMode": mode,
                            "date": "2026-11-02", "tuple": self.tuple, "implementationArtifacts": [artifact]}
        bundle.write_new(self.directory, "measurement.json", self.measurement)
        for project in {t["project"] for t in bundle.planned_tests(self.plan)}:
            rows = [{"name": t["name"], "row": row, "status": "passed"}
                    for t in bundle.planned_tests(self.plan) if t["project"] == project for row, _ in bundle.row_names(t, mode)]
            self.report(project, rows)
        for case in self.plan["cases"]:
            if case["family"] == "AC-001":
                self.observation(case["caseId"])

    def report(self, project, rows):
        path = self.directory / "reports" / (Path(project).stem + ".json")
        path.parent.mkdir(exist_ok=True)
        path.write_bytes(bundle.encoded({"binding": bundle.binding(self.measurement), "project": project, "rows": rows}))
        return path

    def observation(self, case_id):
        value = {**bundle.binding(self.measurement), "caseId": case_id, "integrationOutcome": "passed",
                 "observations": [{"stage": "metadata-only", "constructed": 3}],
                 "observedOutcomes": ["Metadata-validation observation; no provider execution."],
                 "implementationArtifacts": self.measurement["implementationArtifacts"]}
        bundle.write_new(self.directory, "observations/" + case_id.lower() + ".json", value)

    def change_row(self, case_id, status, remove=False):
        test = next(c for c in self.plan["cases"] if c["caseId"] == case_id)["plannedTests"][0]
        path = self.directory / "reports" / (Path(test["project"]).stem + ".json")
        report = bundle.read(path)
        row = next(r for r in report["rows"] if r["name"] == test["name"])
        if remove:
            report["rows"].remove(row)
        else:
            row["status"] = status
        path.write_bytes(bundle.encoded(report))

    def assemble(self):
        return bundle.assemble(ROOT, self.directory, self.commit, self.mode, self.run)

    def record(self, case_id):
        return bundle.read(self.directory / self.run / case_id.lower() / self.mode / "record.json")

    def close(self):
        shutil.rmtree(self.directory)
        if not any(self.directory.parent.iterdir()):
            self.directory.parent.rmdir()


class EvidenceBundleTests(unittest.TestCase):
    def run_fixture(self, mode="single-host"):
        run = Run(mode)
        self.addCleanup(run.close)
        return run

    def test_both_modes_have_exactly_one_record_for_each_of_41_cases(self):
        for mode in bundle.MODES:
            with self.subTest(mode=mode):
                run = self.run_fixture(mode)
                manifest = run.assemble()
                self.assertEqual(41, len(manifest["records"]))
                self.assertEqual({c["caseId"] for c in run.plan["cases"]}, {r["caseId"] for r in manifest["records"]})
                self.assertEqual(41, len(list(run.directory.rglob("record.json"))))
                schema = bundle.read(ROOT / "schemas/evidence-record/evidence-record.schema.json")
                for entry in manifest["records"]:
                    record = bundle.read(bundle.retained(run.directory, entry))
                    self.assertTrue(schema_valid(record, schema, schema), entry["caseId"])
                    self.assertEqual(run.commit, record["sourceCommit"])
                    self.assertEqual(mode, record["tuple"]["deployment"]["topology"])
                    self.assertEqual(bundle.digest(bundle.encoded(record["tuple"])), entry["tupleSha256"])
                    self.assertEqual("blocked", record["verification"]["status"])
                    for artifact in record["implementationArtifacts"] + record["verification"]["results"]:
                        bundle.retained(run.directory, artifact)
                for case_id in ("AC-003-B01", "AC-003-N01", "AC-003-P01"):
                    self.assertTrue(any("covered by argument" in text for text in run.record(case_id)["knownLimitations"]))
                ac001 = run.record("AC-001-P01")
                result = bundle.read(bundle.retained(run.directory, ac001["verification"]["results"][0]))
                self.assertEqual([{"stage": "metadata-only", "constructed": 3}], result["observations"])
                self.assertGreaterEqual(len(ac001["verification"]["results"]), 3)
                self.assertEqual("observations/ac-001-p01.json", ac001["verification"]["results"][1]["location"])

    def test_every_planned_test_and_row_is_required(self):
        for state in ("missing", "skipped", "not-run", "failed"):
            with self.subTest(state=state):
                run = self.run_fixture()
                run.change_row("AC-005-P01", state, remove=state == "missing")
                run.assemble()
                self.assertEqual("failed" if state == "failed" else "blocked", run.record("AC-005-P01")["verification"]["status"])

    def test_a_missing_row_blocks_even_if_another_row_failed(self):
        test = next(t for t in bundle.planned_tests(bundle.read(ROOT / bundle.PLAN)) if ".G02AC008Tests." in t["name"])
        rows = [{"name": test["name"], "row": "dotnet", "status": "failed"}]
        self.assertEqual("blocked", bundle.method_outcome(test, "single-host", rows))
        rows += [{"name": test["name"], "row": "typescript", "status": "passed"}]
        self.assertEqual("failed", bundle.method_outcome(test, "single-host", rows))

    def test_pass_requires_all_methods_and_security_dependencies(self):
        unmet = [{"family": "AC-031", "status": "unmet", "evidenceRecords": []}]
        self.assertEqual("blocked", bundle.case_outcome(["passed", "passed"], unmet))
        self.assertEqual("passed", bundle.case_outcome(["passed", "passed"], []))
        self.assertEqual("failed", bundle.case_outcome(["passed", "failed"], []))
        self.assertEqual("blocked", bundle.case_outcome(["failed", "blocked"], []))

    def test_different_commit_tuple_run_or_mode_is_refused(self):
        for field in ("sourceCommit", "tuple", "runId", "deploymentMode"):
            with self.subTest(field=field):
                run = self.run_fixture()
                path = next((run.directory / "reports").glob("*.json"))
                report = bundle.read(path)
                report["binding"][field] = "different"
                path.write_bytes(bundle.encoded(report))
                with self.assertRaises(bundle.Refusal):
                    run.assemble()

    def test_duplicate_records_reports_and_properties_are_refused(self):
        run = self.run_fixture()
        run.assemble()
        with self.assertRaises(bundle.Refusal):
            run.assemble()
        another = self.run_fixture()
        path = next((another.directory / "reports").glob("*.json"))
        shutil.copyfile(path, path.with_name("second-report.json"))
        with self.assertRaises(bundle.Refusal):
            another.assemble()
        with self.assertRaises(bundle.Refusal):
            json.loads('{"status":"passed","status":"failed"}', object_pairs_hook=bundle.unique_object)

    def test_a_retained_report_cannot_bypass_sanitization(self):
        for mutate in (lambda r: r.update(stdout=uuid.uuid4().hex),
                       lambda r: r["rows"][0].update(message=uuid.uuid4().hex),
                       lambda r: r["rows"][0].update(name=uuid.uuid4().hex),
                       lambda r: r["rows"][0].update(status=uuid.uuid4().hex)):
            run = self.run_fixture()
            path = next((run.directory / "reports").glob("*.json"))
            report = bundle.read(path)
            mutate(report)
            path.write_bytes(bundle.encoded(report))
            with self.assertRaises(bundle.Refusal):
                run.assemble()
            self.assertFalse(list(run.directory.rglob("record.json")))

    def test_report_diagnostics_and_arguments_cannot_reach_retained_results(self):
        plan = bundle.read(ROOT / bundle.PLAN)
        test = bundle.planned_tests(plan)[0]
        marker = uuid.uuid4().hex
        raw = json.dumps({"results": {"tests": [
            {"name": test["name"], "status": "passed", "message": marker, "trace": marker, "stdout": marker},
            {"name": test["name"] + "(" + marker + ")", "status": "passed"}]}})
        rows = bundle.sanitize_report(raw, test["project"], plan, "single-host", "ctrf")
        self.assertFalse(marker in json.dumps(rows))
        self.assertEqual("passed", next(r for r in rows if r["name"] == test["name"])["status"])
        raw = '<TestRun><Results><UnitTestResult testName="' + test["name"] + '" outcome="Failed"><Output>' + marker + '</Output></UnitTestResult></Results></TestRun>'
        rows = bundle.sanitize_report(raw, test["project"], plan, "single-host", "trx")
        self.assertFalse(marker in json.dumps(rows))
        self.assertEqual("failed", next(r for r in rows if r["name"] == test["name"])["status"])

    def test_the_other_deployment_row_never_counts(self):
        plan = bundle.read(ROOT / bundle.PLAN)
        test = plan["cases"][0]["plannedTests"][-1]
        raw = json.dumps({"results": {"tests": [{"name": bundle.row_names(test, "single-host")[0][1], "status": "passed"}]}})
        rows = bundle.sanitize_report(raw, test["project"], plan, "single-host-container", "ctrf")
        self.assertEqual("blocked", bundle.method_outcome(test, "single-host-container", rows))

    def test_collection_removes_raw_diagnostics_after_success_or_refusal(self):
        for invalid in (False, True):
            with self.subTest(invalid=invalid):
                run = self.run_fixture()
                test = bundle.planned_tests(run.plan)[0]
                marker = uuid.uuid4().hex
                raw = run.directory / "runner.raw.json"
                raw.write_text(json.dumps({"results": {"tests": [
                    {"name": test["name"], "status": "passed", "message": marker, "trace": marker}]}}))
                args = SimpleNamespace(repository=ROOT, directory=run.directory, commit="2" * 40 if invalid else run.commit,
                                       mode=run.mode, run=run.run, project=test["project"], report=raw,
                                       report_format="ctrf", label="collected", methods=None)
                if invalid:
                    with self.assertRaises(bundle.Refusal):
                        bundle.collect(args)
                    self.assertFalse((run.directory / "reports/collected.json").exists())
                else:
                    bundle.collect(args)
                    self.assertNotIn(marker, (run.directory / "reports/collected.json").read_text())
                self.assertFalse(raw.exists())

    def test_unsupported_container_methods_have_specific_reasons(self):
        run = self.run_fixture("single-host-container")
        for path in (run.directory / "reports").glob("*.json"):
            report = bundle.read(path)
            unsupported = {t["name"] for t in bundle.planned_tests(run.plan) if bundle.container_reason(t)}
            report["rows"] = [r for r in report["rows"] if r["name"] not in unsupported]
            path.write_bytes(bundle.encoded(report))
        run.assemble()
        self.assertTrue(any("Node.js" in text for text in run.record("AC-008-P01")["knownLimitations"]))
        self.assertTrue(any("browser" in text for text in run.record("AC-003-N02")["knownLimitations"]))

    def test_missing_or_conflicting_composition_observations_never_qualify(self):
        run = self.run_fixture()
        (run.directory / "observations/ac-001-p01.json").unlink()
        run.assemble()
        self.assertTrue(any("no detailed observations" in text for text in run.record("AC-001-P01")["knownLimitations"]))
        another = self.run_fixture()
        path = another.directory / "observations/ac-001-p01.json"
        value = bundle.read(path)
        value["tuple"]["database"]["version"] = "18.0"
        path.write_bytes(bundle.encoded(value))
        with self.assertRaises(bundle.Refusal):
            another.assemble()

    def test_paths_links_hashes_and_xml_entities_are_refused(self):
        run = self.run_fixture()
        for location in ("../escape", "/absolute", "safe/../escape", "safe//escape"):
            with self.assertRaises(bundle.Refusal):
                bundle.safe_path(run.directory, location)
        target = run.directory / "linked"
        target.symlink_to(run.directory / "implementation", target_is_directory=True)
        with self.assertRaises(bundle.Refusal):
            bundle.safe_path(run.directory, "linked/metadata.dll")
        (run.directory / "implementation/metadata.dll").write_bytes(b"changed")
        with self.assertRaises(bundle.Refusal):
            run.assemble()
        with self.assertRaises(bundle.Refusal):
            bundle.sanitize_report('<!DOCTYPE x><TestRun/>', "", run.plan, run.mode, "trx")

    def test_schema_checks_do_not_accept_invalid_records(self):
        run = self.run_fixture()
        run.assemble()
        record = run.record("AC-001-P01")
        schema = bundle.read(ROOT / "schemas/evidence-record/evidence-record.schema.json")
        for mutate in (lambda r: r.update(sourceCommit="unknown"), lambda r: r.update(extra=True),
                       lambda r: r["verification"].update(status="not-run"), lambda r: r["tests"].clear(),
                       lambda r: r["implementationArtifacts"][0].update(sha256=None)):
            changed = copy.deepcopy(record)
            mutate(changed)
            self.assertFalse(schema_valid(changed, schema, schema))

    def test_only_documented_theories_expand_planned_methods(self):
        plan = bundle.read(ROOT / bundle.PLAN)
        for test in bundle.planned_tests(plan):
            method = test["name"].rsplit(".", 1)[1]
            sources = list((ROOT / test["project"]).parent.glob("*.cs"))
            source = next(p.read_text() for p in sources if method + "(" in p.read_text())
            declaration = source[:source.index(method + "(")]
            attributes = declaration[declaration.rfind("\n    [Fact]"):]
            if len(bundle.row_names(test, "single-host")) == 1 and ".CompositionAcceptanceTests." not in test["name"]:
                self.assertTrue("[Theory]" not in attributes, "A new theory needs an explicit expected-row inventory.")


if __name__ == "__main__":
    unittest.main()
