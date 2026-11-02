#!/usr/bin/env bash
# Assembles and checks the complete G-02 bundle half that this job's run wrote (docs/ci.md, "Evidence records"). The evidence
# assembler, run on the pinned SDK through evidence-bundle.sh, writes the half's records and manifest. The repository test
# CiEvidenceTests applies the repository's own evidence contracts to every record beneath MODULARYN_EVIDENCE_DIR, for the
# commit in MODULARYN_SOURCE_COMMIT and the deployment mode in MODULARYN_EVIDENCE_MODE. The test executable's own filter runs
# that one test, -failSkips fails it if it skips, and its report must show that it ran and passed. The script then prints
# each record's SHA-256 and the manifest. It expects a Release build of the repository tests.
set -euo pipefail

fail() {
  echo "::error::$*"
  exit 1
}

[ -n "${RUNNER_TEMP:-}" ] || fail "This script runs in GitHub Actions, which sets RUNNER_TEMP."
[[ "${MODULARYN_SOURCE_COMMIT:-}" =~ ^[0-9a-f]{40}$ ]] || fail "MODULARYN_SOURCE_COMMIT must be the exact head."
[[ "${MODULARYN_EVIDENCE_MODE:-}" =~ ^(single-host|single-host-container)$ ]] || fail "MODULARYN_EVIDENCE_MODE must be single-host or single-host-container."
[ -d "${MODULARYN_EVIDENCE_DIR:-}" ] || fail "The run wrote no evidence directory at ${MODULARYN_EVIDENCE_DIR:-(unset)}."
tests=tests/Modularyn.Repository.Tests/bin/Release/net10.0/Modularyn.Repository.Tests.dll
[ -f "${tests}" ] || fail "${tests} is not built."

bash .github/scripts/evidence-bundle.sh assemble
[ -f "${MODULARYN_EVIDENCE_DIR}/manifest.json" ] || fail "The complete G-02 manifest is required."

report="${RUNNER_TEMP}/evidence-check-${MODULARYN_EVIDENCE_MODE}.json"
rm -f "${report}"
dotnet "${tests}" -noLogo -noColor -failSkips -method "Modularyn.Repository.Tests.CiEvidenceTests.The_records_this_CI_run_wrote_validate_against_the_repository_contracts" -result-ctrf "${report}"
[ "$(jq -r '.results.summary.tests' "${report}")" = "1" ] && [ "$(jq -r '.results.summary.passed' "${report}")" = "1" ] \
  || fail "The evidence check must run once and pass."

echo "The ${MODULARYN_EVIDENCE_MODE} evidence records at ${MODULARYN_SOURCE_COMMIT} and their SHA-256:"
(cd "${MODULARYN_EVIDENCE_DIR}" && find . -name record.json -print0 | LC_ALL=C sort -z | xargs -0 sha256sum)
cat "${MODULARYN_EVIDENCE_DIR}/manifest.json"
