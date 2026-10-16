# Implementation Plan: Safe Manual Package-Store Pruning

**Branch**: `108-safe-package-store-pruning` | **Date**: 2026-10-09 | **Spec**: [spec.md](spec.md)

**Status**: Design accepted after independent review and root verification on 2026-10-09. The 128-artifact implementation checklist is independently reviewed and root-accepted in [tasks.md](tasks.md). Implementation is in progress; the current accepted rows and per-increment evidence are maintained in [tasks.md](tasks.md), including bounded prerequisites and the T124 package/host acceptance plan. Full runtime admission, graph lifetimes and package-deletion acceptance remain outstanding.

## Summary

Coordinate every admitted user of one physical package-install root while preserving multiple independent persistent states. Persist complete active/LKG graph paths, publish graph-use protection before package content is read, retain it for actual load lifetime, then provide explicit manual inventory/retention/execute. Default dry-run and absent-retention keep-all remain mandatory. Actual deletion follows a fresh root-plus-all-state snapshot through a no-follow exact-identity filesystem primitive; policy eligibility is never physical deletion evidence.

The root engineering selection is recorded in [Nuplane #108](https://github.com/valence-works/nuplane/issues/108#issuecomment-6076600594). Ordinary Nuplane compositions retain released-compatible behavior by default. Enabling pruning requires a fully quiescent operator cutover through durable complete membership publication and explicit participation by every client using that store, including clients that do not enable maintenance locally. Participating compositions refuse unknown authority before package-path reads and never retry through ordinary routes; unsupported environments refuse pruning. Complete client coverage is an operator assertion. Arbitrary noncooperating readers/writers are outside this coordinated-root contract; runtime cannot discover them.

## Technical Context

**Language/Version**: C# SDK projects; source `net8.0;net9.0;net10.0`, tests `net10.0`.
**Primary Dependencies**: Existing Microsoft.Extensions DI/Options/Logging, NuGet libraries, System.Runtime.Loader in the optional Loading module, xUnit/NSubstitute. An internal per-platform native filesystem adapter is selected; no new package dependency is selected. Runtime platform qualification remains an implementation gate.
**Storage**: Existing atomic JSON state files; additive complete protection metadata; root-relative coordination/membership and immutable use records with OS-held sentinels. Root coordination must not derive from one state-file path.
**Testing**: Focused existing runtime/store/loading suites, new filesystem and real-composition boundary tests, then isolated physical deletion and Windows/Linux/macOS runtime acceptance. All heavy commands are serial through the machine build-slot wrapper.
**Target Platform**: Local supported Windows/Linux/macOS filesystems whose identity/locking/no-follow operations are established by tests. Unsupported providers refuse destructive operations.
**Project Type**: Host-neutral libraries with an optional admin capability, no automatic prune timer or new host-specific policy.
**Performance Goals**: Short operation ownership with nonblocking live-use probes; no wait for a lifetime sentinel while holding root/state locks. No unmeasured throughput or timing target is invented.
**Constraints**: Preserve required existing interfaces, positional state constructor and ordinary legacy normalization; custom implementations require explicit optional coordination support on enrolled roots. No Elsa/CShells dependency or competing unload policy.
**Scale/Scope**: One physical root, multiple independently persisted state registries and multiple online/offline readers/processes. One active implementation lane; parallel source/design review only.

## Constitution Check

**Pre-research:** passed. **Post-design gate:** passed after independent review and root verification of research, data model, contracts and validation guide. Three findings were corrected: component-wise alias authority, optional serializer participation/round-trip checks, and an idle-process weak-death observer. FR-016 explicitly distinguishes passive lifetime observation from automatic pruning. Task decomposition and all implementation acceptance gates remain required.

| Principle | Planned evidence / current constraint |
|---|---|
| Deterministic reconciliation | Deterministic member-lock and candidate order, fresh authoritative reread, idempotent repeated execution; preserve existing single-flight and bounded retry semantics. |
| Transactional store safety | Atomic state commit includes protection, incomplete enrollment is visible and conservative, all offline active/LKG closures survive; exact quarantine/deletion results include partial failure. |
| Source integrity | Maintenance cannot introduce feeds or arbitrary paths; existing acquisition trust/hash validation preserved. |
| Observability | Correlated maintenance outcomes and refusal/protection reasons; no planned-deletion metrics credited as physical IO. |
| Test discipline | Real two-composition before-read proof precedes deletion; actual platform, crash, overlap, stale-preview and path-swap tests plus causal negative controls required. |
| Decomposition | Each mechanism and direct entry point receives its own artifact task; no combined engine/driver completion shortcut. |
| Options validation | Data-only options, explicit consumers/constraints, `IValidateOptions<T>` and `ValidateOnStart()`; no orphan pruning switch that bypasses enrolled-root admission. |

## Source-Grounded Findings

Baseline: Nuplane `eb2cf6c2ee1f79dc2c45fb83cc415bbe4856d0d4`. Root independently verified 46 source hashes in the read-only ingress/state map; requirements-only commits do not modify those files.

- `StoreLock` is named from the state file. `ReconciliationService.TriggerAsync` currently takes state ownership around refresh and the pipeline. It must take enrolled-root ownership first.
- `CleanupMiddleware` persists state through `StoreRegistry` and atomic JSON replacement. Active descriptors contain paths; graph records contain IDs/versions; historical LKG only has ID/version. Complete historical LKG graph paths require additive persistence and explicit legacy migration/unknown handling.
- `PackageInstallStore` is static. Installation, completion/hash probes and staging sit behind resolver/acquirer call paths; resolving a root path is lexical today.
- `PackageLoadModeSelector` calls metadata advisors before activation gates. A gate added only at assembly load is too late.
- `PackageAutoLoadingObserver` runs inside reconciliation; a second root-lock acquisition there self-contends. A typed, exact-owner handoff is required through pipeline/dispatch.
- `PackageContent`, `NuplaneStore` and `NuplaneHostIntegratedLoader` have public static path-only methods. They cannot be treated as protected by injected DI services. Their compatible scoped overloads and enrolled-root lookup/refusal behavior are a specific design obligation.
- Existing unload requests do not prove context death. Loading owns a weak-reference lifetime association and must not add a second forced-unload policy or release early on partial failure.
- `PackageCleanupService` evaluates policy and does no package deletion. Its `Deleted` action cannot supply execute results.

## Proposed Architecture Boundaries

These responsibilities are selected in the generated contracts; independent/root review and artifact-by-artifact tasks follow before implementation.

1. Core physical-root identity/admission coordinator and durable multi-state membership. Core registration participates on an enrolled root without requiring the optional Admin package or a local pruning flag.
2. Additive per-state complete protection, atomically committed with current reconciliation state. Quiescent migration reconstructs verifiable closures; incomplete legacy evidence remains unknown rather than inferred empty.
3. Typed opaque root-operation ownership, exact-root/lifetime borrows and separate long-lived graph-use leases. An owner is not a process-wide boolean. Child lease publication and parent release must have a defined linearization; retained IO uses graph leases after short operation ownership ends.
4. Explicit optional scoped companions for resolver/observer/direct readers and an internal admitted reader core. Existing required interfaces remain source-compatible. Enrolled custom/legacy entry points cannot silently bypass admission.
5. Loading-owned attachment to actual collectible/noncollectible contexts, preserving protection through partial loads. Actual weak-reference death may release collectible protection; unload-request or catalog replacement is insufficient.
6. Separate inventory, pure retention planning, optional manual admin orchestration and filesystem executor. Execution holds current authority through real IO, uses exact completed-install identities, excludes staging, and reports quarantine/partial deletion honestly.
7. Narrow native filesystem adapter for stable root identity and handle-relative no-follow quarantine/deletion. Root locks serialize admitted mutations; this is not a claim to defeat arbitrary hostile same-identity writers outside the protocol.

## Selected Engineering Decisions

[research.md](research.md) records decisions, rationale, rejected alternatives, primary references and limited native-probe evidence. [data-model.md](data-model.md) defines identity/publication/lifetime transitions. [Admission contracts](contracts/admission.md) define opaque capabilities, compatibility and each driver obligation; [maintenance contracts](contracts/maintenance.md) define the optional public entry point and real execute semantics. [quickstart.md](quickstart.md) defines pending reproducible product/platform gates.

The maintainer resolved the compatibility decision on 2026-10-11: preserve ordinary Nuplane compatibility and restrict pruning to verified filesystem environments where every client sharing the store participates in coordination. [The recorded decision](evidence/pruning-support-policy.md) defines the support boundary and its remaining implementation proof. This replaces the pending owner-answer hold; it does not activate enrollment or execution.

- Root membership is multi-state and epoch-bound. Complete authority requires complete known active and recoverable LKG closure for every member. Unknown legacy promises keep enrollment incomplete; no partial-active admission is selected.
- State membership binds parent directory identity plus basename, surviving verified atomic replacement. Current state file identity is a revision/race observation. Every enrolled state write uses a pending prior/next publication and verified acknowledgement.
- Physical authority uses component-wise open-handle metadata lookup, tracking enrolled authority across aliases and refusing escapes. Darwin/Linux/Windows adapters implement native identity/no-follow/relative operations; unsupported capabilities fail closed. The selected design has no lexical recursive-delete fallback. C and .NET Darwin probes are limited mechanism evidence, not platform deletion acceptance.
- Transient resolution reads remain under short root ownership; retained selected graphs acquire immutable leases before first retained read. Lease views have counted read pins; context ownership is weak/non-owning, and actual death or process exit governs release. A passive local weak observer runs while collectible associations exist, so idle owners can release dead-context sentinels without GC/unload or automatic pruning.
- An optional serializer participation companion preserves the existing serializer interface; unsupported custom serializers refuse enrollment, and every saved state/protection payload is round-trip verified.
- Explicit optional companions carry root scopes through every participating driver and awaited observer callback. Existing required interfaces/positional constructors and released legacy static-reader compatibility remain. Participant compositions require protected readers and never retry through legacy calls; pure state observation remains available. The explicit mode boundary is defined in [admission contracts](contracts/admission.md#support-boundary).
- Manual execution confirms root epoch/retention and replans under root/all-state ownership. Preview IDs correlate only. Absent retention keeps all; actual quarantine/partial/deleted outcomes remain distinct.

These are engineering selections under existing authorization, not pending owner permission or grounds to pause the Codex goal. All real component, process-termination, physical deletion and supported-platform gates remain required implementation work.

## Project Structure

```text
specs/030-safe-package-store-pruning/
  spec.md
  checklists/requirements.md
  plan.md
  research.md                 # selected design decisions and evidence limits
  data-model.md               # enrollment/protection/use/report transitions
  contracts/                  # additive public/scoped contracts
  quickstart.md               # pending owned-store/platform acceptance guide
  tasks.md                    # after plan acceptance: one artifact per task
src/Nuplane/Store/            # core coordination/protection; preserve module direction
src/Nuplane/Feeds/            # explicit resolver/acquisition admission
src/Nuplane/Restore/          # host-free scoped entry points
src/Nuplane/Hosting/          # root-before-state LKG recovery
src/Nuplane/Events/           # optional explicit scope dispatch
src/Nuplane.Loading/          # before-advisor graph leases and actual context lifetime
src/Nuplane.Admin/            # optional manual capability, no required-interface break
src/Nuplane.Admin.Api/        # separate explicit driver if included in final contract
 test/Nuplane.Runtime.Tests/
 test/Nuplane.Store.Tests/
 test/Nuplane.Loading.Tests/
 test/Nuplane.Integration.Tests/
```

**Structure Decision**: Keep coordination below Loading and Admin, and keep host-specific readiness/unload policy in its existing owners. The accepted implementation checklist enumerates the artifact tasks; no new package is selected. AGENTS.md now points to this plan in its managed section.

## Review Record

Independent review found and re-reviewed the three corrections above; final review SHA-256 `d918b61bbaee452b9e3fed923a566176a9707c6cdab1f12a9e6f079a745f59c2`. Root checked all final artifact hashes, 54 distinct current-source files, links and whitespace. The independent source observations used a prior snapshot; root closed that provenance gap by verifying all seven referenced source files byte-identical between `21e2c24` and current `03b48ac`. Spec SHA-256 after the FR-016 clarification is `29154bd3b9a5b1809c8e0b81aff7954261209bcb8279132810cd89cb3fdf78c2`. Original findings, re-review, probe evidence and root verification remain retained in the delivery workspace.

At the original design-acceptance checkpoint, only plan/checklist acceptance metadata changed after the frozen independent re-review; no product code, store, package version or release changed at that checkpoint. Before/after plan hooks were absent because `.specify/extensions.yml` did not exist. AGENTS.md has the managed plan reference. Subsequent implementation and verification are recorded in the checklist and linked evidence.

### Native entry-removal prerequisite (2026-10-11)

The accepted T105 two-composition overlap gate permits T107's isolated native work. The first implementation slice is `IPhysicalStoreDeletionFileSystem`: `RemoveFileAt(parent, singleName, expectedIdentity)` and `RemoveEmptyDirectoryAt(parent, singleName, expectedIdentity)`. Both use one exact canonical component relative to a provider-owned held parent; refuse links/reparse points, missing/replaced identities, cross-volume entries and profile changes; and verify positive absence after native transition/handle close. Shared `PhysicalStoreDeletionChecks` reuses the existing canonical directory observation and publication identity checks. Existing transaction-control removal and payload-file removal share a private native/check kernel while retaining separate contracts. The empty-only OS operation refuses newly populated directories without recursive fallback.

Unix uses fixed-signature `unlinkat` with zero flags for a regular file and platform-specific `AT_REMOVEDIR` for an empty directory. Darwin's installed SDK and [Apple source](https://github.com/apple-oss-distributions/xnu/blob/main/bsd/sys/fcntl.h) define `0x0080`; the [Linux UAPI](https://github.com/torvalds/linux/blob/master/include/uapi/linux/fcntl.h) defines `0x0200`. Windows uses a no-follow relative native open with delete access, validates exact directory/file identity on the actual removal handle, sets [FileDispositionInformation](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-fscc/12c3dd1c-14f6-4229-9d29-75fb2cb392f6), closes it and establishes positive absence. Native errors are never retried blindly or translated into successful removal.

This companion has no registration or public Execute caller. The executor must later supply retained complete root/all-state ownership, freshly verified eligibility/containment, quarantine identity, bounded no-follow traversal and partial-result reconciliation. Quarantine naming/location and crash recovery need their own source-grounded design; the existing prepared-directory publication supplies a reusable native kernel, not a completed quarantine contract. T100/T101 all-driver safety and T106–T113 remain open. Native fixture tests cover actual success, nonempty preservation, wrong/replaced identities, aliases/links, confinement and foreign/disposed parents before the new methods can be accepted on supported platforms.

### Quarantine and recovery implementation contract (2026-10-11)

[Exact quarantine and recovery](contracts/quarantine-recovery.md) records the source-grounded T106–T113/T118 engineering contract. A full bounded exact-entry manifest is durable in Prepared before the first quarantine move; every later phase retains it. Marker-last deletion and exact surviving-subset checks preserve partial facts while fresh complete owner/planner admission decides every resume. Detached journal data is never mutation authority. This contract supersedes the earlier count-only recovery draft; implementation and native/process qualification remain required. No public activation or new owner-policy choice is introduced.


### Internal fresh execution planning

The [bounded planning increment](evidence/fresh-execution-planning.md) factors inspection ownership/cleanup and projection for internal execution reuse. An exact operation token spans full-catalog verification, graph-use inventory, native install inventory, fresh retention planning, replay and the awaited candidate callback. It requires current enrollment/epoch and explicit retention, and refuses uncertain plans before candidate work. Ordinary inspection preserves its wrapper semantics. This boundary has no public caller or deletion implementation; exact Eligible-row/native binding and the verified quarantine-journal/inventory join remain required before recursive execution or recovery acceptance.
