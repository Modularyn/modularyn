#!/usr/bin/env bash
# Runs one command of the G-02 evidence assembler, EvidenceBundle in the repository tests, on the pinned SDK (docs/ci.md,
# "Evidence records"):
#
#   collect   sanitizes the runner report MODULARYN_EVIDENCE_REPORT of MODULARYN_EVIDENCE_PROJECT into the bundle as
#             reports/MODULARYN_EVIDENCE_LABEL.json, keeping only the methods listed in MODULARYN_EVIDENCE_METHODS if it is
#             set, then removes the runner report;
#   select    writes to MODULARYN_EVIDENCE_METHODS the planned methods of MODULARYN_EVIDENCE_PROJECT that no collected
#             report ran;
#   assemble  writes the bundle half: a record for each plan case, then its manifest.
#
# MODULARYN_EVIDENCE_DIR, MODULARYN_SOURCE_COMMIT, MODULARYN_EVIDENCE_MODE and MODULARYN_EVIDENCE_RUN_ID name the bundle half.
# The test executable's own filter runs the one EvidenceBundleCommandTests method that performs the command, -failSkips fails
# it if it skips, and its report must show that it ran once and passed. The assembler needs no database, so it never receives
# the test credentials. It expects a Release build of the repository tests.
set -euo pipefail

fail() {
  echo "::error::$*"
  exit 1
}

case "${1:-}" in
  collect) method=Collect_one_runner_report_into_the_bundle ;;
  select) method=Select_the_planned_methods_that_no_collected_report_ran ;;
  assemble) method=Assemble_the_bundle_half_from_the_collected_reports ;;
  *) fail "Usage: $0 collect|select|assemble" ;;
esac

[ -n "${RUNNER_TEMP:-}" ] || fail "This script runs in GitHub Actions, which sets RUNNER_TEMP."
tests=tests/Modularyn.Repository.Tests/bin/Release/net10.0/Modularyn.Repository.Tests.dll
[ -f "${tests}" ] || fail "${tests} is not built."

report="${RUNNER_TEMP}/evidence-bundle-$1.json"
rm -f "${report}"
status=0
env -u MODULARYN_TEST_POSTGRESQL MODULARYN_EVIDENCE_COMMAND="$1" dotnet "${tests}" -noLogo -noColor -failSkips -method "Modularyn.Repository.Tests.EvidenceBundleCommandTests.${method}" -result-ctrf "${report}" || status=$?
[ "${status}" -eq 0 ] && [ -f "${report}" ] \
  && [ "$(jq -r '.results.summary.tests' "${report}")" = "1" ] && [ "$(jq -r '.results.summary.passed' "${report}")" = "1" ] \
  || fail "The evidence $1 command must run once and pass."
rm -f "${report}"
