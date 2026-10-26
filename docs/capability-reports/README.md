# G-02 capability-report skeleton

[g02-skeleton.json](g02-skeleton.json) is a REL-003 planning report. Its qualified
subset is empty, its source commit is null and every included action is planned
with no evidence records. No case is claimed as executed or passed.

The 41 action entries correspond to the [evidence plan](../evidence/g02-plan.json).
Each names its module, action, providers, UI surface, tenant placement, deployment
topologies and intended runtime version. Both Linux x64 host and container runs
need their own measured evidence. PostgreSQL and filesystem selection does not
qualify another provider. SDK and runtime pins describe intended execution, not
an observed deployment.

AC-001 covers the merged Modularity and Hosting packages. Its exact existing
unit methods belong to CODE-02; CODE-10 owns the planned real-provider methods
on CODE-09's `Modularyn.Tooling.Samples.TeamWorkspace` host and container. Test
ownership and existence are read from each entry in the evidence plan. Existing
source does not qualify an action or supply deployment evidence.

`uiDelivery` distinguishes the planned native Razor sign-in and form surface
from actions without a UI. It makes no canonical Admin or full CMS claim. A
future substitute route or companion surface must be named explicitly and must
not be counted as a native screen. Browser fixtures must retain their action
evidence as well as API and provider evidence.

`supportingCapabilities` keeps identity fixtures, trust and ingress, file
eligibility, audit, observability and the sample application visible even where
this corpus slice has no dedicated reference case. `unmetDependencies` lists
every security-family prerequisite and the cases that depend on it. Evidence
for another scope or tuple cannot satisfy those obligations. The complete
AC-002 and AC-012 reference families remain deferred to G-03, and AC-031 to
G-08; that deferral does not supply missing G-02 fixture evidence.

`unsupportedSelections` includes every selection and reason in the
[reference profile](../profiles/reference.md). Windows and macOS hosts are
additionally unqualified: build and unit-test results on those platforms do not
qualify their real-provider behavior. Repository checks compare the complete
reference-profile exclusion table, including reasons, so a selection cannot
disappear silently. Unimplemented and unsupported selections remain visible
when future evidence is collected.

Before a release report can describe a qualified action, its author must bind
it to the reviewed commit, exact module and platform versions, measured host or
container tuple and retained evidence for all required cases and dependencies.
Record limitations and residual gaps beside the action. Passing a format check
does not perform that review or authorize publication. The skeleton changes no
coverage-register state, profile definition or master-design decision.
