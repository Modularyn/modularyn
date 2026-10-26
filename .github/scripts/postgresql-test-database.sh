#!/usr/bin/env bash
# Creates the per-run PostgreSQL test database that MODULARYN_TEST_POSTGRESQL names, the same way in
# CI and in local runs (docs/ci.md, "Real-provider tests").
#
#   ci COMMAND...   The real-provider job: checks the PostgreSQL service container, creates the test
#                   role and database in it, opens host connections for that role only, and runs
#                   COMMAND with MODULARYN_TEST_POSTGRESQL set.
#   run COMMAND...  A local run: creates the test role and database on the local PostgreSQL 18 server,
#                   runs COMMAND with MODULARYN_TEST_POSTGRESQL set, then drops them.
#
# Each run generates its own role name, database name and password. The password is masked in CI and
# never printed, and the statements that carry it are kept out of the server log. The connection string
# exists only in COMMAND's environment: neither mode writes it to a file or to a later step.
set -euo pipefail

variable="MODULARYN_TEST_POSTGRESQL"

fail() {
  if [ "${GITHUB_ACTIONS:-}" = "true" ]; then
    echo "::error::$*"
  else
    echo "error: $*" >&2
  fi
  exit 1
}

random_hex() {
  local bytes="$1" value
  value="$(od -An -N"${bytes}" -tx1 /dev/urandom | tr -d ' \n')" || fail "Cannot read random bytes."
  [[ "${value}" =~ ^[0-9a-f]+$ ]] && [ "${#value}" -eq $((bytes * 2)) ] || fail "Cannot read random bytes."
  printf '%s' "${value}"
}

# The SQL that creates the role and its database. Names and password are lowercase hexadecimal with a
# fixed prefix, so they need no quoting beyond what is shown.
create_sql() {
  local name="$1" password="$2"
  cat <<SQL
SET log_statement = 'none';
SET log_min_error_statement = 'panic';
CREATE ROLE "${name}" LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS PASSWORD '${password}';
CREATE DATABASE "${name}" OWNER "${name}";
REVOKE ALL ON DATABASE "${name}" FROM PUBLIC;
SQL
}

drop_sql() {
  local name="$1"
  cat <<SQL
DROP DATABASE IF EXISTS "${name}" WITH (FORCE);
DROP ROLE IF EXISTS "${name}";
SQL
}

ci() {
  [ "$#" -gt 0 ] || fail "Usage: $0 ci COMMAND [ARGUMENT...]"
  [ "${GITHUB_ACTIONS:-}" = "true" ] || fail "The ci mode runs only in GitHub Actions."
  [ "${RUNNER_OS:-}/${RUNNER_ARCH:-}" = "Linux/X64" ] \
    || fail "The real-provider job runs on Linux x64, not ${RUNNER_OS:-unknown} ${RUNNER_ARCH:-unknown}."
  [[ "${POSTGRES_CONTAINER:-}" =~ ^[0-9a-f]{12,64}$ ]] || fail "POSTGRES_CONTAINER must be the service container id."
  [[ "${POSTGRES_PORT:-}" =~ ^[1-9][0-9]{0,4}$ ]] || fail "POSTGRES_PORT must be the service's host port."
  [[ "${POSTGRES_IMAGE}" == docker.io/library/postgres:* && "${POSTGRES_IMAGE}" =~ ^[^@]+:[A-Za-z0-9._-]+@(sha256:[0-9a-f]{64})$ ]] || fail "The PostgreSQL service image must be named by tag and index digest."
  local index_digest="${BASH_REMATCH[1]}"
  [[ "${POSTGRESQL_VERSION:-}" =~ ^[0-9]+\.[0-9]+$ ]] || fail "POSTGRESQL_VERSION must be an exact server release."

  # The service must run the pinned image, pulled by its index digest, on linux/amd64.
  local configured image_id platform repo_digests
  configured="$(docker inspect --format '{{.Config.Image}}' "${POSTGRES_CONTAINER}")" || fail "Cannot inspect the service."
  [ "${configured}" = "${POSTGRES_IMAGE}" ] || fail "The service runs ${configured}, not ${POSTGRES_IMAGE}."
  image_id="$(docker inspect --format '{{.Image}}' "${POSTGRES_CONTAINER}")" || fail "Cannot inspect the service."
  platform="$(docker image inspect --format '{{.Os}}/{{.Architecture}}' "${image_id}")" || fail "Cannot inspect ${image_id}."
  [ "${platform}" = "linux/amd64" ] || fail "The service image is ${platform}, not linux/amd64."
  repo_digests="$(docker image inspect --format '{{join .RepoDigests " "}}' "${image_id}")" || fail "Cannot inspect ${image_id}."
  [[ " ${repo_digests} " == *"@${index_digest} "* ]] \
    || fail "The service image was not pulled by the index digest ${index_digest}; it records ${repo_digests}."
  echo "Service ${POSTGRES_CONTAINER} runs ${POSTGRES_IMAGE}, image ${image_id}, ${platform}"

  # Inside the container, the postgres OS user reaches the server over its Unix socket; the second array signs in
  # over TCP. ToolPinContract allows each line that names these arrays, and only those lines.
  local admin=(docker exec -i --user postgres "${POSTGRES_CONTAINER}" psql --no-psqlrc --quiet --tuples-only --no-align --set=ON_ERROR_STOP=1 --dbname=postgres)
  local tcp=(docker exec -i -e PGPASSWORD --user postgres "${POSTGRES_CONTAINER}" psql --no-psqlrc --no-password --quiet --tuples-only --no-align --set=ON_ERROR_STOP=1)
  local version
  version="$("${admin[@]}" --command='SHOW server_version' < /dev/null)" || fail "Cannot query the service's server version."
  [[ "${version}" == "${POSTGRESQL_VERSION}" || "${version}" == "${POSTGRESQL_VERSION} "* ]] \
    || fail "The service reports PostgreSQL ${version}, not ${POSTGRESQL_VERSION}."
  echo "Service reports PostgreSQL ${version}"

  local name password hba_file
  name="modularyn_test_$(random_hex 8)"
  password="$(random_hex 32)"
  echo "::add-mask::${password}"
  create_sql "${name}" "${password}" | "${admin[@]}" || fail "Cannot create the test role and database."
  "${admin[@]}" --command='ALTER ROLE postgres PASSWORD NULL' < /dev/null > /dev/null || fail "Cannot remove the bootstrap superuser password."

  # Replace the image's rules: the socket stays for the postgres OS user, and host connections are
  # accepted only for the test role, on its database, with SCRAM-SHA-256.
  hba_file="$("${admin[@]}" --command='SHOW hba_file' < /dev/null)" || fail "Cannot read the hba_file setting."
  [[ "${hba_file}" =~ ^/[A-Za-z0-9/._-]+$ ]] || fail "Unexpected hba_file ${hba_file}."
  local hba_rules
  hba_rules="$(printf '%s\n' "# Written by the real-provider job. Only the test role may connect over TCP, with SCRAM-SHA-256." \
    "local all postgres peer" "host ${name} ${name} all scram-sha-256")"
  printf '%s\n' "${hba_rules}" | docker exec -i --user postgres "${POSTGRES_CONTAINER}" sh -c 'cat > "$1"' sh "${hba_file}" || fail "Cannot write ${hba_file}."
  local rules
  rules="$("${admin[@]}" --command="SELECT count(*) FILTER (WHERE error IS NOT NULL) || ' ' || count(*) FROM pg_hba_file_rules" < /dev/null)" || fail "Cannot read pg_hba_file_rules."
  [ "${rules}" = "0 2" ] || fail "pg_hba_file_rules reports '${rules}' (errors, rules), not '0 2'."
  local listing="SELECT 'pg_hba rule ' || rule_number || ': ' || type || ' ' || array_to_string(database, ',') || ' '
    || array_to_string(user_name, ',') || ' ' || coalesce(address, 'socket') || ' ' || auth_method FROM pg_hba_file_rules
    ORDER BY rule_number"
  "${admin[@]}" --command="${listing}" < /dev/null || fail "Cannot read pg_hba_file_rules."
  "${admin[@]}" --command='SELECT pg_reload_conf()' < /dev/null > /dev/null || fail "Cannot reload the server configuration."

  # The test role signs in over TCP with its password, and the superuser cannot sign in over TCP.
  local signed_in="" attempt
  for attempt in 1 2 3 4 5 6 7 8 9 10; do
    if PGPASSWORD="${password}" "${tcp[@]}" "host=127.0.0.1 port=5432 dbname=${name} user=${name}" --command='SELECT current_user' < /dev/null > /dev/null 2>&1; then
      signed_in="yes"
      break
    fi
    sleep 1
  done
  [ -n "${signed_in}" ] || fail "The test role cannot sign in over TCP after ${attempt} attempts."
  if "${tcp[@]}" "host=127.0.0.1 port=5432 dbname=postgres user=postgres" --command='SELECT 1' < /dev/null > /dev/null 2>&1; then
    fail "The superuser can still sign in over TCP."
  fi

  echo "Created role and database ${name}; ${variable} names them on 127.0.0.1:${POSTGRES_PORT} for this command only."
  export MODULARYN_TEST_POSTGRESQL="Host=127.0.0.1;Port=${POSTGRES_PORT};Database=${name};Username=${name};Password=${password}"
  local status
  set +e
  "$@"
  status=$?
  set -e
  return "${status}"
}

# Globals, because the EXIT trap that drops the role and database runs after run() has returned.
local_admin=(psql --no-psqlrc --quiet --tuples-only --no-align --set=ON_ERROR_STOP=1 --dbname=postgres)
local_name=""

drop_local() {
  [ -n "${local_name}" ] || return 0
  drop_sql "${local_name}" | "${local_admin[@]}" > /dev/null \
    || echo "error: cannot drop the role and database ${local_name}." >&2
}

run() {
  [ "$#" -gt 0 ] || fail "Usage: $0 run COMMAND [ARGUMENT...]"
  command -v psql > /dev/null || fail "psql is required; install PostgreSQL 18 as docs/ci.md describes."
  # The local superuser connection follows libpq's defaults and PGHOST, PGPORT and PGUSER.
  local version_number trust
  version_number="$("${local_admin[@]}" --command='SHOW server_version_num' < /dev/null)" \
    || fail "Cannot reach the local PostgreSQL server as a superuser."
  [[ "${version_number}" =~ ^18[0-9]{4}$ ]] \
    || fail "The local server reports server_version_num ${version_number}; real-provider tests need PostgreSQL 18."
  trust="$("${local_admin[@]}" --command="SELECT count(*) FROM pg_hba_file_rules WHERE type LIKE 'host%' AND auth_method = 'trust'" < /dev/null)" \
    || fail "Cannot read pg_hba_file_rules; the local connection must be a superuser."
  if [ "${trust}" != "0" ]; then
    echo "note: the local server accepts host connections without a password (trust), so it does not check the generated password." >&2
  fi

  local password status
  password="$(random_hex 32)"
  local_name="modularyn_test_$(random_hex 8)"
  # The drop runs on every exit, including a failed or interrupted COMMAND.
  trap drop_local EXIT
  create_sql "${local_name}" "${password}" | "${local_admin[@]}" || fail "Cannot create the test role and database."
  echo "Created the role and database ${local_name} for this run; they are dropped when it ends." >&2
  export MODULARYN_TEST_POSTGRESQL="Host=localhost;Port=${PGPORT:-5432};Database=${local_name};Username=${local_name};Password=${password}"
  set +e
  "$@"
  status=$?
  set -e
  return "${status}"
}

case "${1:-}" in
  ci) shift; ci "$@" ;;
  run) shift; run "$@" ;;
  *) fail "Usage: $0 ci COMMAND [ARGUMENT...] | $0 run COMMAND [ARGUMENT...]" ;;
esac
