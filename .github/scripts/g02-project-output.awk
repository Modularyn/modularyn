# The lines of a container project run that the log shows (docs/ci.md, "Container acceptance"): xunit's long-running-test
# lines, the live phase markers that AC-003-N02's integration row writes, each failed test's [FAIL] line with its message
# and stack trace but never its output, and the run's summary line, as the real-provider job's log shows failures. Every
# other line is discarded, since it can carry test arguments or output.
match($0, /\[Long Running Test\] '[^']*', Elapsed: [0-9:]+/) { print substr($0, RSTART, RLENGTH); fflush(); next }
match($0, /\[OUTPUT\] G02 phase: [a-z0-9-]+$/) { print substr($0, RSTART, RLENGTH); fflush(); next }
/ \[OUTPUT\] / || /\[D::/ { next }
/ \[FAIL\]$/ { print; failing = 1; fflush(); next }
/ \[(PASS|SKIP|NOT RUN)\]$/ || /^ *Output:$/ || /^  (Discovering|Discovered|Starting|Finished):/ || /^=== / { failing = 0; next }
/^ +[^ ].*  Total: [0-9]+, Errors: [0-9]+, Failed: [0-9]+, Skipped: [0-9]+, Not Run: [0-9]+, Time: / { print; failing = 0; fflush(); next }
failing { print; fflush() }
