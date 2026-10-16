# Internal native recursive deletion increment

Worker commit `6cf637f12c43ebec9578b29c4872399e484e2ea7` is integrated locally as
`08a6c37734f4e959a2cc3b5d1c7c24c636d564c2`. This bounded increment follows the
accepted [non-destructive before-read gate](earliest-read-overlap.md) and implements
one candidate through the existing internal `PackageStoreExecutionService`.
The executor lives in `src/Nuplane/Store/Maintenance/PhysicalStoreDeletionExecutor.cs`
beside that owner-bound execution service; no duplicate Coordination executor is required.

## Behavior

Fresh full-catalog ownership, expected enrollment epoch and explicit retention produce
the current Eligible row. Prepared persists the immutable native baseline before the
quarantine move. Quarantined requires the entire baseline and completion marker.
Deleting is published before the first payload removal; files and then deepest empty
directories are removed using exact native identities and held parent handles.

Every mutation checks the live operation, exact current journal and positive absence
of the original install slot. Native observations must remain exact baseline subsets;
new entries, replacements, links and uncertain evidence refuse further removal.
Finalizing requires the exact empty, single-link completion marker as the sole child.
The marker is removed last, the empty quarantine root handle is closed, authority is
checked again and the exact root is removed through its retained package parent.

Fresh explicit operations can resume verified Quarantined, Deleting and Finalizing
states. `Deleted` requires verified root removal during that invocation;
`AlreadyAbsent` requires a Finalizing journal and positive absence before invocation.
Journals remain present; terminal-journal retirement is outside this increment.

## Current evidence

Focused attempt 004 on macOS passes **22/22**, zero failed or skipped. Root read the
actual TRX, and an independent reviewer verified all ten changed source/test hashes,
the result rows and their exact match to the 22 strict-manifest additions.
Earlier setup/compile/test failures are preserved in the external program evidence.

The tests cover nested marker-last deletion; five distinct fresh-owner resume
boundaries; already-absent reporting; four phase-publication fault boundaries;
changed retention, protection and inventory; unexpected descendants; linked baseline
files; cancellation; a native unlink that succeeds before throwing; journal mutation;
and original-slot replacement after an unlink or after closing the root handle.

The combined native-group manifest contains 126 unique required scenarios:
all previous 104 plus the exact 22 new executor rows. The first combined run passed
125 and failed one existing missing-publisher test: the shared test double had
accidentally acquired publisher capability. Commit `925decd` limits that capability
to the remove-then-throw fixture; independent source review accepted the correction.
The failed run remains preserved.

Root then built and ran the restored full Store suite at `925decd` on macOS:
**1,055 passed, zero failed, 65 platform skips, 1,120 total**. Direct TRX readback
finds each of the 126 required names exactly once and Passed, including both the
missing-publisher refusal and the remove-then-throw scenario. The full-suite skips
do not hide a skipped required row. This is local combined regression evidence;
hosted qualification on all native lanes remains pending.

A separate compiled causal control removed only the per-mutation original-slot
absence check. Its sole selected regression failed at the assertion that the later
payload file survives a replacement of the original logical slot. Root verified
the expected failure and exact restoration of the production source before the
passing full Store run. Logs, TRX files, source hashes and readback JSON are retained
under the external program evidence `deletion-executor/`; an expected control
failure is not a passing production test.

## Acceptance limits

The resume tests acquire fresh owners in the same process. They do not prove actual
child-process interruption/restart. Windows handle-sharing behavior and all five
native lanes remain unqualified for this increment. Public maintenance registration,
execution confirmation/results, enrollment/migration orchestration, process recovery,
final release and Foundation adoption remain required. T107/T111 stay unchecked.
Repeated bounded tree scans have not been measured for performance.
