# Research: Loading & Query API Simplification

**Branch**: `015-simplify-loading-api` | **Date**: 2026-04-11

## D-001 — Canonical host taxonomy should map directly to the existing read intent

- **Decision**: Keep `IActivePackageCatalog` as the inventory anchor, rename `IActivePackageCatalog.GetSnapshotAsync` to `GetActivePackagesAsync`, rename `ActivePackageCatalogSnapshot` to `ActivePackagesSnapshot`, rename `ActivePackageDescriptor` to `ActivePackage`, and rename the loading-catalog family to explicit load-state terms (`IPackageLoadStateCatalog`, `GetLoadStateAsync`, `PackageLoadStateSnapshot`, `PackageLoadState`, `PackageLoadStatus`).
- **Rationale**: The current public nouns already separate inventory from loading, but hosts still have to learn mechanics-first terms such as "catalog snapshot" and "descriptor". The simplification goal is best served by keeping the right top-level anchors while renaming models/methods to the thing the host is actually asking for.
- **Alternatives considered**:
  - Rename `IActivePackageCatalog` as well (rejected: the spec explicitly preserves it as the canonical top-level inventory service).
  - Keep the current loading-catalog names and only adjust docs (rejected: the spec requires simplification across contracts, models, routes, and supporting terminology, not documentation only).

## D-002 — Public loading/query surfaces should stop at the four intended host concepts

- **Decision**: Keep only four host-facing concepts public: active packages, load state, assemblies, and optional type finding. `IPackageAssemblyCatalog` remains the default loading-enabled surface; `IPackageTypeScanner` is renamed to `IPackageTypeFinder` and remains public only as a secondary convenience surface; public provider-style and exact-version mechanics are removed rather than retained as advanced escape hatches.
- **Rationale**: The spec clarifications explicitly reject keeping exact-version/provider mechanics public. Keeping `IPackageTypeFinder` public but clearly secondary preserves the promised four-concept mental model while preventing type discovery from becoming the required base model.
- **Alternatives considered**:
  - Keep one or more advanced-only exact-version surfaces (rejected: contradicts the accepted clarification and retains unnecessary host-facing mechanics).
  - Internalize type finding completely (rejected: conflicts with the clarified requirement that optional type finding remains one of the four teachable concepts).

## D-003 — Assembly access should stay runtime-only and remove exact-version overloads from the public contract

- **Decision**: Keep `IPackageAssemblyCatalog` public, keep it centered on the current active package set, rename `PackageAssemblyCatalogEntry` to `PackageAssemblies`, rename `AssemblyScanCandidate` to `PackageAssemblyReference`, and remove public exact-version assembly methods so hosts can query all active loaded packages or the current active loaded version for a package ID only.
- **Rationale**: The current catalog already frames assemblies as a convenience over active loaded packages, which matches the intended host model. Public exact-version overloads pull the API back toward mechanics-first thinking and contradict the clarified "no exact-version/provider public mechanics" direction.
- **Alternatives considered**:
  - Keep exact-version overloads on `IPackageAssemblyCatalog` because they live on a canonical surface (rejected: still exposes exact-version public mechanics and complicates the default mental model).
  - Replace `IPackageAssemblyCatalog` with a new assembly provider abstraction (rejected: reintroduces provider mechanics as a first-class concept).

## D-004 — Type finding should become a secondary, assembly-first convenience contract

- **Decision**: Rename `IPackageTypeScanner` to `IPackageTypeFinder`, keep the `FindTypesAsync` verb, retain only active-package-based async overloads, and remove synchronous exact-version methods from the public contract. `IPackageTypeFinder` will operate over `IPackageAssemblyCatalog` semantics rather than over a separate exact-version provider surface.
- **Rationale**: The current implementation already treats type scanning as a convenience over assembly access, but the interface still advertises scanner language and exact-version/provider-based methods. Moving to an async, active-package-only contract keeps the feature public while teaching hosts to start from assemblies.
- **Alternatives considered**:
  - Keep both async and sync exact-version methods for backward familiarity (rejected: the feature is not constrained by backward compatibility and the extra methods preserve unnecessary mechanics).
  - Rename the method verb as well (rejected: the spec explicitly preserves `FindTypesAsync` as the action hosts already understand).

## D-005 — Low-level loading mechanics should become internal runtime infrastructure unless a distinct safety boundary remains

- **Decision**: Internalize or collapse `IPackageAssemblyProvider`, `IPackageLoader`, `IPackageUnloadCoordinator`, `ILoadingEventDispatcher`, `IPackageLoadingObserver`, `ILoadingFailureTracker`, `PackageLoadSession`, `PackageLoadContextHandle`, `PackageLoadResult`, `DeactivationAttempt`, `UnloadOutcome`, and `UnloadOutcomeRecord` unless implementation work proves that a narrower internal seam is still required.
- **Rationale**: These types describe orchestration, bookkeeping, or event fan-out rather than host intent. The feature explicitly allows public and internal constructs to be removed, merged, renamed, refactored, or internalized, and the clarified public taxonomy no longer needs these mechanics exposed.
- **Alternatives considered**:
  - Keep the abstractions public for future extensibility (rejected: public complexity without a current host scenario violates the simplification goal).
  - Keep the abstractions but hide them from docs (rejected: still leaves the architecture and support surface more complex than necessary).

## D-006 — Loading-owned HTTP composition should adopt load-state terminology while core admin stays loading-free

- **Decision**: Rename `MapNuplaneLoading` to `MapNuplaneLoadState` and rename the loading-owned route from `GET /nuplane/admin/loading` to `GET /nuplane/admin/load-state`. Core admin remains responsible only for packages, state, and reconcile routes and must not introduce loading-specific wrappers or availability DTOs.
- **Rationale**: Route and composition naming materially shape the mental model. Renaming the loading-owned route is necessary to make operator guidance use the same canonical terms as the in-process contracts while preserving the clean admin/loading separation established by feature 014.
- **Alternatives considered**:
  - Keep the existing route name for continuity (rejected: leaves a contradictory naming state and fails the spec’s consistency requirement).
  - Move the load-state route into core admin (rejected: would re-couple core admin to optional loading concepts).

## D-007 — Unload-sensitive runtime objects should stay confined to in-process convenience surfaces

- **Decision**: Durable, serializable, or remotely exposed models use `PackageAssemblyReference` only; `Assembly`, `Type`, and derived reflection artifacts remain limited to in-process convenience surfaces such as `IPackageAssemblyCatalog` and `IPackageTypeFinder`, and every such contract must document immediate-use/no-caching guidance.
- **Rationale**: The current contracts already warn about collectible `AssemblyLoadContext` behavior. The simplification work must preserve that safety boundary while making it much clearer which models are safe for persistence or remote transport versus which are runtime-only conveniences.
- **Alternatives considered**:
  - Return `Assembly` or `Type` objects in load-state/admin DTOs for convenience (rejected: violates unload-safety and the spec’s durable-model restrictions).
  - Remove runtime assembly/type convenience entirely (rejected: the feature explicitly keeps assemblies and optional type finding as host-facing concepts).

## D-008 — Validation should favor clean-break renames/removals over compatibility bridges

- **Decision**: Plan implementation as a clean break: rename/remove outdated public contracts and route names directly, update samples/docs/tests in the same change, and avoid compatibility aliases, duplicate DTOs, staged deprecations, or bridge layers whose only purpose is vocabulary preservation.
- **Rationale**: The spec explicitly says backward compatibility is not required. A clean break is the only reliable way to prevent contradictory public guidance and lingering pass-through layers.
- **Alternatives considered**:
  - Keep temporary aliases for old names (rejected: creates the exact parallel vocabulary the feature is trying to remove).
  - Stage the cleanup over multiple features (rejected: prolongs the conflicting architecture and complicates tests/docs immediately).

