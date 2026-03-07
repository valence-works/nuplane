# Implementation Plan: Runtime Folder & Namespace Reorganization

**Branch**: `010-runtime-folder-reorganization` | **Date**: 2026-03-07 | **Spec**: `/specs/010-runtime-folder-reorganization/spec.md`
**Input**: Feature specification from `/specs/010-runtime-folder-reorganization/spec.md`

## Summary

Reorganize the `Nuplane.Runtime` project to separate three tangled logical layers — Feed Acquisition, Desired-State Sources, and Trust Gates — into distinct, well-bounded folders and namespaces. This is a pure structural refactor: files move, namespaces change, `using` statements update, but zero runtime behavior changes. The work is sequenced into three independently compilable moves (Feeds → Sources → Trust), each leaving the solution green after completion.

## Technical Context

**Language/Version**: C# on .NET multi-targeting (`net8.0;net9.0;net10.0`)
**Primary Dependencies**: `Microsoft.Extensions.Options`, `Microsoft.Extensions.Logging`, `Microsoft.Extensions.DependencyInjection`; xUnit for tests
**Storage**: N/A — no store changes; purely structural refactor
**Testing**: `dotnet test` (xUnit); unit tests under `test/Nuplane.Runtime.Tests`; integration tests under `test/Nuplane.Integration.Tests`
**Target Platform**: Cross-platform .NET hosts (Linux/macOS/Windows)
**Project Type**: Multi-project .NET library — primarily `Nuplane.Runtime` with `using` statement updates across `Nuplane`, test projects
**Performance Goals**: N/A — no runtime behavior changes
**Constraints**: Zero behavior change; each move must be independently compilable; no public API surface change (only namespace paths change)
**Scale/Scope**: ~25 files moved/updated in `src/Nuplane.Runtime`; ~30+ files updated for `using` statements across `src/` and `test/`

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

### Pre-Design Gate Assessment

- **Deterministic reconciliation**: PASS — this is a pure structural refactor. No reconciliation logic, retry behavior, or idempotency semantics are modified. All types retain identical implementations; only their file locations and namespace declarations change.
- **Transactional store safety**: PASS — no store mutation paths are added, removed, or modified. Stage/validate/publish/atomic-switch semantics are untouched. LKG fallback behavior is unaffected.
- **Source & supply chain integrity**: PASS — no trusted source boundaries, validation steps, or credential handling are modified. Feed resolution types are relocated but their behavior is identical.
- **Observability & operability**: PASS — observability infrastructure (`Events/`, `Health/`, `Observability/`, `Operational/`) is explicitly excluded from this reorganization (OSR-004). No correlation ID, structured log, metric, or health reporting changes.
- **Test & contract discipline**: PASS — no test logic or assertions change. Only `using` statements and file locations are updated in test projects. OSR-005 requires compilation verification and all existing tests passing after each move — this is the verification mechanism. No new tests are required for a namespace refactor (OSR-005).
- **Decomposition discipline**: PASS — each user story maps to one architectural concern (Feeds folder, Sources consolidation, Trust gates, Test mirroring). Each FR names concrete files and target folders/namespaces. The three moves are sequenced but independently deliverable.
- **Options validation discipline**: PASS — no new options types are introduced. Existing validators (`FeedCredentialOptionsValidator`, `FeedTrustPolicyOptions`) change location/namespace but not behavior.

### Post-Design Re-Check

- **Deterministic reconciliation**: PASS — confirmed no logic changes in any moved file.
- **Transactional store safety**: PASS — no store paths modified.
- **Source & supply chain integrity**: PASS — trust pipeline types relocate within same project boundary.
- **Observability & operability**: PASS — observability folders untouched per OSR-004.
- **Test & contract discipline**: PASS — test folder mirroring and `using` updates defined; no assertion changes.
- **Decomposition discipline**: PASS — each move (Feeds, Sources, Trust) is a single deployable batch with clear file lists.
- **Options validation discipline**: PASS — `FeedCredentialOptionsValidator` moves but validation logic unchanged; `ValidateOnStart` registrations in DI unaffected (type names unchanged, only namespace imports update).

No constitution violations require exception tracking.

## Project Structure

### Documentation (this feature)

```text
specs/010-runtime-folder-reorganization/
├── plan.md              ← this file
├── research.md          ← Phase 0 output
├── data-model.md        ← Phase 1 output
├── quickstart.md        ← Phase 1 output
├── contracts/           ← N/A (no external interface changes)
└── tasks.md             ← Phase 2 output (/speckit.tasks — NOT created by /speckit.plan)
```

### Source Code — Before & After

```text
src/Nuplane.Runtime/
├── Configuration/                              # MODIFIED — 3 files move out, 1 stays
│   ├── FeedCredentialOptionsValidator.cs        → MOVES TO Feeds/Configuration/
│   ├── FeedResolutionOptions.cs                 → MOVES TO Feeds/Configuration/
│   ├── FeedResolutionPolicyMode.cs              → MOVES TO Feeds/Configuration/
│   └── ManifestOptions.cs                       ← STAYS (FR-013)
│
├── Desired/                                    # REMOVED — all files move to Sources/
│   ├── DesiredManifestPackageSource.cs          → MOVES TO Sources/
│   └── DesiredManifestReader.cs                 → MOVES TO Sources/
│
├── Feeds/                                      # NEW FOLDER
│   ├── AcquisitionOutcomeEntry.cs               ← FROM Reconciliation/
│   ├── INuGetPackageResolver.cs                 ← FROM Reconciliation/
│   ├── MultiFeedPackageResolver.cs              ← FROM Reconciliation/
│   ├── NoEligibleFeedException.cs               ← FROM Reconciliation/
│   ├── NuGetPackageResolver.cs                  ← FROM Reconciliation/
│   ├── NuGetRemotePackageAcquirer.cs            ← FROM Reconciliation/
│   ├── Configuration/                          # NEW SUBFOLDER
│   │   ├── FeedCredentialOptionsValidator.cs    ← FROM Configuration/
│   │   ├── FeedResolutionOptions.cs             ← FROM Configuration/
│   │   └── FeedResolutionPolicyMode.cs          ← FROM Configuration/
│   └── Policy/                                 # NEW SUBFOLDER
│       ├── FeedResolutionPolicy.cs              ← FROM Reconciliation/FeedPolicy/
│       └── FeedUnavailableException.cs          ← FROM Reconciliation/FeedPolicy/
│
├── Reconciliation/                             # REDUCED — ~50% file count reduction
│   ├── Configuration/
│   │   └── ReconciliationOptions.cs             ← STAYS (FR-014)
│   ├── Convergence/                             ← STAYS
│   ├── LockFile/                                ← STAYS
│   ├── Middleware/                               ← STAYS
│   ├── Models/                                  # REDUCED — 3 files move out, 10 stay
│   │   ├── DesiredAggregateResult.cs             → MOVES TO Sources/
│   │   ├── DesiredReadResult.cs                  → MOVES TO Sources/
│   │   ├── StaticDesiredSource.cs                → MOVES TO Sources/
│   │   ├── DryRunPlan.cs                         ← STAYS
│   │   ├── FeedObservationKind.cs                ← STAYS
│   │   ├── FeedObservationOrigin.cs              ← STAYS
│   │   ├── FeedResolutionDecision.cs             ← STAYS
│   │   ├── LockFileEvaluationResult.cs           ← STAYS
│   │   ├── PackageApplyExecutionResult.cs        ← STAYS
│   │   ├── PackageResolutionResult.cs            ← STAYS
│   │   ├── ReconciliationRunResult.cs            ← STAYS
│   │   ├── ReconciliationTrigger.cs              ← STAYS
│   │   └── TriggerType.cs                        ← STAYS
│   ├── FeedPolicy/                              # REMOVED — files move to Feeds/Policy/
│   ├── AllowlistGate.cs                          → MOVES TO Trust/
│   ├── IAllowlistGate.cs                         → MOVES TO Trust/
│   ├── AcquisitionOutcomeEntry.cs                → MOVES TO Feeds/
│   ├── INuGetPackageResolver.cs                  → MOVES TO Feeds/
│   ├── MultiFeedPackageResolver.cs               → MOVES TO Feeds/
│   ├── NoEligibleFeedException.cs                → MOVES TO Feeds/
│   ├── NuGetPackageResolver.cs                   → MOVES TO Feeds/
│   ├── NuGetRemotePackageAcquirer.cs             → MOVES TO Feeds/
│   ├── DesiredStateAggregator.cs                 → MOVES TO Sources/
│   ├── IDesiredStateAggregator.cs                → MOVES TO Sources/
│   └── [remaining orchestration files STAY]
│
├── Sources/                                    # EXPANDED — 7 files move in
│   ├── DesiredSourceSnapshotCache.cs            ← STAYS
│   ├── FeedRuleDesiredSource.cs                 ← STAYS
│   ├── FeedRuleResultSelector.cs                ← STAYS
│   ├── DesiredManifestPackageSource.cs          ← FROM Desired/
│   ├── DesiredManifestReader.cs                 ← FROM Desired/
│   ├── DesiredStateAggregator.cs                ← FROM Reconciliation/
│   ├── IDesiredStateAggregator.cs               ← FROM Reconciliation/
│   ├── StaticDesiredSource.cs                   ← FROM Reconciliation/Models/
│   ├── DesiredAggregateResult.cs                ← FROM Reconciliation/Models/
│   └── DesiredReadResult.cs                     ← FROM Reconciliation/Models/
│
├── Trust/                                      # EXPANDED — 2 files move in
│   ├── AllowlistGate.cs                         ← FROM Reconciliation/
│   ├── IAllowlistGate.cs                        ← FROM Reconciliation/
│   ├── Feeds/                                   ← STAYS (files stay, namespaces change)
│   │   ├── FeedTrustPolicyEvaluator.cs          ← NS: Reconciliation.FeedPolicy → Feeds.Policy
│   │   ├── UntrustedOverridePolicy.cs           ← NS: Reconciliation.FeedPolicy → Feeds.Policy
│   │   ├── IFeedTrustPolicyEvaluator.cs         ← NS: Reconciliation.FeedPolicy → Feeds.Policy
│   │   ├── RestrictedFeedValidatorPipeline.cs   ← NS: Reconciliation.FeedPolicy → Feeds.Policy
│   │   ├── FeedTrustPolicyOutcome.cs            ← NS: Reconciliation.Models → Feeds.Policy
│   │   └── FeedTrustPolicyOptions.cs            ← NS: Configuration → Feeds.Configuration
│   └── Source/                                  ← STAYS (unchanged)
│
├── Events/                                      ← STAYS (OSR-004)
├── Health/                                      ← STAYS (OSR-004)
├── Observability/                               ← STAYS (OSR-004)
├── Operational/                                 ← STAYS (OSR-004)
├── Loading/                                     ← STAYS
└── Versioning/                                  ← STAYS

test/Nuplane.Runtime.Tests/
├── Configuration/                               ← using updates only
│   └── FeedCredentialOptionsValidatorTests.cs   ← using update
├── Desired/                                    # POTENTIAL MOVE to Sources/ (test mirroring)
│   ├── DesiredAggregationContractTests.cs       → Sources/ + using updates
│   ├── DesiredAggregationDeterminismTests.cs    → Sources/ + using updates
│   ├── DesiredAggregationDuplicateRegressionTests.cs → Sources/ + using updates
│   ├── DesiredManifestParserTests.cs            → Sources/ + using updates
│   └── DesiredManifestProjectionDeterminismTests.cs → Sources/ + using updates
├── Reconciliation/                              ← using updates for affected tests
│   ├── AllowlistGateTests.cs                    → Trust/ + using updates
│   ├── DesiredStateAggregatorTests.cs           → Sources/ + using updates
│   ├── FeedTrustPolicyEvaluatorTests.cs         ← using updates (Feeds.Policy)
│   ├── MultiFeedResolutionPolicyTests.cs        ← using updates
│   ├── MultiFeedRetryPolicyTests.cs             ← using updates
│   ├── MultiFeedTieBreakRegressionTests.cs      ← using updates
│   ├── RemoteFeedDownloadContractTests.cs       ← using updates
│   ├── LocalDirectoryFeedContractTests.cs       ← using updates
│   └── [remaining tests STAY]
└── Sources/                                    # EXPANDED with moved test files
```

**Structure Decision**: The existing `Nuplane.Runtime` project structure is reorganized in-place. No new projects are created. Three new folders (`Feeds/`, `Feeds/Configuration/`, `Feeds/Policy/`) are created; two folders (`Desired/`, `Reconciliation/FeedPolicy/`) are removed. Test project mirrors the source reorganization for directly-testing files.

