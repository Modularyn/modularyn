#!/usr/bin/env bash
# Runs the tests with the trait Requires=PostgreSQL in every test project, one project at a time, and fails
# unless at least one such test ran and every one that ran passed (docs/ci.md, "Real-provider tests").
#
#   (no arguments)          Runs the tests, then counts their results as count does.
#   count DIRECTORY NAME... Counts the results in DIRECTORY/NAME.trx for each NAME, without running anything;
#                           a repository test runs it against fixture results.
#
# dotnet test exits successfully when its filter selects no test, so this script reads each project's TRX
# results instead. Each project writes its own named results file, so no project's counts can be lost, and the
# projects run one after another against the shared test database. Running the tests expects a Release build
# and, like any run of them, MODULARYN_TEST_POSTGRESQL; postgresql-test-database.sh provides it.
set -euo pipefail

fail() {
  if [ "${GITHUB_ACTIONS:-}" = "true" ]; then
    echo "::error::$*"
  else
    echo "error: $*" >&2
  fi
  exit 1
}

# Sums the counters of DIRECTORY/NAME.trx for each NAME and fails unless every test passed and at least one ran.
count_results() {
  local results="$1"
  shift
  local projects="$#" total=0 passed=0 name trx counters project_total project_passed
  [ "${projects}" -gt 0 ] || fail "No test project to count."
  for name in "$@"; do
    trx="${results}/${name}.trx"
    [ -f "${trx}" ] || fail "${name} wrote no test results to ${trx}."
    counters="$(grep -o '<Counters [^>]*>' "${trx}")" || fail "${trx} has no result counters."
    [ "$(printf '%s\n' "${counters}" | wc -l | tr -d ' ')" = "1" ] || fail "${trx} must have exactly one set of result counters."
    project_total="$(printf '%s\n' "${counters}" | sed -E 's/.* total="([0-9]+)".*/\1/')"
    project_passed="$(printf '%s\n' "${counters}" | sed -E 's/.* passed="([0-9]+)".*/\1/')"
    [[ "${project_total}" =~ ^[0-9]+$ && "${project_passed}" =~ ^[0-9]+$ ]] || fail "${trx} has unreadable result counters."
    echo "${name}: ${project_passed} of ${project_total} PostgreSQL tests passed"
    total=$((total + project_total))
    passed=$((passed + project_passed))
  done

  [ "${passed}" -eq "${total}" ] || fail "Only ${passed} of ${total} PostgreSQL tests passed; none may fail or be skipped."
  [ "${passed}" -ge 1 ] || fail "No test with the trait Requires=PostgreSQL ran, so nothing exercised PostgreSQL."
  echo "${passed} PostgreSQL tests passed across ${projects} test projects."
}

# A local run keeps its results only until the script ends; CI keeps them in the runner's temporary directory.
local_results=""
remove_local_results() {
  [ -z "${local_results}" ] || rm -rf "${local_results}"
}

run_tests() {
  local results
  if [ -n "${RUNNER_TEMP:-}" ]; then
    results="${RUNNER_TEMP}/real-provider-results"
  else
    local_results="$(mktemp -d)"
    trap remove_local_results EXIT
    results="${local_results}/real-provider-results"
  fi
  rm -rf "${results}"
  mkdir -p "${results}"

  local projects=() names=() project name
  while IFS= read -r -d '' project; do
    projects+=("${project}")
  done < <(git ls-files -z -- 'tests/*/*.Tests.csproj')
  [ "${#projects[@]}" -gt 0 ] || fail "No test project found under tests/."

  for project in "${projects[@]}"; do
    name="$(basename "${project}" .csproj)"
    names+=("${name}")
    dotnet test "${project}" -c Release --no-build --results-directory "${results}" --logger "trx;LogFileName=${name}.trx" \
      --logger "console;verbosity=detailed" --filter "Requires=PostgreSQL"
  done

  count_results "${results}" "${names[@]}"
}

case "${1:-}" in
  "") run_tests ;;
  count)
    shift
    [ "$#" -ge 2 ] || fail "Usage: $0 count DIRECTORY NAME..."
    [ -d "$1" ] || fail "$1 is not a directory."
    count_results "$@"
    ;;
  *) fail "Usage: $0 | $0 count DIRECTORY NAME..." ;;
esac
