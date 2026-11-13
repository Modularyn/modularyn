#!/usr/bin/env bash
# The container-build job's AC-001 container rows (docs/ci.md, "Container acceptance"). postgresql-test-database.sh runs this
# script with MODULARYN_TEST_POSTGRESQL set for the test role it created in the database container, naming that container's
# loopback port. In order, the script:
#
#   1. writes the published sample image's connection string, the same role reached by the database container's name on
#      the internal network, to a secret file that only the runner's group can read, and removes it when it ends;
#   2. applies the sample's migrations with the image's --migrate argument, then starts the image on the internal network
#      with its settings file, that secret mounted read-only, and owner-only key and BLOB directories in memory;
#   3. waits until the published-sample readiness test, run in the composition fixture image, passes;
#   4. runs the composition fixture image in the database container's network namespace, so the tests reach the database
#      on loopback as their probe requires, with an output directory mounted for the report and the evidence records,
#      selecting exactly the four AC-001 container rows and the published sample image's readiness and sign-in page tests
#      through the test executable's own filters, within a bound that fails the step, showing its failures and summary through
#      the shared filter, as every other test run does;
#      and
#   5. fails unless the report shows exactly those six tests and every one passed: none skipped, none missing.
#
# Every container here runs with no capability, no privilege escalation and a read-only root filesystem. The sample image
# trusts forwarded headers only from the address its settings declare, that of the database container's namespace, in
# which the composition fixture image acts as the sample's TLS proxy.
set -euo pipefail

fail() {
  echo "::error::$*"
  exit 1
}

[ "${GITHUB_ACTIONS:-}" = "true" ] || fail "This script runs only in GitHub Actions, as the container-build job's command."
[[ "${POSTGRES_CONTAINER:-}" =~ ^[0-9a-f]{12,64}$ ]] || fail "POSTGRES_CONTAINER must be the database container's id."
[[ "${MODULARYN_SOURCE_COMMIT:-}" =~ ^[0-9a-f]{40}$ ]] || fail "MODULARYN_SOURCE_COMMIT must be the exact head."
loopback="Host=127.0.0.1;Port=5432;"
bound=300
[[ "${MODULARYN_TEST_POSTGRESQL:-}" == "${loopback}"* ]] || fail "MODULARYN_TEST_POSTGRESQL must name the database container's loopback port."

network=modularyn-acceptance
sample=modularyn-sample
secrets="${RUNNER_TEMP}/composition-secrets"
output="${RUNNER_TEMP}/composition-output"
report="${output}/composition-results.json"
group="$(id -g)"
export MODULARYN_SAMPLE_BASE_URL="http://${sample}:8080"

remove_secrets() {
  rm -rf "${secrets}"
}
trap remove_secrets EXIT
rm -rf "${secrets}" "${output}"
(umask 027 && mkdir "${secrets}" && printf 'Host=modularyn-postgres;Port=5432;%s' "${MODULARYN_TEST_POSTGRESQL#"${loopback}"}" > "${secrets}/database")
mkdir -m 0770 "${output}"
# The runner assembles records here after the fixture exits. Keep the root runner-owned and group-writable for both users.
mkdir -m 0770 "${output}/evidence"

proxy="$(docker inspect --format '{{range .NetworkSettings.Networks}}{{.IPAddress}}{{end}}' "${POSTGRES_CONTAINER}")"
[[ "${proxy}" =~ ^[0-9]{1,3}(\.[0-9]{1,3}){3}$ ]] || fail "Cannot read the database container's address on ${network}."
echo "The sample image declares ${proxy}, the database container's address, as its TLS proxy."

docker run --rm --pull=never --platform=linux/amd64 --network="${network}" --read-only --tmpfs=/tmp --tmpfs=/var/lib/modularyn/keys:uid=1654,gid=1654,mode=0700 --tmpfs=/var/lib/modularyn/blobs:uid=1654,gid=1654,mode=0700 --cap-drop=ALL --security-opt=no-new-privileges --group-add="${group}" --volume="${secrets}:/run/modularyn/secrets:ro" --env-file=.github/scripts/composition-sample.env --env=Modularyn__Ingress__Proxies__0="${proxy}" "${SAMPLE_IMAGE}" --migrate || fail "The sample image did not apply its migrations."
docker run --detach --pull=never --platform=linux/amd64 --name="${sample}" --network="${network}" --read-only --tmpfs=/tmp --tmpfs=/var/lib/modularyn/keys:uid=1654,gid=1654,mode=0700 --tmpfs=/var/lib/modularyn/blobs:uid=1654,gid=1654,mode=0700 --cap-drop=ALL --security-opt=no-new-privileges --group-add="${group}" --volume="${secrets}:/run/modularyn/secrets:ro" --env-file=.github/scripts/composition-sample.env --env=Modularyn__Ingress__Proxies__0="${proxy}" "${SAMPLE_IMAGE}" > /dev/null || fail "The sample image did not start."

ready=""
for attempt in $(seq 1 30); do
  if docker run --rm --pull=never --platform=linux/amd64 --network="container:${POSTGRES_CONTAINER}" --read-only --tmpfs=/tmp --cap-drop=ALL --security-opt=no-new-privileges --env=MODULARYN_SAMPLE_BASE_URL "${COMPOSITION_IMAGE}" -noLogo -noColor -failSkips -method "Modularyn.Tooling.Samples.TeamWorkspace.Tests.CompositionAcceptanceTests.The_published_sample_image_is_ready_over_HTTP" > /dev/null 2>&1; then
    ready="yes"
    break
  fi
  [ "$(docker inspect --format '{{.State.Running}}' "${sample}")" = "true" ] || break
  sleep 2
done
if [ -z "${ready}" ]; then
  docker logs --tail=50 "${sample}" 2>&1 || true
  fail "The published sample image did not become ready."
fi
echo "The published sample image is ready after ${attempt} checks."

digest="$(docker image inspect --format '{{.Id}}' "${COMPOSITION_IMAGE}")"
[[ "${digest}" =~ ^sha256:[0-9a-f]{64}$ ]] || fail "Cannot measure the composition fixture image's digest."
echo "The container rows run in the composition fixture image ${digest}."
export MODULARYN_CONTAINER_IMAGE_DIGEST="${digest}"
bash .github/scripts/g02-container-results.sh prepare
status=0
timeout --kill-after=30s "${bound}" docker run --rm --pull=never --platform=linux/amd64 --name=g02-composition --network="container:${POSTGRES_CONTAINER}" --read-only --tmpfs=/tmp --cap-drop=ALL --security-opt=no-new-privileges --group-add="${group}" --volume="${output}:/output" --env=MODULARYN_TEST_POSTGRESQL --env=MODULARYN_SAMPLE_BASE_URL --env=MODULARYN_SOURCE_COMMIT --env=MODULARYN_EVIDENCE_RUN_ID --env=MODULARYN_CONTAINER_IMAGE_DIGEST="${digest}" --env=MODULARYN_EVIDENCE_DIR=/output/evidence "${COMPOSITION_IMAGE}" -noLogo -noColor -longRunning 60 -preEnumerateTheories -trait "Deployment=single-host-container" -method "Modularyn.Tooling.Samples.TeamWorkspace.Tests.CompositionAcceptanceTests.AC_001_B01_CompatibilityEdgesHonorTheDeclaredRange_Integration" -method "Modularyn.Tooling.Samples.TeamWorkspace.Tests.CompositionAcceptanceTests.AC_001_N01_InvalidGraphsRefuseActivationWithDiagnostics_Integration" -method "Modularyn.Tooling.Samples.TeamWorkspace.Tests.CompositionAcceptanceTests.AC_001_P01_DependenciesInitializeOnceAndStopInReverseOrder_Integration" -method "Modularyn.Tooling.Samples.TeamWorkspace.Tests.CompositionAcceptanceTests.AC_001_R01_FailedInitializationCleansUpAndRecovers_Integration" -method "Modularyn.Tooling.Samples.TeamWorkspace.Tests.CompositionAcceptanceTests.The_published_sample_image_is_ready_over_HTTP" -method "Modularyn.Tooling.Samples.TeamWorkspace.Tests.CompositionAcceptanceTests.The_published_sample_image_serves_its_sign_in_page_over_HTTP" -result-ctrf /output/composition-results.json 2>&1 | awk -f .github/scripts/g02-project-output.awk || status=$?
if [ "${status}" -eq 124 ] || [ "${status}" -eq 137 ]; then
  docker kill g02-composition > /dev/null 2>&1 || true
  fail "The G-02 container run g02-composition did not finish within ${bound} seconds."
fi

check_composition_results() {
[ -f "${report}" ] || fail "The composition fixture image wrote no report."
jq -r '.results.tests[] | "\(.status) \(.name)"' "${report}"
expected="$(printf '%s\n' \
  'Modularyn.Tooling.Samples.TeamWorkspace.Tests.CompositionAcceptanceTests.AC_001_B01_CompatibilityEdgesHonorTheDeclaredRange_Integration(mode: "single-host-container")' \
  'Modularyn.Tooling.Samples.TeamWorkspace.Tests.CompositionAcceptanceTests.AC_001_N01_InvalidGraphsRefuseActivationWithDiagnostics_Integration(mode: "single-host-container")' \
  'Modularyn.Tooling.Samples.TeamWorkspace.Tests.CompositionAcceptanceTests.AC_001_P01_DependenciesInitializeOnceAndStopInReverseOrder_Integration(mode: "single-host-container")' \
  'Modularyn.Tooling.Samples.TeamWorkspace.Tests.CompositionAcceptanceTests.AC_001_R01_FailedInitializationCleansUpAndRecovers_Integration(mode: "single-host-container")' \
  'Modularyn.Tooling.Samples.TeamWorkspace.Tests.CompositionAcceptanceTests.The_published_sample_image_is_ready_over_HTTP' \
  'Modularyn.Tooling.Samples.TeamWorkspace.Tests.CompositionAcceptanceTests.The_published_sample_image_serves_its_sign_in_page_over_HTTP' \
  | LC_ALL=C sort)"
passed="$(jq -r '.results.tests[] | select(.status == "passed") | .name' "${report}" | LC_ALL=C sort)"
[ "$(jq -r '.results.summary.tests' "${report}")" = "6" ] && [ "${passed}" = "${expected}" ] \
  || fail "The four AC-001 container rows and the published sample image's readiness and sign-in page tests must all pass, with none skipped and none missing."
[ "${status}" -eq 0 ] || fail "The composition fixture image's tests exited with status ${status}."
echo "The four AC-001 container rows and the published sample image's readiness and sign-in page tests passed."

}
(check_composition_results) || status=1
bash .github/scripts/g02-container-results.sh complete || status=1
exit "${status}"
