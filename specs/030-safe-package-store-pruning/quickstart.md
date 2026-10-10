# Validation guide

**Planning artifact:** the product suites and manual APIs below must be implemented before these scenarios can pass. No command in this guide is recorded as executed product acceptance. The existing artifact-only native probes described in [research.md](research.md) are separate design evidence.

Use unique owned temporary stores and state directories for every run. Never point the harness at an existing user/customer store. All local heavy commands use the machine's normal build-slot wrapper; do not bypass a queued slot. Build only affected projects until the final cross-cutting gate.

## Prerequisites and enrollment

Use .NET 10 for tests; source builds must retain net8/net9/net10. Prepare actual local package feeds with v1/v2 roots, a shared dependency, a historical-only dependency, a support package, and lazy managed/native resolution fixtures. Configure two real Nuplane compositions with the same physical install root and different state slots outside it. Use additional child processes for crash/lock scenarios. Keep all users stopped during enrollment and supply the complete state list.

Preview enrollment, then explicitly enroll under the quiescent confirmation. Verify Incomplete appears before member writes and Complete only after every active/LKG snapshot is known and verified. Resume upgraded clients with optional Admin absent or pruning disabled locally and prove admission still applies. Legacy unknown LKG must refuse completion until verified migration or explicit retirement; ordinary access cannot erase it.

## Non-destructive boundary gate first

Run the focused store/runtime/loading suites after implementation:

```bash
dotnet test test/Nuplane.Store.Tests/Nuplane.Store.Tests.csproj --configuration Release
dotnet test test/Nuplane.Runtime.Tests/Nuplane.Runtime.Tests.csproj --configuration Release
dotnet test test/Nuplane.Loading.Tests/Nuplane.Loading.Tests.csproj --configuration Release
```

Synchronize at the real earliest resolver/metadata read in composition A. Let B with its distinct state request maintenance. Assert A already holds root authority, B cannot see an unprotected window, and the selected complete graph lease is published before retained metadata/loading. Release A's short owner while retaining its actual context and prove B still protects the graph. Include negative controls that remove admission/publication and make the test fail at the causal boundary, not an arbitrary delay. No physical package deletion is permitted in this first gate.

Exercise every driver in [admission contracts](contracts/admission.md), including direct static content/archive reads, restore, scanners, state snapshots, host-integrated loading and custom participants. Wrong-root/disposed capability, concurrent parent close/borrow, multi-root unwind and cancellation must perform zero unauthorized reads. A path beneath an enrolled ancestor that links outside cannot become Unenrolled; test intermediate links/junctions, final content links and archive hardlink ambiguity. Unsupported custom serializers refuse before enrollment; a falsely participating serializer that drops protection fails saved-payload verification and keeps authority incomplete. Pure state-file observation remains available; raw path projections do not authorize package access.

## Physical deletion gate

Only after the preceding real-component gate passes, use the optional maintenance API with explicit retention and execute confirmation in an owned fixture. Default Preview and omitted retention delete nothing. Retain active, offline historical LKG, shared dependencies and real live leases. Delete an actual unused completed package directory; verify every payload is gone and every protected graph can still materialize. Staging/control/state files remain untouched.

Preview, then protect an earlier candidate in another state before Execute; the fresh plan retains it. Race candidate identity/link replacement, symlinked root aliases and Windows junction/reparse entries. A relative quarantine/deletion walker must not traverse into outside sentinels. Compare actual disk results with every Deleted/Partial/Failed outcome. Inject denial, cancellation after an earlier deletion, and a repeated execute. Retain meaningful mutation controls for before-read admission, protection completeness, identity/no-follow and fresh replanning.

## Lifetime, process termination and platform matrix

Hold collectible context references after `Unload()` and prove leases remain protected; release references and observe actual weak death before release. Keep the owner process alive and idle without new admission; a sibling pruner must eventually observe sentinel release from the passive local weak observer. Removing that observer is a causal negative control. Tests may use controlled GC to establish test evidence; production must not initiate GC/unload. Partial-load failures and lazy managed/native resolution retain protection. Noncollectible contexts retain until child-process exit.

Kill controlled children before/after sentinel publication, root Incomplete, each member write, pending state publication, state replacement and acknowledgement. Recover only exact old/new evidence and preserve persistent active/LKG graphs. Busy sentinel probes do not wait under root/state locks. Missing/mismatched evidence and failed stale cleanup refuse deletion. Normal File.Replace changes the current file ID while preserving the state slot; parent/name/hardlink substitutions refuse.

Extend `.github/workflows/validate.yml` with the actual admission/deletion integration suites on ubuntu-latest, macos-latest and windows-latest, preserving existing state-persistence jobs. Capture exact source head, platform/filesystem identity, test counts and raw logs. A native lock probe or mocked planner is not deletion acceptance. Complete all supported platform gates before release.

## Final delivery evidence

Independent self-review and root review accompany automated/behavioral gates; Copilot and Greptile are waived for this delivery. Record implementation and platform evidence on Nuplane #108/its PR. Then follow the parent program's ordered Nuplane release, CShells integration release and Foundation stable-pin/actual-host regression gates. Refresh live versions before selecting tags; do not overwrite existing releases. This work unit alone does not accept stable downstream delivery.

For the internal locked two-state producer-to-Complete boundary and its exact limits, see [protected enrollment completion evidence](evidence/protected-enrollment-completion.md). Runtime admission, leases and destructive operations remain unaccepted.

For the native inventory and accepted pure retention mechanisms, see [inventory and retention evidence](evidence/inventory-and-retention.md). Inventory is qualified on all four hosted platforms; public preview/execution remains pending.

For the internal fresh all-member/live-use inspection kernel and its causal protection control, see [fresh inspection evidence](evidence/fresh-inspection.md). Public operations, stale-use recovery and the actual maintenance/before-read gate remain pending.
