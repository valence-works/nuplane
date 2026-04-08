# Research: Queryable Package Catalog

**Branch**: `014-query-package-catalog` | **Date**: 2026-04-08

## D-001 — Persist the active package catalog as first-class store state

- **Decision**: Extend the persisted store-state payload so the durable active package inventory is a full descriptor set, not just `packageId -> version`. Each persisted entry should capture package identity, active version, trusted provenance (`FeedName` and source context when present), install path, and activation timestamp/correlation so the active catalog can be read immediately after restart without replaying observers or re-walking the store.
- **Rationale**: `IStoreRegistry` already owns the atomic persistence boundary that follows successful activation. Reusing that boundary preserves deterministic semantics and restart recovery while avoiding a second read model that could drift from the actual active pointer set.
- **Alternatives considered**:
  - Reconstruct the catalog from `current/` links or package folders on every read (rejected: expensive, couples readers to store layout, and still does not provide activation timing/provenance cleanly).
  - Rebuild the catalog from observer history (rejected: violates the query-first requirement and fails restart recovery).

## D-002 — Keep catalog contracts in abstraction packages and implementations in owning runtime packages

- **Decision**: Define the active package catalog interface and pure read-model types in `src/Nuplane.Abstractions`, implemented by the core runtime in `src/Nuplane`. Define the loading catalog interface and pure loading read-model types in `src/Nuplane.Loading.Abstractions`, implemented by `src/Nuplane.Loading`.
- **Rationale**: This matches the repository boundary rules: abstraction packages hold stable, implementation-agnostic contracts, while runtime packages own the concrete composition. Hosts can depend on the smallest possible package for query contracts, and admin/sample surfaces can compose those same services without becoming the primary contract.
- **Alternatives considered**:
  - Place catalog interfaces only in `src/Nuplane` and `src/Nuplane.Loading` (rejected: forces hosts to reference implementation packages just to consume contracts).
  - Put both active and loading contracts into `src/Nuplane.Abstractions` (rejected: would pull optional loading concepts into the core abstraction boundary).

## D-003 — Split operational state from package inventory instead of overloading the current snapshot

- **Decision**: Refactor the current operational read model so operational state becomes its own state-only snapshot, while active package inventory and loading inventory become separate query surfaces. Admin and remote/operator APIs should expose distinct reads for packages, loading, and state, and any compatibility alias must remain secondary.
- **Rationale**: The current `OperationalSnapshot` mixes active packages with health and reconcile status. The feature spec explicitly requires package truth, loading truth, and operational truth to remain separate so later delivery stages do not redefine what “active” means.
- **Alternatives considered**:
  - Add more fields to the existing operational snapshot (rejected: keeps unrelated concerns coupled and makes admin consumers parse package inventory when they only need health/state).
  - Make admin endpoints the only new contract (rejected: violates the requirement for standalone host-facing services).

## D-004 — Build the loading catalog from active packages plus current-process loader state

- **Decision**: Implement the loading catalog as a projection over the active package catalog, current-process loader sessions, loading failures, and a current-process refresh marker. The standalone loading catalog service should report `Disabled`, `Stale`, or `Available` at the snapshot level, while each active package gets its own loading status such as `Loaded`, `Failed`, `Disabled`, or `Stale`. Admin composition can additionally translate “loading module not installed” into `Unavailable`.
- **Rationale**: Loading data is process-local and is not durable across restart the way active package state is. A refresh marker cleanly distinguishes “module installed but not yet refreshed in this process” from “loading disabled” and from “module absent entirely.”
- **Alternatives considered**:
  - Persist loader sessions and treat them as restart-authoritative (rejected: would misrepresent in-memory load contexts after process restart).
  - Register a core no-op loading catalog when the module is absent (rejected: the spec explicitly forbids this).

## D-005 — Reuse the loader’s framework-selection logic to produce scan candidates

- **Decision**: The loading catalog should expose assembly scan candidates derived from the same framework-compatible asset-selection rules already used by `PackageLoader`. Each candidate should include the assembly path, file name, target framework context when known, and a deterministic selection reason. The catalog must not expose discovered type identities.
- **Rationale**: Hosts need Nuplane to tell them which assemblies are appropriate to scan without forcing them to crawl package folders or reimplement target-framework asset selection. Reusing loader logic keeps the catalog authoritative and deterministic.
- **Alternatives considered**:
  - Expose every `.dll` under the install path (rejected: pushes asset-selection ambiguity back to the host).
  - Expose discovered plugin/module types directly (rejected: violates the host-neutral boundary and the explicit requirement that discovery remains host-owned).

## D-006 — Shift the sample and docs to a query-first integration model

- **Decision**: Update `samples/Nuplane.Sample.AspNetCore` and repository docs so hosts demonstrate querying the active package catalog and loading catalog directly, then passing scan candidates into host-owned discovery via `IPackageTypeScanner`. Observer callbacks remain supplemental invalidation/logging hooks rather than the only state source.
- **Rationale**: The sample is the clearest downstream proof that hosts can build menus, diagnostics, and discovery flows from query surfaces alone. It also satisfies the requirement that scanners such as CShells can integrate without browsing store folders.
- **Alternatives considered**:
  - Keep the current observer-only sample and document the query model separately (rejected: leaves the most visible integration path behind the new architecture).
  - Replace observers entirely (rejected: the spec keeps them as supplemental invalidation signals).

## D-007 — Add catalog-specific observability and verification at the same boundaries as the new read models

- **Decision**: Add structured logs, metrics, and health/degraded signals for active catalog persistence/reads, loading catalog availability (`Disabled`, `Stale`, `Available`, `Unavailable`), and package-versus-loading divergence. Cover the feature with runtime, store, loading, admin/API, restart, and sample validation tests.
- **Rationale**: The constitution requires operability and regression coverage for boundary changes. Query surfaces become part of the operational contract, so they need first-class evidence just like reconcile and store flows already do.
- **Alternatives considered**:
  - Rely on existing reconcile telemetry only (rejected: it would not tell operators whether catalog reads are stale, unavailable, or inconsistent).
  - Validate only through sample/manual checks (rejected: insufficient for public contract changes and restart edge cases).

