#!/usr/bin/env bash
# Runs the tests with the trait Requires=PostgreSQL in every test project, one project at a time, and fails
# unless at least one such test ran and every one that ran passed (docs/ci.md, "Real-provider tests").
#
# dotnet test exits successfully when its filter selects no test, so this script reads each project's TRX
# results instead. Each project writes its own named results file, so no project's counts can be lost, and the
# projects run one after another against the shared test database. It expects a Release build and, like any run
# of these tests, MODULARYN_TEST_POSTGRESQL; postgresql-test-database.sh provides it in CI and locally.
set -euo pipefail

fail() {
  if [ "${GITHUB_ACTIONS:-}" = "true" ]; then
    echo "::error::$*"
  else
    echo "error: $*" >&2
  fi
  exit 1
}

[ "$#" -eq 0 ] || fail "Usage: $0"
if [ -n "${RUNNER_TEMP:-}" ]; then
  results="${RUNNER_TEMP}/real-provider-results"
else
  results="$(mktemp -d)/real-provider-results"
fi
rm -rf "${results}"
mkdir -p "${results}"

projects=()
while IFS= read -r -d '' project; do
  projects+=("${project}")
done < <(git ls-files -z -- 'tests/*/*.Tests.csproj')
[ "${#projects[@]}" -gt 0 ] || fail "No test project found under tests/."

total=0
passed=0
for project in "${projects[@]}"; do
  name="$(basename "${project}" .csproj)"
  dotnet test "${project}" -c Release --no-build --results-directory "${results}" --logger "trx;LogFileName=${name}.trx" \
    --logger "console;verbosity=detailed" --filter "Requires=PostgreSQL"
  trx="${results}/${name}.trx"
  [ -f "${trx}" ] || fail "${project} wrote no test results to ${trx}."
  counters="$(grep -o '<Counters [^>]*>' "${trx}")" || fail "${trx} has no result counters."
  [ "$(printf '%s\n' "${counters}" | wc -l | tr -d ' ')" = "1" ] || fail "${trx} must have exactly one set of result counters."
  project_total="$(printf '%s\n' "${counters}" | sed -E 's/.* total="([0-9]+)".*/\1/')"
  project_passed="$(printf '%s\n' "${counters}" | sed -E 's/.* passed="([0-9]+)".*/\1/')"
  [[ "${project_total}" =~ ^[0-9]+$ && "${project_passed}" =~ ^[0-9]+$ ]] || fail "${trx} has unreadable result counters."
  echo "${project}: ${project_passed} of ${project_total} PostgreSQL tests passed"
  total=$((total + project_total))
  passed=$((passed + project_passed))
done

[ "${passed}" -eq "${total}" ] || fail "Only ${passed} of ${total} PostgreSQL tests passed; none may fail or be skipped."
[ "${passed}" -ge 1 ] || fail "No test with the trait Requires=PostgreSQL ran, so nothing exercised PostgreSQL."
echo "${passed} PostgreSQL tests passed across ${#projects[@]} test projects."
