# Research: Runtime Folder & Namespace Reorganization

**Branch**: `010-runtime-folder-reorganization` | **Date**: 2026-03-07

All decisions below resolve the technical context and unknowns for the folder/namespace
reorganization of `Nuplane.Runtime`.

---

## D-001 — Move Sequencing Strategy

**Decision**: Execute the three moves sequentially in priority order: (1) Feeds, (2) Sources, (3) Trust Gates. Each move includes the file relocations, namespace updates, and all `using` statement updates across `src/` and `test/`. The solution must compile and all tests must pass after each individual move.

**Rationale**: The spec defines these as P1/P2/P3 with explicit independence requirements (OSR-002). Sequential execution avoids conflicting intermediate states — particularly between the Feeds move (which affects `Reconciliation/FeedPolicy/` namespace) and the Trust move (which affects files in `Trust/Feeds/` that currently use `Reconciliation.FeedPolicy` namespace). Completing the Feeds move first establishes the `Nuplane.Runtime.Feeds.Policy` namespace, so the Trust/Feeds namespace update in the same move is coherent.

**Alternatives considered**:
- All three moves in a single batch (rejected: violates OSR-002 independent compilability requirement; no rollback to intermediate states).
- Alphabetical order (rejected: Trust depends on Feeds namespace being established first; spec explicitly requires Feeds first).

---

## D-002 — Namespace Strategy for Feed Configuration Files

**Decision**: Files currently in `Configuration/` that are feed-specific (`FeedResolutionOptions.cs`, `FeedResolutionPolicyMode.cs`, `FeedCredentialOptionsValidator.cs`) move to `Feeds/Configuration/` with namespace `Nuplane.Runtime.Feeds.Configuration`. `ManifestOptions.cs` remains in `Configuration/` with namespace `Nuplane.Runtime.Configuration` (FR-013).

**Rationale**: The spec explicitly defines this in FR-003 and the edge case for `ManifestOptions.cs`. The `Configuration/` folder continues to exist with one file. Files referencing `using Nuplane.Runtime.Configuration;` for feed config types need an additional `using Nuplane.Runtime.Feeds.Configuration;` — but files that also reference `ManifestOptions` must keep the original `using Nuplane.Runtime.Configuration;`.

**Alternatives considered**:
- Move all of `Configuration/` (rejected: FR-013 explicitly keeps `ManifestOptions.cs` in place).
- Keep feed config files in `Configuration/` but change namespace (rejected: spec FR-003 requires physical move to `Feeds/Configuration/`).

---

## D-003 — FeedTrustPolicyOptions Namespace Treatment

**Decision**: `FeedTrustPolicyOptions.cs` in `Trust/Feeds/` currently has namespace `Nuplane.Runtime.Configuration`. The spec's FR-004 lists five specific files for namespace update to `Nuplane.Runtime.Feeds.Policy` but does NOT list `FeedTrustPolicyOptions.cs`. Since this file defines options (configuration data), its namespace should change to `Nuplane.Runtime.Feeds.Configuration` to be consistent with the other feed configuration types.

**Rationale**: `FeedTrustPolicyOptions` is a configuration/options type, not a policy evaluator. Moving it to the `Feeds.Configuration` namespace aligns with the pattern established by FR-003. The spec's FR-004 file list covers the five non-options files. The spec's edge case explicitly states files in `Trust/Feeds/` stay physically in `Trust/Feeds/` but get namespace updates. `FeedTrustPolicyOptions` should follow the same pattern with the appropriate namespace for its type category.

**Alternatives considered**:
- Leave namespace as `Nuplane.Runtime.Configuration` (rejected: creates inconsistency where a feed trust options type shares a namespace with `ManifestOptions` which is unrelated to feeds).
- Move to `Nuplane.Runtime.Feeds.Policy` (rejected: options types are not policy types; the configuration vs policy distinction is maintained across the codebase).

---

## D-004 — `using` Statement Update Strategy (Conservative Approach)

**Decision**: Follow the spec's conservative `using` statement approach:
1. **ADD** new `using` statements for moved namespaces where files reference moved types.
2. **REMOVE** old `using` statements ONLY when the file no longer references ANY type from that namespace.
3. **KEEP** old `using` statements when the file still references types that remain in the old namespace.

**Rationale**: The spec's assumptions section explicitly defines this conservative approach. Many files reference both moved and remaining types from `Nuplane.Runtime.Reconciliation;` — these files must keep the existing `using` AND add new ones (e.g., `using Nuplane.Runtime.Feeds;`). Key example: `ReconciliationService.cs` uses both `MultiFeedPackageResolver` (moving to Feeds) and `IReconciliationService` (staying in Reconciliation) — it needs both `using` statements.

**Alternatives considered**:
- Aggressive cleanup removing all unused `using` statements (rejected: out of scope; introduces risk of removing statements that are actually needed for types not immediately visible in the file).

---

## D-005 — FeedTrustPolicyOutcome Namespace Clarification

**Decision**: `FeedTrustPolicyOutcome.cs` currently has namespace `Nuplane.Runtime.Reconciliation.Models` (not `Nuplane.Runtime.Reconciliation.FeedPolicy` as one might expect from its location in `Trust/Feeds/`). The spec's FR-004 lists it for namespace update to `Nuplane.Runtime.Feeds.Policy`.

**Rationale**: The spec explicitly names `FeedTrustPolicyOutcome` in FR-004's file list. Even though its current namespace is `Reconciliation.Models` rather than `Reconciliation.FeedPolicy`, the intent is clear: it's a feed policy type that should live in the feed policy namespace. Files that currently reach it via `using Nuplane.Runtime.Reconciliation.Models;` will need `using Nuplane.Runtime.Feeds.Policy;` added if they reference this type.

**Alternatives considered**:
- Move to `Nuplane.Runtime.Sources` with other models (rejected: it's semantically a feed trust policy outcome, not a desired-state source type).

---

## D-006 — Test File Mirroring Strategy

**Decision**: Test files that directly test moved source types will be relocated to mirror the new source folder structure. Test files that only consume moved types (via `using` statements) will only have their `using` statements updated.

Specific moves:
- `test/Nuplane.Runtime.Tests/Desired/` → all 5 files move to `test/Nuplane.Runtime.Tests/Sources/` (mirroring `Desired/` → `Sources/` consolidation)
- `test/Nuplane.Runtime.Tests/Reconciliation/AllowlistGateTests.cs` → moves to `test/Nuplane.Runtime.Tests/Trust/`
- `test/Nuplane.Runtime.Tests/Reconciliation/DesiredStateAggregatorTests.cs` → moves to `test/Nuplane.Runtime.Tests/Sources/`
- `test/Nuplane.Runtime.Tests/Reconciliation/FeedTrustPolicyEvaluatorTests.cs` → stays (tests trust evaluation, which physically stays in `Trust/Feeds/`)
- Feed-related test files in `Reconciliation/` (MultiFeed*, RemoteFeed*, LocalDirectory*) → could move to a new `test/.../Feeds/` folder for mirroring

**Rationale**: FR-015 and User Story 4 require test folder mirroring. The `Desired/` folder tests are the clearest case since the source `Desired/` folder is completely eliminated. Tests already in `Sources/` (`DesiredSourceSnapshotCacheTests.cs`) stay.

**Alternatives considered**:
- No test folder restructuring, only `using` updates (rejected: FR-015 explicitly requires mirroring where test files correspond to moved source files).
- Move every feed-related test to `Feeds/` (viable but lower priority per User Story 4).

---

## D-007 — DI Registration Impact

**Decision**: No DI registration code changes are needed beyond `using` statement updates in the registration files.

**Rationale**: The spec's assumptions section states "No DI registration changes are needed — service registration calls reference types by their fully qualified or imported names, and updated `using` statements will resolve them." Verified by inspection: `NuplaneServiceCollectionExtensions.cs` registers types by their short names (e.g., `services.AddSingleton<IAllowlistGate, AllowlistGate>()`) which are resolved through `using` statements. Updating the `using` statements is sufficient.

**Alternatives considered**: None — the approach is clear and verified.

---

## D-008 — Handling Files That Reference All Three Moved Concerns

**Decision**: Per the spec's edge case, files in `Reconciliation/` that use types from feeds, sources, AND trust will receive `using` statements for all three new namespaces. The existing `using Nuplane.Runtime.Reconciliation;` is kept (since the file is IN that namespace or references remaining types).

**Rationale**: The spec explicitly calls out this edge case. The primary example is `ReconciliationService.cs` which orchestrates all three concerns and will need `using Nuplane.Runtime.Feeds;`, `using Nuplane.Runtime.Sources;`, and `using Nuplane.Runtime.Trust;` in addition to its existing `using` statements.

**Alternatives considered**: None — the spec is explicit.

---

## D-009 — Remaining `Reconciliation/Models/` Files

**Decision**: After moving `DesiredAggregateResult.cs`, `DesiredReadResult.cs`, and `StaticDesiredSource.cs` to `Sources/`, the `Reconciliation/Models/` folder retains 10 files: `DryRunPlan.cs`, `FeedObservationKind.cs`, `FeedObservationOrigin.cs`, `FeedResolutionDecision.cs`, `LockFileEvaluationResult.cs`, `PackageApplyExecutionResult.cs`, `PackageResolutionResult.cs`, `ReconciliationRunResult.cs`, `ReconciliationTrigger.cs`, `TriggerType.cs`.

**Rationale**: The spec's edge case explicitly states "Reconciliation/Models/ still has files after the desired-state types are moved — the folder remains with the non-moved model files intact." These are reconciliation orchestration models, not desired-state source types.

**Alternatives considered**: None — the spec is explicit.

