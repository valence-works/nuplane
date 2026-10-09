# Implementation Plan: Safe Manual Package-Store Pruning

**Branch**: `108-safe-package-store-pruning` | **Date**: 2026-10-09 | **Spec**: [spec.md](spec.md)

**Status**: Architecture research in progress. Requirements are committed; this plan is not yet an implementation authorization/checklist completion record. No product code or package deletion has been performed for Spec030.

## Summary

Coordinate every admitted user of one physical package-install root while preserving multiple independent persistent states. Persist complete active/LKG graph paths, publish graph-use protection before package content is read, retain it for actual load lifetime, then provide explicit manual inventory/retention/execute. Default dry-run and absent-retention keep-all remain mandatory. Actual deletion follows a fresh root-plus-all-state snapshot through a no-follow exact-identity filesystem primitive; policy eligibility is never physical deletion evidence.

The root engineering selection is recorded in [Nuplane #108](https://github.com/valence-works/nuplane/issues/108#issuecomment-6076600594). Existing-root enrollment is a fully quiescent operator cutover through durable complete membership publication. Non-enrolled roots retain ordinary behavior; upgraded clients of enrolled roots must participate even without enabling maintenance locally. Unknown authority denies ordinary package-path access until verified recovery and refuses deletion. Arbitrary noncooperating readers/writers are outside this coordinated-root contract; runtime cannot discover them.

## Technical Context

**Language/Version**: C# SDK projects; source `net8.0;net9.0;net10.0`, tests `net10.0`.
**Primary Dependencies**: Existing Microsoft.Extensions DI/Options/Logging, NuGet libraries, System.Runtime.Loader in the optional Loading module, xUnit/NSubstitute. Native filesystem interop design requires review; no new package dependency is selected.
**Storage**: Existing atomic JSON state files; additive complete protection metadata; root-relative coordination/membership and immutable use records with OS-held sentinels. Root coordination must not derive from one state-file path.
**Testing**: Focused existing runtime/store/loading suites, new filesystem and real-composition boundary tests, then isolated physical deletion and Windows/Linux/macOS runtime acceptance. All heavy commands are serial through the machine build-slot wrapper.
**Target Platform**: Local supported Windows/Linux/macOS filesystems whose identity/locking/no-follow operations are established by tests. Unsupported providers refuse destructive operations.
**Project Type**: Host-neutral libraries with an optional admin capability, no automatic prune timer or new host-specific policy.
**Performance Goals**: Short operation ownership with nonblocking live-use probes; no wait for a lifetime sentinel while holding root/state locks. No unmeasured throughput or timing target is invented.
**Constraints**: Preserve required existing interfaces, positional state constructor and ordinary legacy normalization; custom implementations require explicit optional coordination support on enrolled roots. No Elsa/CShells dependency or competing unload policy.
**Scale/Scope**: One physical root, multiple independently persisted state registries and multiple online/offline readers/processes. One active implementation lane; parallel source/design review only.

## Constitution Check

**Pre-research:** the requirements cover all constitution concerns and permit design research. **Post-design gate: pending**, until the remaining engineering choices below are resolved, data model/contracts are written and independent/root review accepts the complete plan. No implementation may start from this draft.

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

These responsibilities are selected for planning; exact names/signatures and full integration graph follow in the contracts/tasks after remaining choices are reviewed.

1. Core physical-root identity/admission coordinator and durable multi-state membership. Core registration participates on an enrolled root without requiring the optional Admin package or a local pruning flag.
2. Additive per-state complete protection, atomically committed with current reconciliation state. Quiescent migration reconstructs verifiable closures; incomplete legacy evidence remains unknown rather than inferred empty.
3. Typed opaque root-operation ownership, exact-root/lifetime borrows and separate long-lived graph-use leases. An owner is not a process-wide boolean. Child lease publication and parent release must have a defined linearization; retained IO uses graph leases after short operation ownership ends.
4. Explicit optional scoped companions for resolver/observer/direct readers and an internal admitted reader core. Existing required interfaces remain source-compatible. Enrolled custom/legacy entry points cannot silently bypass admission.
5. Loading-owned attachment to actual collectible/noncollectible contexts, preserving protection through partial loads. Actual weak-reference death may release collectible protection; unload-request or catalog replacement is insufficient.
6. Separate inventory, pure retention planning, optional manual admin orchestration and filesystem executor. Execution holds current authority through real IO, uses exact completed-install identities, excludes staging, and reports quarantine/partial deletion honestly.
7. Narrow native filesystem adapter for stable root identity and handle-relative no-follow quarantine/deletion. Root locks serialize admitted mutations; this is not a claim to defeat arbitrary hostile same-identity writers outside the protocol.

## Engineering Research Still To Resolve

- Choose precise durable membership/protection schema, incomplete/complete publication and crash recovery transitions, including LKG path reconstruction and member removal/move handling.
- Settle physical root/state identity and tested per-OS native ABI/handle operations. No lexical-check-plus-recursive-delete fallback is acceptable.
- Resolve path-only public helpers: metadata-only physical authority discovery must work through supported aliases and fail closed on uncertainty; injected-only or lexical ancestor-marker checks are inadequate. Additive owner-aware reading cannot leave old direct calls as an enrolled-root bypass.
- Define the smallest explicit cycle-to-observer/loader owner bridge and all standalone resolver/restore/catalog/scanner/static entry points. Verify cancellation, wrong-root and disposed-scope behavior without ambient bypass.
- Select the exact non-owning weak-context observation/release mechanism that complements the existing unload owner and eventually releases genuinely dead collectible uses without forcing GC/unload or retaining the context itself.
- Define public maintenance request/retention/result contracts and explicit confirmation/stale-preview behavior. Keep execution and preview distinct and current unknown/refusal/partial outcomes visible.

These are engineering tasks under existing authorization, not pending owner permission or grounds to pause the Codex goal. Research artifacts are retained in the program workspace; accepted decisions will be copied into repository-owned research/data-model/contracts before implementation.

## Project Structure

```text
specs/030-safe-package-store-pruning/
  spec.md
  checklists/requirements.md
  plan.md
  research.md                 # next: accepted design decisions and source references
  data-model.md               # next: enrollment/protection/use/report transitions
  contracts/                  # next: additive public/scoped contracts
  quickstart.md               # next: executable owned-store acceptance guide
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

**Structure Decision**: Keep coordination below Loading and Admin, and keep host-specific readiness/unload policy in its existing owners. Final source files/tasks are enumerated after the research decisions above are accepted; no speculative new package is selected.
