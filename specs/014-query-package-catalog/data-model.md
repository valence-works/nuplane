# Data Model — Queryable Package Catalog

## Entity: ActivePackageDescriptor
- Purpose: The authoritative host-facing description of one currently active reconciled package.
- Fields:
  - `packageId` (string, required, case-insensitive unique within snapshot)
  - `version` (string, required)
  - `feedName` (string, optional when provenance is source-only)
  - `sourceName` (string, optional)
  - `installPath` (string, required)
  - `activatedAtUtc` (DateTimeOffset, required)
  - `activationCorrelationId` (string, required)
- Validation rules:
  - The descriptor may exist only for the version currently considered active by reconciliation.
  - `installPath` must point at the active installed package location, not a retained rollback copy.
  - `activatedAtUtc` records activation/publication time, not package file creation time.
  - Feed/source provenance must originate from trusted resolution inputs already accepted by reconciliation.

## Entity: ActivePackageCatalogSnapshot
- Purpose: A consistent, point-in-time read model of all active package descriptors.
- Fields:
  - `snapshotAtUtc` (DateTimeOffset, required)
  - `persistedAtUtc` (DateTimeOffset, required)
  - `packages` (list<ActivePackageDescriptor>, required)
  - `correlationId` (string, optional for read tracing)
- Validation rules:
  - `packages` must be ordered deterministically by `packageId` and then `version`.
  - Only currently active packages may appear; retained, failed, and removed versions are excluded.
  - Snapshot creation must not require observer replay or file-system crawling.

## Entity: OperationalStateSnapshot
- Purpose: A separate operator-facing view of health and reconciliation state that does not define package availability.
- Fields:
  - `snapshotAtUtc` (DateTimeOffset, required)
  - `health` (enum: `Healthy`, `Degraded`, required)
  - `degradedReasons` (list<string>, required)
  - `lastReconcile` (LastReconcileOutcome, optional)
  - `correlationId` (string, required)
- Validation rules:
  - The snapshot must not embed the full active package catalog.
  - Degraded reasons must stay correlation-friendly and machine-readable.
  - Reads must remain available even when loading is absent or stale.

## Entity: LoadingCatalogSnapshot
- Purpose: The optional loading-module read model that reports loading availability and per-active-package loading guidance.
- Fields:
  - `availability` (enum: `Unavailable`, `Disabled`, `Stale`, `Available`, required)
  - `snapshotAtUtc` (DateTimeOffset, required)
  - `refreshedAtUtc` (DateTimeOffset, optional)
  - `packages` (list<LoadingPackageDescriptor>, required)
  - `reason` (string, optional)
  - `correlationId` (string, optional)
- Validation rules:
  - Standalone loading services exposed directly to hosts use `Disabled`, `Stale`, or `Available`; `Unavailable` is reserved for admin/operator compositions when the loading module is not installed.
  - `packages` must align to the active package catalog, never to arbitrary retained store contents.
  - `availability = Stale` means the current process has not refreshed loading data yet; stale data is not treated as current success.

## Entity: LoadingPackageDescriptor
- Purpose: The loading view for one active package.
- Fields:
  - `packageId` (string, required)
  - `version` (string, required)
  - `loadingStatus` (enum: `Disabled`, `Stale`, `Loaded`, `Failed`, required)
  - `activeInstallPath` (string, required)
  - `loadedAtUtc` (DateTimeOffset, optional)
  - `diagnostics` (list<string>, required)
  - `scanCandidates` (list<AssemblyScanCandidate>, required)
  - `contextKey` (string, optional)
- Validation rules:
  - The descriptor must correspond to an active package descriptor with the same package identity/version.
  - `scanCandidates` are populated only when the package has current-process loading guidance available.
  - `Failed` loading status must not remove the package from the active package catalog.
  - `diagnostics` must explain failure/stale/disabled reasons without exposing secrets.

## Entity: AssemblyScanCandidate
- Purpose: A deterministic host-facing recommendation for assembly discovery/scanning.
- Fields:
  - `assemblyPath` (string, required)
  - `assemblyFileName` (string, required)
  - `targetFrameworkMoniker` (string, optional)
  - `candidateKind` (enum: `PrimaryLoadAssembly`, `AdditionalManagedAssembly`, required)
  - `selectionReason` (string, required)
- Validation rules:
  - Candidate paths must remain under the active package install path.
  - Ordering must be deterministic so repeated identical reconciliations return the same candidate list.
  - Candidates must never include discovered type/plugin/module identities.

## Entity: LoadingRefreshMarker
- Purpose: Tracks whether loading data belongs to the current process instance.
- Fields:
  - `processInstanceId` (string, required)
  - `lastRefreshCorrelationId` (string, optional)
  - `lastRefreshAtUtc` (DateTimeOffset, optional)
  - `loadingEnabled` (bool, required)
  - `moduleInstalled` (bool, required)
- Validation rules:
  - A new process instance without a refresh produces `Stale` loading availability.
  - `moduleInstalled = false` maps to admin/operator `Unavailable`, not a core no-op service.
  - `loadingEnabled = false` maps to `Disabled` even when the module is installed.

## Relationships
- `ActivePackageCatalogSnapshot` contains one or more `ActivePackageDescriptor` records.
- `LoadingCatalogSnapshot` projects over the active package catalog and emits one `LoadingPackageDescriptor` per relevant active package.
- `LoadingPackageDescriptor` owns zero or more `AssemblyScanCandidate` records.
- `OperationalStateSnapshot` shares reconcile/health context with the catalogs but intentionally remains separate from package inventory.
- `LoadingRefreshMarker` governs the top-level availability of `LoadingCatalogSnapshot`.

## State Transitions

### Active package catalog lifecycle
1. `ResolvedPackagePendingActivation`
2. `TransactionalActivationSucceeded`
3. `ActiveDescriptorPersisted`
4. `SnapshotVisibleToHosts`
5. `SupersededOrRemoved`
6. `RetainedOnDiskButHiddenFromCatalog`

### Loading catalog lifecycle
1. `ModuleAbsent` → admin/operator projection reports `Unavailable`
2. `ModuleInstalledButDisabled` → snapshot availability `Disabled`
3. `ProcessRestartedWithoutRefresh` → snapshot availability `Stale`
4. `CurrentProcessRefreshSucceeded` → package statuses become `Loaded`/`Failed` and snapshot availability `Available`
5. `ReconcileChangesActiveSet` → loading snapshot re-projects against the latest active catalog

### Package-versus-loading consistency lifecycle
1. `PackageActive`
2. `LoadingNotAttemptedYet` (`Stale` or `Disabled`)
3. `LoadingSucceeded` with scan candidates available
4. `LoadingFailed` with diagnostics retained
5. `PackageRemovedFromActiveCatalog` → loading descriptor removed regardless of retained on-disk cleanup state

