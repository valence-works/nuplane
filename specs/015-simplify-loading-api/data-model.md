# Data Model — Loading & Query API Simplification

## Entity: ActivePackage
- Purpose: The canonical host-facing representation of one currently active reconciled package.
- Fields:
  - `packageId` (string, required, case-insensitive unique within the snapshot)
  - `version` (string, required)
  - `feedName` (string, optional)
  - `sourceName` (string, optional)
  - `installPath` (string, required)
  - `activatedAtUtc` (DateTimeOffset, required)
  - `activationCorrelationId` (string, required)
- Validation rules:
  - Only the currently active reconciled version may appear.
  - Provenance fields must originate from trusted reconcile inputs already accepted by Nuplane.
  - `installPath` points at the active package location, not retained rollback content.

## Entity: ActivePackagesSnapshot
- Purpose: A deterministic point-in-time view of the current active package inventory.
- Fields:
  - `snapshotAtUtc` (DateTimeOffset, required)
  - `persistedAtUtc` (DateTimeOffset, required)
  - `packages` (list<ActivePackage>, required)
  - `correlationId` (string, required)
- Validation rules:
  - `packages` must be deterministically ordered by `packageId` and `version`.
  - Reads must succeed after restart from persisted state without observer replay or directory crawling.
  - Retained, failed, or removed versions never appear.

## Entity: PackageLoadStateSnapshot
- Purpose: The canonical host-facing read model for current-process loading availability and per-package load state.
- Fields:
  - `availability` (enum: `Disabled`, `Stale`, `Available`, required)
  - `snapshotAtUtc` (DateTimeOffset, required)
  - `refreshedAtUtc` (DateTimeOffset, optional)
  - `packages` (list<PackageLoadState>, required)
  - `reason` (string, optional machine-readable explanation)
  - `correlationId` (string, required)
- Validation rules:
  - The snapshot always projects over the active package set, never over arbitrary retained store contents.
  - `availability = Stale` means the current process has not refreshed loading data yet.
  - Snapshot-level availability must not be inferred from stale or failed package entries alone.

## Entity: PackageLoadState
- Purpose: The load-state description for one active package.
- Fields:
  - `packageId` (string, required)
  - `version` (string, required)
  - `status` (enum: `Disabled`, `Stale`, `Loaded`, `Failed`, required)
  - `installPath` (string, required)
  - `loadedAtUtc` (DateTimeOffset, optional)
  - `diagnostics` (list<string>, required)
  - `assemblyReferences` (list<PackageAssemblyReference>, required)
- Validation rules:
  - Each record must correspond to an `ActivePackage` with the same `packageId`/`version`.
  - `Failed` and `Stale` load state never remove the package from active inventory.
  - Diagnostics must be correlation-friendly and secret-safe.
  - Public load-state models must not expose `Assembly`, `Type`, or other unload-sensitive runtime objects.

## Entity: PackageAssemblyReference
- Purpose: A durable, serializable description of an assembly associated with an active package.
- Fields:
  - `assemblyPath` (string, required)
  - `assemblyFileName` (string, required)
  - `targetFrameworkMoniker` (string, optional)
  - `kind` (string, required, e.g. `PrimaryLoadAssembly`, `AdditionalManagedAssembly`)
  - `selectionReason` (string, required)
- Validation rules:
  - Paths must remain under the active package install path.
  - Ordering must be deterministic for repeated identical reconcile inputs.
  - The model must not encode discovered plugin/application semantics.

## Entity: PackageAssemblies
- Purpose: The in-process runtime assembly collection Nuplane exposes for one active loaded package.
- Fields:
  - `packageId` (string, required)
  - `version` (string, required)
  - `assemblies` (list<Assembly>, required runtime-only)
  - `assemblyReferences` (list<PackageAssemblyReference>, required)
- Validation rules:
  - The entity is valid only for active packages whose load-state status is `Loaded`.
  - `assemblies` may be consumed only in-process and must not be serialized or remotely exposed.
  - Callers must treat all returned runtime objects as immediate-use/no-cache values because they are unload-sensitive.

## Entity: OptionalTypeFinderResult
- Purpose: A transient runtime-only collection of matching types discovered from one active package’s assemblies.
- Fields:
  - `packageId` (string, required)
  - `types` (list<Type>, required runtime-only)
- Validation rules:
  - Results are best-effort and skip uninspectable assemblies or types without failing the whole query.
  - The result is valid only for the current active loaded package version of the requested package ID.
  - `Type` instances and derived reflection artifacts must not be cached beyond the current reconciliation cycle.

## Entity: SurfaceDisposition
- Purpose: The maintained classification and simplification decision for one current loading/query construct.
- Fields:
  - `currentSurface` (string, required)
  - `canonicalSurface` (string, optional when removed)
  - `classification` (enum: `DefaultPublic`, `SecondaryPublic`, `AdvancedOnlyPublic`, `Internal`, `MergeRefactor`, `Remove`, required)
  - `ownerPackage` (string, required)
  - `reason` (string, required)
- Validation rules:
  - Every materially relevant public or internal loading/query construct must have exactly one disposition.
  - A construct classified `AdvancedOnlyPublic` must have an explicit support boundary and justification.
  - A removed or internalized construct must not survive only as an alias or pass-through compatibility layer.

## Relationships
- `ActivePackagesSnapshot` contains zero or more `ActivePackage` records.
- `PackageLoadStateSnapshot` projects over `ActivePackagesSnapshot` and contains one `PackageLoadState` per relevant active package.
- `PackageLoadState` contains zero or more `PackageAssemblyReference` records.
- `PackageAssemblies` represents the runtime-only assembly materialization for a `PackageLoadState` whose status is `Loaded`.
- `OptionalTypeFinderResult` is derived from `PackageAssemblies` and never becomes a durable or remotely exposed model.
- `SurfaceDisposition` governs the keep/rename/remove/internalize decisions for the full loading/query architecture.

## State Transitions

### Active package inventory lifecycle
1. `ResolvedPackagePendingActivation`
2. `TransactionalActivationSucceeded`
3. `ActivePackagePersisted`
4. `ActivePackagesSnapshotVisible`
5. `SupersededOrRemoved`
6. `RetainedOnDiskButExcludedFromInventory`

### Load-state lifecycle
1. `LoadingModuleAbsent` → no load-state service or route is composed
2. `LoadingInstalledButDisabled` → snapshot availability `Disabled`
3. `ProcessRestartedWithoutRefresh` → snapshot availability `Stale`
4. `CurrentProcessRefreshSucceeded` → package states become `Loaded` or `Failed`, snapshot availability `Available`
5. `ActiveSetChanges` → load state re-projects over the latest active package set

### Assembly access lifecycle
1. `PackageActiveButNotLoaded`
2. `PackageLoadStateLoaded`
3. `PackageAssembliesAvailableForImmediateUse`
4. `CallerReleasesRuntimeObjects`
5. `PackageContextCanUnload`

### Optional type finding lifecycle
1. `HostQueriesAssembliesFirst`
2. `HostInvokesOptionalTypeFinder`
3. `MatchingTypesReturnedBestEffort`
4. `HostConsumesTypesImmediately`
5. `RuntimeObjectsReleasedBeforeFutureUnload`

