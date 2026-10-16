# Validation guide

**Qualification guide:** the product APIs, suites, and supported native gates are implemented and qualified for Nuplane 0.0.12. Use [manual package-store maintenance](../../docs/package-store-maintenance.md) for configuration and public operations. Exact-source [build](evidence/builds.md), [serial test](evidence/tests.md), and [five-profile platform acceptance](evidence/platform-acceptance.md) records cover Validate 38835166221 at `3f8f2842060423420f26587aef62bacefbf71898`; the merged `main` tree is identical. The artifact-only native probes described in [research.md](research.md) remain separate design evidence. Downstream package-family/host qualification and Foundation adoption remain separate program gates.

Use unique owned temporary stores and state directories for every run. Never point the harness at an existing user/customer store. All local heavy commands use the machine's normal build-slot wrapper; do not bypass a queued slot. Build only affected projects until the final cross-cutting gate.

## Prerequisites and enrollment

Use .NET 10 for tests; source builds must retain net8/net9/net10. Prepare actual local package feeds with v1/v2 roots, a shared dependency, an unused completed package, a support package, and lazy managed/native resolution fixtures. Preserve the exact offline active/recoverable selection accepted by the current `UseLastKnownGood` policy. Include an unselected historical-only recoverable graph as an explicit refusal case; extra LKG-map entries do not select additional graphs. Configure two real Nuplane compositions with the same physical install root and different state slots outside it. Use additional child processes for crash/lock scenarios. Keep all users stopped during enrollment and supply the complete state list.

Preview enrollment, then explicitly enroll under the quiescent confirmation. Verify Incomplete appears before member writes and Complete only after every active/LKG snapshot is known and verified. Resume upgraded clients with optional Admin absent or pruning disabled locally and prove admission still applies. Legacy unknown LKG must refuse completion until verified migration or explicit retirement; ordinary access cannot erase it.

## Non-destructive boundary gate first

Run the focused store/runtime/loading suites:

```bash
dotnet test test/Nuplane.Store.Tests/Nuplane.Store.Tests.csproj --configuration Release
dotnet test test/Nuplane.Runtime.Tests/Nuplane.Runtime.Tests.csproj --configuration Release
dotnet test test/Nuplane.Loading.Tests/Nuplane.Loading.Tests.csproj --configuration Release
```

Synchronize at the real earliest resolver/metadata read in composition A. Let B with its distinct state request maintenance. Assert A already holds root authority, B cannot see an unprotected window, and the selected complete graph lease is published before retained metadata/loading. Release A's short owner while retaining its actual context and prove B still protects the graph. Include negative controls that remove admission/publication and make the test fail at the causal boundary, not an arbitrary delay. No physical package deletion is permitted in this first gate.

Exercise every driver in [admission contracts](contracts/admission.md), including direct static content/archive reads, restore, scanners, state snapshots, host-integrated loading and custom participants. Wrong-root/disposed capability, concurrent parent close/borrow, multi-root unwind and cancellation must perform zero unauthorized reads. A path beneath an enrolled ancestor that links outside cannot become Unenrolled; test intermediate links/junctions, final content links and archive hardlink ambiguity. Unsupported custom serializers refuse before enrollment; a falsely participating serializer that drops protection fails saved-payload verification and keeps authority incomplete. Pure state-file observation remains available; raw path projections do not authorize package access.

## Physical deletion gate

Only after the preceding real-component gate passes, use the optional maintenance API with explicit retention and execute confirmation in an owned fixture. Default Preview and omitted retention delete nothing. Retain active graphs, offline recoverable graphs selected by the current `UseLastKnownGood` policy, shared dependencies and real live leases. Delete an actual unused completed package directory; verify every payload is gone and every protected graph can still materialize. Staging/control/state files remain untouched.

Preview, then protect an earlier candidate in another state before Execute; the fresh plan retains it. Race candidate identity/link replacement, symlinked root aliases and Windows junction/reparse entries. A relative quarantine/deletion walker must not traverse into outside sentinels. Compare actual disk results with every Deleted/Partial/Failed outcome. Inject denial, cancellation after an earlier deletion, and a repeated execute. Retain meaningful mutation controls for before-read admission, protection completeness, identity/no-follow and fresh replanning.

## Lifetime, process termination and platform matrix

Hold collectible context references after `Unload()` and prove leases remain protected; release references and observe actual weak death before release. Keep the owner process alive and idle without new admission; a sibling pruner must eventually observe sentinel release from the passive local weak observer. Removing that observer is a causal negative control. Tests may use controlled GC to establish test evidence; production must not initiate GC/unload. Partial-load failures and lazy managed/native resolution retain protection. Noncollectible contexts retain until child-process exit.

Kill controlled children before/after sentinel publication, root Incomplete, each member write, pending state publication, state replacement and acknowledgement. Recover only exact old/new evidence and preserve persistent active/LKG graphs. Busy sentinel probes do not wait under root/state locks. Missing/mismatched evidence and failed stale cleanup refuse deletion. Normal File.Replace changes the current file ID while preserving the state slot; parent/name/hardlink substitutions refuse.

Run the implemented `.github/workflows/validate.yml` admission/deletion suites on Linux x64/arm64, macOS x64/arm64 and Windows x64, preserving existing state-persistence jobs. Capture exact source head, platform/filesystem identity, test counts and raw logs. A native lock probe or mocked planner is not deletion acceptance. Complete all supported platform gates before release.

## Final delivery evidence

Independent source and root acceptance reviews accompany the automated/behavioral gates. The hosted evidence is linked above and bound to the exact candidate source tree. Nuplane 0.0.12 has been published. The parent program still requires the separate package-family consumer, CShells serving-generation promotion/drain, Foundation stable-pin, and actual-host regression gates. This work unit does not accept downstream adoption.

Earlier increment reports retain their historical acceptance limits. They do not describe the complete current implementation:

- [Protected enrollment completion](evidence/protected-enrollment-completion.md) and [public maintenance](evidence/public-maintenance.md) cover core completion and the later explicit public enrollment/migration/recovery boundary.
- [Inventory and retention](evidence/inventory-and-retention.md), [fresh inspection](evidence/fresh-inspection.md), and [native control recovery and maintenance overlap](evidence/native-control-recovery-and-maintenance-overlap.md) describe the prerequisites for public physical execution.
- [Post-loading observers](evidence/post-loading-observers.md) records coordinated catalog refresh, ordinary callback compatibility and the deferred Generate-mode regression control. [Maintenance observability](../../docs/package-store-maintenance-observability.md) explains optional diagnostics and their limits.
- [Confirmed stale-use recovery](evidence/stale-use-execution.md) records the Execute-only recovery boundary, public refusal/preservation tests, causal control and actual source-consumer deletion.
- The strict test manifests under `build/test-cases/` name mandatory cases for each hosted lane. Final delivery must retain raw results, source commit and actual package provenance; historical green runs do not qualify later edits.
