#!/usr/bin/env bash
# Additional planned projects run in the existing test-only fixture image, in the database container's namespace. The evidence
# assembler's commands run on the runner, on the pinned SDK, through evidence-bundle.sh.
#
# Every run is bounded and shown (docs/ci.md, "Container acceptance"): the script names each project and how many planned
# methods it runs, timeout stops a run that exceeds its bound, which fails the step naming the run, and of the runner's output
# only xunit's long-running-test lines appear, each a test name and its elapsed time. After a run, its report's planned method
# names and statuses are printed, and nothing else of the report.
set -euo pipefail
fail() { echo "::error::G-02 container result collection failed."; exit 1; }
[ "${GITHUB_ACTIONS:-}" = true ] || fail
[[ "${POSTGRES_CONTAINER:-}" =~ ^[0-9a-f]{12,64}$ ]] || fail
[[ "${MODULARYN_SOURCE_COMMIT:-}" =~ ^[0-9a-f]{40}$ ]] || fail
[[ "${MODULARYN_CONTAINER_IMAGE_DIGEST:-}" =~ ^sha256:[0-9a-f]{64}$ ]] || fail
output="${RUNNER_TEMP}/composition-output"
evidence="${output}/evidence"
group="$(id -g)"
bound=300
export MODULARYN_EVIDENCE_DIR="${evidence}" MODULARYN_EVIDENCE_MODE=single-host-container
remove_raw() { rm -f "${output}/"*.raw.json "${output}/"*.methods; }
trap remove_raw EXIT
# Of a run's output, only xunit's long-running-test lines, a test's name and its elapsed time, and the live phase markers that
# AC-003-N02's integration row writes, the fixed prefix and a phase name.
long_running() { grep --line-buffered -oE "\[Long Running Test\] '[^']*', Elapsed: [0-9:]+|\[OUTPUT\] G02 phase: [a-z0-9-]+$" || true; }
# timeout ends a run past its bound with 124, or 137 once it has to kill the docker client; the container is then stopped too.
bounded() {
  if [ "$2" -eq 124 ] || [ "$2" -eq 137 ]; then
    docker kill "$1" > /dev/null 2>&1 || true
    echo "::error::The G-02 container run $1 did not finish within ${bound} seconds."
    exit 1
  fi
}
if [ "$1" = prepare ]; then
  run=0
  timeout --kill-after=30s "${bound}" docker run --rm --pull=never --platform=linux/amd64 --name=g02-measurement --network="container:${POSTGRES_CONTAINER}" --read-only --tmpfs=/tmp --cap-drop=ALL --security-opt=no-new-privileges --group-add="${group}" --volume="${output}:/output" --env=MODULARYN_TEST_POSTGRESQL --env=MODULARYN_SOURCE_COMMIT --env=MODULARYN_EVIDENCE_RUN_ID --env=MODULARYN_CONTAINER_IMAGE_DIGEST --env=MODULARYN_EVIDENCE_DIR=/output/evidence --env=MODULARYN_EVIDENCE_MEASURE=1 --env=MODULARYN_EVIDENCE_BINARIES=/app "${COMPOSITION_IMAGE}" -noLogo -noColor -failSkips -longRunning 60 -method Modularyn.Tooling.Samples.TeamWorkspace.Tests.EvidenceMeasurementTests.Measure_the_evidence_run_before_collecting_results 2>&1 | long_running || run=$?
  bounded g02-measurement "${run}"
  [ "${run}" -eq 0 ] || fail
  exit 0
fi
[ "$1" = complete ] || fail
status=0
sample=tests/Modularyn.Tooling.Samples.TeamWorkspace.Tests/Modularyn.Tooling.Samples.TeamWorkspace.Tests.csproj
if [ -f "${output}/composition-results.json" ]; then
  MODULARYN_EVIDENCE_PROJECT="${sample}" MODULARYN_EVIDENCE_REPORT="${output}/composition-results.json" MODULARYN_EVIDENCE_LABEL=composition bash .github/scripts/evidence-bundle.sh collect || fail
fi
while IFS= read -r project; do
  [ "${project}" != "${sample}" ] || continue
  name="$(basename "${project}" .csproj)"
  methods="${output}/${name}.methods"
  rm -f "${methods}"
  MODULARYN_EVIDENCE_PROJECT="${project}" MODULARYN_EVIDENCE_METHODS="${methods}" bash .github/scripts/evidence-bundle.sh select || fail
  selected=()
  while IFS= read -r method; do selected+=(-method "${method}"); done < "${methods}"
  echo "G-02 container run ${name}: $(( ${#selected[@]} / 2 )) planned methods, within ${bound} seconds."
  if [ "${#selected[@]}" -gt 0 ]; then
    report="${output}/${name}.raw.json"
    run=0
    timeout --kill-after=30s "${bound}" docker run --rm --pull=never --platform=linux/amd64 --name="g02-${name}" --network="container:${POSTGRES_CONTAINER}" --read-only --tmpfs=/tmp --cap-drop=ALL --security-opt=no-new-privileges --group-add="${group}" --volume="${output}:/output" --env=MODULARYN_TEST_POSTGRESQL --entrypoint=dotnet "${COMPOSITION_IMAGE}" "/app/projects/${name}/${name}.dll" -noLogo -noColor -preEnumerateTheories -failSkips -longRunning 60 -showLiveOutput "${selected[@]}" -result-ctrf "/output/${name}.raw.json" 2>&1 | long_running || run=$?
    bounded "g02-${name}" "${run}"
    [ "${run}" -eq 0 ] || status=1
    if [ -f "${report}" ]; then
      # The planned method names and statuses only, before collection removes the raw report.
      jq -r --rawfile planned "${methods}" '($planned | split("\n")) as $names | .results.tests[] | ((.extra.type // "") + "." + (.extra.method // "")) as $name
        | select($names | index($name)) | "\(.status) \($name)"' "${report}"
      MODULARYN_EVIDENCE_PROJECT="${project}" MODULARYN_EVIDENCE_REPORT="${report}" MODULARYN_EVIDENCE_LABEL="${name}" MODULARYN_EVIDENCE_METHODS="${methods}" bash .github/scripts/evidence-bundle.sh collect || fail
    else
      status=1
    fi
  fi
  rm -f "${methods}"
done < <(jq -r '[.cases[].plannedTests[].project] | unique[]' docs/evidence/g02-plan.json)
exit "${status}"
