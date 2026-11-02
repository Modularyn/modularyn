#!/usr/bin/env bash
# Additional planned projects run in the existing test-only fixture image, in the database container's namespace.
set -euo pipefail
fail() { echo "::error::G-02 container result collection failed."; exit 1; }
[ "${GITHUB_ACTIONS:-}" = true ] || fail
[[ "${POSTGRES_CONTAINER:-}" =~ ^[0-9a-f]{12,64}$ ]] || fail
[[ "${MODULARYN_SOURCE_COMMIT:-}" =~ ^[0-9a-f]{40}$ ]] || fail
[[ "${MODULARYN_CONTAINER_IMAGE_DIGEST:-}" =~ ^sha256:[0-9a-f]{64}$ ]] || fail
output="${RUNNER_TEMP}/composition-output"
evidence="${output}/evidence"
group="$(id -g)"
collector=tests/Modularyn.Repository.Tests/evidence_bundle.py
arguments=(--directory "${evidence}" --commit "${MODULARYN_SOURCE_COMMIT}" --run "${MODULARYN_EVIDENCE_RUN_ID}" --mode single-host-container)
remove_raw() { rm -f "${output}/"*.raw.json "${output}/"*.methods; }
trap remove_raw EXIT
if [ "$1" = prepare ]; then
  docker run --rm --pull=never --platform=linux/amd64 --network="container:${POSTGRES_CONTAINER}" --read-only --tmpfs=/tmp --cap-drop=ALL --security-opt=no-new-privileges --group-add="${group}" --volume="${output}:/output" --env=MODULARYN_TEST_POSTGRESQL --env=MODULARYN_SOURCE_COMMIT --env=MODULARYN_EVIDENCE_RUN_ID --env=MODULARYN_CONTAINER_IMAGE_DIGEST --env=MODULARYN_EVIDENCE_DIR=/output/evidence --env=MODULARYN_EVIDENCE_MEASURE=1 --env=MODULARYN_EVIDENCE_BINARIES=/app "${COMPOSITION_IMAGE}" -noLogo -noColor -failSkips -method Modularyn.Tooling.Samples.TeamWorkspace.Tests.EvidenceMeasurementTests.Measure_the_evidence_run_before_collecting_results > /dev/null 2>&1 || fail
  exit 0
fi
[ "$1" = complete ] || fail
status=0
sample=tests/Modularyn.Tooling.Samples.TeamWorkspace.Tests/Modularyn.Tooling.Samples.TeamWorkspace.Tests.csproj
if [ -f "${output}/composition-results.json" ]; then
  python3 -B "${collector}" collect "${arguments[@]}" --project "${sample}" --report "${output}/composition-results.json" --label composition || fail
fi
while IFS= read -r project; do
  [ "${project}" != "${sample}" ] || continue
  name="$(basename "${project}" .csproj)"
  methods="${output}/${name}.methods"
  python3 -B "${collector}" select "${arguments[@]}" --project "${project}" > "${methods}" || fail
  selected=()
  while IFS= read -r method; do selected+=(-method "${method}"); done < "${methods}"
  if [ "${#selected[@]}" -gt 0 ]; then
    report="${output}/${name}.raw.json"
    docker run --rm --pull=never --platform=linux/amd64 --network="container:${POSTGRES_CONTAINER}" --read-only --tmpfs=/tmp --cap-drop=ALL --security-opt=no-new-privileges --group-add="${group}" --volume="${output}:/output" --env=MODULARYN_TEST_POSTGRESQL --entrypoint=dotnet "${COMPOSITION_IMAGE}" "/app/projects/${name}/${name}.dll" -noLogo -noColor -preEnumerateTheories -failSkips "${selected[@]}" -result-ctrf "/output/${name}.raw.json" > /dev/null 2>&1 || status=1
    if [ -f "${report}" ]; then
      python3 -B "${collector}" collect "${arguments[@]}" --project "${project}" --report "${report}" --label "${name}" --methods "${methods}" || fail
    else
      status=1
    fi
  fi
  rm -f "${methods}"
done < <(jq -r '[.cases[].plannedTests[].project] | unique[]' docs/evidence/g02-plan.json)
exit "${status}"
