# Data Model: Runtime Folder & Namespace Reorganization

**Branch**: `010-runtime-folder-reorganization` | **Date**: 2026-03-07

This feature introduces no new types. It relocates existing types into new folder/namespace
groupings. This document catalogs every entity affected, its current and target location,
and the namespace transition.

---

## Move 1: Feed Acquisition Types → `Feeds/`

### Files Moving from `Reconciliation/` to `Feeds/`

| File | Current Namespace | Target Folder | Target Namespace |
|------|------------------|---------------|------------------|
| `MultiFeedPackageResolver.cs` | `Nuplane.Runtime.Reconciliation` | `Feeds/` | `Nuplane.Runtime.Feeds` |
| `NuGetRemotePackageAcquirer.cs` | `Nuplane.Runtime.Reconciliation` | `Feeds/` | `Nuplane.Runtime.Feeds` |
| `NuGetPackageResolver.cs` | `Nuplane.Runtime.Reconciliation` | `Feeds/` | `Nuplane.Runtime.Feeds` |
| `INuGetPackageResolver.cs` | `Nuplane.Runtime.Reconciliation` | `Feeds/` | `Nuplane.Runtime.Feeds` |
| `NoEligibleFeedException.cs` | `Nuplane.Runtime.Reconciliation` | `Feeds/` | `Nuplane.Runtime.Feeds` |
| `AcquisitionOutcomeEntry.cs` | `Nuplane.Runtime.Reconciliation` | `Feeds/` | `Nuplane.Runtime.Feeds` |

### Files Moving from `Reconciliation/FeedPolicy/` to `Feeds/Policy/`

| File | Current Namespace | Target Folder | Target Namespace |
|------|------------------|---------------|------------------|
| `FeedResolutionPolicy.cs` | `Nuplane.Runtime.Reconciliation.FeedPolicy` | `Feeds/Policy/` | `Nuplane.Runtime.Feeds.Policy` |
| `FeedUnavailableException.cs` | `Nuplane.Runtime.Reconciliation.FeedPolicy` | `Feeds/Policy/` | `Nuplane.Runtime.Feeds.Policy` |

### Files Moving from `Configuration/` to `Feeds/Configuration/`

| File | Current Namespace | Target Folder | Target Namespace |
|------|------------------|---------------|------------------|
| `FeedResolutionOptions.cs` | `Nuplane.Runtime.Configuration` | `Feeds/Configuration/` | `Nuplane.Runtime.Feeds.Configuration` |
| `FeedResolutionPolicyMode.cs` | `Nuplane.Runtime.Configuration` | `Feeds/Configuration/` | `Nuplane.Runtime.Feeds.Configuration` |
| `FeedCredentialOptionsValidator.cs` | `Nuplane.Runtime.Configuration` | `Feeds/Configuration/` | `Nuplane.Runtime.Feeds.Configuration` |

### Files Staying in `Trust/Feeds/` — Namespace-Only Changes

| File | Current Namespace | Target Namespace |
|------|------------------|------------------|
| `FeedTrustPolicyEvaluator.cs` | `Nuplane.Runtime.Reconciliation.FeedPolicy` | `Nuplane.Runtime.Feeds.Policy` |
| `UntrustedOverridePolicy.cs` | `Nuplane.Runtime.Reconciliation.FeedPolicy` | `Nuplane.Runtime.Feeds.Policy` |
| `IFeedTrustPolicyEvaluator.cs` | `Nuplane.Runtime.Reconciliation.FeedPolicy` | `Nuplane.Runtime.Feeds.Policy` |
| `RestrictedFeedValidatorPipeline.cs` | `Nuplane.Runtime.Reconciliation.FeedPolicy` | `Nuplane.Runtime.Feeds.Policy` |
| `FeedTrustPolicyOutcome.cs` | `Nuplane.Runtime.Reconciliation.Models` | `Nuplane.Runtime.Feeds.Policy` |
| `FeedTrustPolicyOptions.cs` | `Nuplane.Runtime.Configuration` | `Nuplane.Runtime.Feeds.Configuration` |

### Folders Created/Removed (Move 1)

- **Created**: `Feeds/`, `Feeds/Policy/`, `Feeds/Configuration/`
- **Removed**: `Reconciliation/FeedPolicy/` (FR-009 — empty after moves)

### File Staying in `Configuration/`

| File | Namespace | Notes |
|------|-----------|-------|
| `ManifestOptions.cs` | `Nuplane.Runtime.Configuration` | FR-013: stays unchanged |

---

## Move 2: Desired-State Source Types → `Sources/`

### Files Moving from `Desired/` to `Sources/`

| File | Current Namespace | Target Namespace |
|------|------------------|------------------|
| `DesiredManifestPackageSource.cs` | `Nuplane.Runtime.Desired` | `Nuplane.Runtime.Sources` |
| `DesiredManifestReader.cs` | `Nuplane.Runtime.Desired` | `Nuplane.Runtime.Sources` |

### Files Moving from `Reconciliation/` to `Sources/`

| File | Current Namespace | Target Namespace |
|------|------------------|------------------|
| `DesiredStateAggregator.cs` | `Nuplane.Runtime.Reconciliation` | `Nuplane.Runtime.Sources` |
| `IDesiredStateAggregator.cs` | `Nuplane.Runtime.Reconciliation` | `Nuplane.Runtime.Sources` |

### Files Moving from `Reconciliation/Models/` to `Sources/`

| File | Current Namespace | Target Namespace |
|------|------------------|------------------|
| `StaticDesiredSource.cs` | `Nuplane.Runtime.Reconciliation.Models` | `Nuplane.Runtime.Sources` |
| `DesiredAggregateResult.cs` | `Nuplane.Runtime.Reconciliation.Models` | `Nuplane.Runtime.Sources` |
| `DesiredReadResult.cs` | `Nuplane.Runtime.Reconciliation.Models` | `Nuplane.Runtime.Sources` |

### Folders Created/Removed (Move 2)

- **Removed**: `Desired/` (FR-008 — empty after moves)

### Existing Files in `Sources/` (unchanged)

| File | Namespace | Notes |
|------|-----------|-------|
| `DesiredSourceSnapshotCache.cs` | `Nuplane.Runtime.Sources` | Already in place |
| `FeedRuleDesiredSource.cs` | `Nuplane.Runtime.Sources` | Already in place |
| `FeedRuleResultSelector.cs` | `Nuplane.Runtime.Sources` | Already in place |

---

## Move 3: Trust Gate Types → `Trust/`

### Files Moving from `Reconciliation/` to `Trust/`

| File | Current Namespace | Target Namespace |
|------|------------------|------------------|
| `AllowlistGate.cs` | `Nuplane.Runtime.Reconciliation` | `Nuplane.Runtime.Trust` |
| `IAllowlistGate.cs` | `Nuplane.Runtime.Reconciliation` | `Nuplane.Runtime.Trust` |

---

## Namespace Transition Map

Summary of all namespace changes for `using` statement updates:

| Old Namespace | New Namespace | Fully Retired? |
|---------------|---------------|----------------|
| `Nuplane.Runtime.Reconciliation` (feed types only) | `Nuplane.Runtime.Feeds` | NO — orchestration types remain |
| `Nuplane.Runtime.Reconciliation` (source types only) | `Nuplane.Runtime.Sources` | NO — orchestration types remain |
| `Nuplane.Runtime.Reconciliation` (trust gate types only) | `Nuplane.Runtime.Trust` | NO — orchestration types remain |
| `Nuplane.Runtime.Reconciliation.FeedPolicy` | `Nuplane.Runtime.Feeds.Policy` | YES (SC-005) |
| `Nuplane.Runtime.Reconciliation.Models` (3 desired-state types only) | `Nuplane.Runtime.Sources` | NO — 10 model files remain |
| `Nuplane.Runtime.Reconciliation.Models` (FeedTrustPolicyOutcome) | `Nuplane.Runtime.Feeds.Policy` | NO — 10 model files remain |
| `Nuplane.Runtime.Desired` | `Nuplane.Runtime.Sources` | YES (SC-005) |
| `Nuplane.Runtime.Configuration` (3 feed config types) | `Nuplane.Runtime.Feeds.Configuration` | NO — ManifestOptions remains |
| `Nuplane.Runtime.Configuration` (FeedTrustPolicyOptions) | `Nuplane.Runtime.Feeds.Configuration` | NO — ManifestOptions remains |

---

## Remaining `Reconciliation/` Types (Stay in Place)

These files remain in `Reconciliation/` with their existing namespaces:

| File | Namespace |
|------|-----------|
| `ReconciliationService.cs` | `Nuplane.Runtime.Reconciliation` |
| `PackageApplyExecutor.cs` | `Nuplane.Runtime.Reconciliation` |
| `IPackageApplyExecutor.cs` | `Nuplane.Runtime.Reconciliation` |
| `DesiredActualDiffEngine.cs` | `Nuplane.Runtime.Reconciliation` |
| `IDesiredActualDiffEngine.cs` | `Nuplane.Runtime.Reconciliation` |
| `DryRunPlanner.cs` | `Nuplane.Runtime.Reconciliation` |
| `IDryRunPlanner.cs` | `Nuplane.Runtime.Reconciliation` |
| `IReconciliationService.cs` | `Nuplane.Runtime.Reconciliation` |
| `IReconciliationRetryPolicy.cs` | `Nuplane.Runtime.Reconciliation` |
| `ReconciliationRetryPolicy.cs` | `Nuplane.Runtime.Reconciliation` |
| `ReconciliationRollbackCoordinator.cs` | `Nuplane.Runtime.Reconciliation` |
| `RollbackResult.cs` | `Nuplane.Runtime.Reconciliation` |
| `ILockFileCoordinator.cs` | `Nuplane.Runtime.Reconciliation` |
| `LockFileCoordinator.cs` | `Nuplane.Runtime.Reconciliation` |
| `LockFileStore.cs` | `Nuplane.Runtime.Reconciliation` |
| `ManualReconcileCoordinator.cs` | `Nuplane.Runtime.Reconciliation` |
| `ManualReconcileOutcome.cs` | `Nuplane.Runtime.Reconciliation` |
| `ManualReconcileOutcomeCode.cs` | `Nuplane.Runtime.Reconciliation` |
| `NoOpPackageLoader.cs` | `Nuplane.Runtime.Reconciliation` |
| `NoOpPackageUnloadCoordinator.cs` | `Nuplane.Runtime.Reconciliation` |
| `IReconciliationTriggerIngress.cs` | `Nuplane.Runtime.Reconciliation` |
| `Configuration/ReconciliationOptions.cs` | `Nuplane.Runtime.Reconciliation.Configuration` |
| `Convergence/*.cs` | `Nuplane.Runtime.Reconciliation.Convergence` |
| `LockFile/*.cs` | `Nuplane.Runtime.Reconciliation.LockFile` |
| `Middleware/*.cs` | `Nuplane.Runtime.Reconciliation.Middleware` |
| `Models/` (10 remaining) | `Nuplane.Runtime.Reconciliation.Models` |

---

## Test File Moves

### Tests Moving from `Desired/` to `Sources/`

| Test File | Tests Type |
|-----------|------------|
| `DesiredAggregationContractTests.cs` | `DesiredStateAggregator` |
| `DesiredAggregationDeterminismTests.cs` | `DesiredStateAggregator` |
| `DesiredAggregationDuplicateRegressionTests.cs` | `DesiredStateAggregator` |
| `DesiredManifestParserTests.cs` | `DesiredManifestReader` |
| `DesiredManifestProjectionDeterminismTests.cs` | `DesiredManifestPackageSource` |

### Tests Moving from `Reconciliation/` to Other Folders

| Test File | Target Folder | Reason |
|-----------|---------------|--------|
| `AllowlistGateTests.cs` | `Trust/` | Tests `AllowlistGate` which moves to Trust |
| `DesiredStateAggregatorTests.cs` | `Sources/` | Tests `DesiredStateAggregator` which moves to Sources |

### Tests Staying in `Reconciliation/` — `using` Updates Only

| Test File | New `using` Needed |
|-----------|--------------------|
| `FeedTrustPolicyEvaluatorTests.cs` | `Nuplane.Runtime.Feeds.Policy` (replaces `Reconciliation.FeedPolicy`) |
| `MultiFeedResolutionPolicyTests.cs` | `Nuplane.Runtime.Feeds` + `Nuplane.Runtime.Feeds.Policy` |
| `MultiFeedRetryPolicyTests.cs` | `Nuplane.Runtime.Feeds` |
| `MultiFeedTieBreakRegressionTests.cs` | `Nuplane.Runtime.Feeds` + `Nuplane.Runtime.Feeds.Policy` |
| `RemoteFeedDownloadContractTests.cs` | `Nuplane.Runtime.Feeds` + `Nuplane.Runtime.Feeds.Policy` |
| `LocalDirectoryFeedContractTests.cs` | `Nuplane.Runtime.Feeds` + `Nuplane.Runtime.Feeds.Policy` |

### Tests in `Configuration/` — `using` Updates Only

| Test File | Change |
|-----------|--------|
| `FeedCredentialOptionsValidatorTests.cs` | `using Nuplane.Runtime.Feeds.Configuration` (replaces `Configuration`) |

