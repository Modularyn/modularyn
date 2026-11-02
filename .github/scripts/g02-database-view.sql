-- The test database's view of a container run near its bound (docs/ci.md, "Container acceptance"): g02-container-results.sh
-- runs this through the database container's own psql as its postgres OS user, with the test database's name in the variable
-- "database". Only these fields print. A query is cut to 60 characters, with every literal replaced by the placeholder x, so no
-- password, connection string or other value appears.
\pset footer off
SELECT pid, application_name, state, wait_event_type, wait_event,
  round(extract(epoch FROM now() - xact_start))::int AS xact_age_s,
  round(extract(epoch FROM now() - query_start))::int AS query_age_s,
  left(regexp_replace(regexp_replace(regexp_replace(regexp_replace(query, '\$[A-Za-z_]*\$.*$', 'x'),
    '''([^'']|'''')*''', 'x', 'g'), '''.*$', 'x'), '\m[0-9]+(\.[0-9]+)?\M', 'x', 'g'), 60) AS query
FROM pg_stat_activity
WHERE datname = :'database'
ORDER BY pid;
SELECT locktype, objid, mode, granted, pid
FROM pg_locks
WHERE (locktype = 'advisory' OR NOT granted)
  AND (database IS NULL OR database = (SELECT oid FROM pg_database WHERE datname = :'database'))
ORDER BY pid, locktype, objid;
