#!/usr/bin/env bash
# Collect xunit v3's VSTest reports, then execute the planned methods absent from that provider selection on this same host.
set -euo pipefail
fail() { echo "::error::G-02 host result collection failed."; exit 1; }
[ "${GITHUB_ACTIONS:-}" = true ] || fail
[ -d "${MODULARYN_EVIDENCE_DIR:-}" ] || mkdir -p "${MODULARYN_EVIDENCE_DIR}"
collector=tests/Modularyn.Repository.Tests/evidence_bundle.py
sample=tests/Modularyn.Tooling.Samples.TeamWorkspace.Tests/bin/Release/net10.0/Modularyn.Tooling.Samples.TeamWorkspace.Tests.dll
arguments=(--directory "${MODULARYN_EVIDENCE_DIR}" --commit "${MODULARYN_SOURCE_COMMIT}" --run "${MODULARYN_EVIDENCE_RUN_ID}" --mode single-host)
remove_raw() {
  rm -f "${MODULARYN_EVIDENCE_DIR}/"*.raw.json "${MODULARYN_EVIDENCE_DIR}/"*.methods
  if [ "${1:-}" = complete ]; then rm -f "${RUNNER_TEMP}/real-provider-results/"*.trx; fi
}
trap 'remove_raw "$1"' EXIT
if [ "$1" = prepare ]; then
  MODULARYN_EVIDENCE_MEASURE=1 MODULARYN_EVIDENCE_BINARIES="${GITHUB_WORKSPACE}/tests" dotnet "${sample}" -noLogo -noColor -failSkips -method Modularyn.Tooling.Samples.TeamWorkspace.Tests.EvidenceMeasurementTests.Measure_the_evidence_run_before_collecting_results > /dev/null 2>&1 || fail
  exit 0
fi
[ "$1" = complete ] || fail
status=0
while IFS= read -r project; do
  name="$(basename "${project}" .csproj)"
  report="${RUNNER_TEMP}/real-provider-results/${name}.trx"
  if [ -f "${report}" ]; then
    python3 -B "${collector}" collect "${arguments[@]}" --project "${project}" --report "${report}" --report-format trx --label "${name}-provider" || fail
  fi
  methods="${MODULARYN_EVIDENCE_DIR}/${name}.methods"
  python3 -B "${collector}" select "${arguments[@]}" --project "${project}" > "${methods}" || fail
  selected=()
  while IFS= read -r method; do selected+=(-method "${method}"); done < "${methods}"
  if [ "${#selected[@]}" -gt 0 ]; then
    report="${MODULARYN_EVIDENCE_DIR}/${name}.raw.json"
    dotnet "$(dirname "${project}")/bin/Release/net10.0/${name}.dll" -noLogo -noColor -preEnumerateTheories -failSkips "${selected[@]}" -result-ctrf "${report}" > /dev/null 2>&1 || status=1
    if [ -f "${report}" ]; then
      python3 -B "${collector}" collect "${arguments[@]}" --project "${project}" --report "${report}" --label "${name}-remaining" --methods "${methods}" || fail
    else
      status=1
    fi
  fi
  rm -f "${methods}"
done < <(jq -r '[.cases[].plannedTests[].project] | unique[]' docs/evidence/g02-plan.json)
exit "${status}"
