# Feature Specification: Queryable Package Catalog

**Feature Branch**: `[014-query-package-catalog]`  
**Created**: 2026-04-08  
**Status**: Draft  
**Input**: User description: "Create a specification for a queryable package catalog and loading scan catalog for Nuplane"

## Clarifications

### Session 2026-04-08

- Q: What lifecycle statuses does the active package catalog expose? → A: The active package catalog contains only currently active packages; their lifecycle status is always Active, and rollback/failure nuance lives in separate operational or loading state surfaces.
- Q: How should hosts access the active package catalog? → A: Nuplane exposes a standalone active package catalog service in the core runtime for host code, and operator/admin surfaces compose that same catalog rather than being the only way to access package inventory.
- Q: How should hosts access the loading catalog? → A: Nuplane exposes a standalone loading catalog service owned by the optional loading module, and admin/operator surfaces compose that same loading catalog when the loading module is installed.
- Q: How far should the loading catalog go? → A: The loading catalog stops at loading status, diagnostics, and assembly scan candidates; discovered types remain host-owned and are not part of the catalog.
- Q: What happens when the optional loading module is not installed? → A: The loading catalog service exists only when the loading module is installed; when absent, admin/operator surfaces report loading as unavailable rather than exposing a core no-op loading service.

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Query Active Reconciled Packages (Priority: P1)

As a host integrator, I want Nuplane to expose a queryable inventory of the packages it currently considers active so that my application can make decisions from authoritative runtime state without reconstructing that state from observer callbacks.

**Why this priority**: A durable active-package inventory is the foundational capability. It delivers immediate value even when optional loading is disabled and enables the host to build menus, configuration views, scanning lists, and diagnostics from a stable read model.

**Independent Test**: Can be fully tested by reconciling a known desired package set, querying the active package inventory, and verifying that the returned package list matches the active reconciled state across initial startup, a no-change reconcile, and a process restart with persisted state.

**Acceptance Scenarios**:

1. **Given** a host with reconciled packages, **When** the host queries Nuplane for active packages, **Then** it receives a consistent list of the packages currently active for host use, including the package identity, active version, source provenance, install location, and activation timing; only currently active packages appear in this inventory.
2. **Given** a host restart with persisted store state and no new reconciliation yet, **When** the host queries active packages, **Then** it receives the same authoritative active package inventory that existed before restart without replaying observer history.
3. **Given** that older inactive versions remain on disk for cleanup or rollback purposes, **When** the host queries active packages, **Then** only the active reconciled package set is returned as host-available inventory.

---

### User Story 2 - Query Loading and Scan Candidates Separately (Priority: P2)

As a host integrator using optional package loading, I want a separate queryable loading catalog that reports loading status and recommended assembly scan candidates so that scanners such as CShells can use Nuplane-managed packages without browsing store folders or inferring framework-specific assembly choices themselves.

**Why this priority**: This unlocks the CShells-style use case while preserving Nuplane's host-neutral boundary. It is lower priority than the core package catalog because it depends on optional loading, but it is the most valuable next slice once active package inventory exists.

**Independent Test**: Can be tested independently by enabling loading, reconciling at least one loadable package and one failing package, querying the loading catalog, and verifying that the response distinguishes successful, failed, stale, and unavailable loading states together with the corresponding scan candidates.

**Acceptance Scenarios**:

1. **Given** optional loading is enabled and a reconciled package is successfully loaded, **When** the host queries the loading catalog, **Then** the package appears with its loading status and the assembly candidates Nuplane recommends for host scanning.
2. **Given** optional loading is disabled, **When** the host queries the loading catalog, **Then** the result clearly indicates that loading data is unavailable or disabled rather than implying that no packages exist.
3. **Given** package activation succeeds but package loading fails, **When** the host queries Nuplane, **Then** the active package catalog still reports the package as active while the loading catalog reports the loading failure and does not misclassify the package as absent.
4. **Given** the repository sample host runs with optional loading enabled, **When** the sample queries Nuplane for active assembly scan candidates, **Then** it demonstrates how a host can enumerate those active assemblies and use them for host-owned type discovery without relying solely on observer callbacks.

---

### User Story 3 - Stage Delivery Without Redefining the Model (Priority: P3)

As a Nuplane maintainer, I want the package inventory, loading inventory, and operational state to be modeled as separate query surfaces so that the work can be delivered in stages without redefining the meaning of package availability at each stage.

**Why this priority**: The feature will span multiple implementation stages. Clear boundaries between package truth, loading truth, and operational truth reduce rework and make later admin and documentation work predictable.

**Independent Test**: Can be tested independently by delivering the core package catalog first, confirming that hosts can integrate with it without loading enabled, and then adding loading-specific and operator-facing read surfaces without changing the meaning of the original package inventory.

**Acceptance Scenarios**:

1. **Given** the core package catalog is available before any loading-specific work ships, **When** a host integrates against the core catalog, **Then** that host does not need later feature stages to reinterpret what constitutes an active Nuplane package.
2. **Given** operator-facing state and package inventory are both exposed, **When** a consumer queries for health or reconciliation state, **Then** that query is separate from the package inventory query and does not require the consumer to parse unrelated package details.
3. **Given** a process restart before loading data has been refreshed, **When** the host queries the separate surfaces, **Then** active packages remain queryable immediately while loading state is marked as stale or unavailable until refreshed for the current process.

### Edge Cases

- Loading is disabled, but the host still needs authoritative package inventory.
- Store cleanup retains inactive or last-known-good versions on disk, but those retained versions must not appear as active host-available packages.
- A reconciliation cycle completes package activation but the loading subsystem fails for one or more packages.
- A host queries inventory during a reconciliation cycle; the query must not surface a partially updated active set.
- The process restarts with persisted package state but without any re-established loading sessions.
- A package is removed from desired state while a previously retained version remains on disk for rollback or cleanup policy reasons.

### Assumptions

- Backward compatibility with the current observer-first query model is not required for this feature.
- The host-facing definition of "available package" is the active reconciled package set, not every package version retained anywhere on disk.
- Optional loading remains a separate capability and should not become a prerequisite for querying the active reconciled package inventory.
- Hosts that only need metadata and install locations can rely solely on the core package catalog; hosts that need assembly scanning guidance can additionally rely on the loading catalog.
- At least one repository sample should demonstrate the query-first integration model so downstream consumers can see how to use active assembly scan candidates from host code.
- Operator-facing remote/admin read surfaces are part of the long-term design, but the in-process query model is the primary contract that later delivery stages will build upon.
- The active package catalog is a core host-facing runtime service; admin and remote operator surfaces are secondary compositions over that same underlying package inventory.
- The loading catalog is owned by the optional loading module as a direct host-facing service when loading is installed; admin and remote operator surfaces compose it secondarily rather than defining its primary access path.
- The loading catalog provides assembly-level guidance only; hosts remain responsible for running discovery/scanning logic and interpreting discovered types.
- When the optional loading module is not installed, no standalone loading catalog service exists in core runtime composition; loading-aware admin or remote surfaces must report loading as unavailable.

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: Nuplane MUST expose a standalone host-facing active package catalog service from the core runtime as the primary query surface for the current active reconciled package set.
- **FR-002**: The active package catalog MUST return a descriptor for each currently active package that includes package identity, active version, source or feed provenance, install location, and activation timing. Only packages that are currently active appear in this inventory; rollback-retained, failed, and removed package versions are never returned as active package entries.
- **FR-003**: The reconciliation completion flow MUST persist the complete active package descriptor set as the durable source of truth used by later package queries and restart recovery.
- **FR-004**: The active package catalog MUST represent only the active reconciled package set and MUST NOT classify every retained on-disk version as host-available inventory.
- **FR-005**: Nuplane MUST expose operational state as a query surface that is separate from package inventory so that consumers can read health and reconciliation status without coupling those reads to package inventory semantics.
- **FR-005A**: Operator-facing in-process and remote/admin read surfaces MUST compose the standalone active package catalog rather than being the only supported path for hosts to access active package inventory.
- **FR-006**: Nuplane MUST expose a standalone loading catalog service from the optional loading module, separate from the active package catalog, that reports per-active-package loading status, loading diagnostics, and recommended assembly scan candidates for hosts using optional loading.
- **FR-006A**: Operator-facing in-process and remote/admin read surfaces MUST compose the standalone loading catalog when the loading module is installed rather than being the only supported path for hosts to access loading information.
- **FR-007**: The loading catalog MUST explicitly communicate disabled, stale, failed, and available states so that hosts can distinguish "loading not enabled" from "no packages available" and from "loading has not yet been refreshed for this process."
- **FR-007A**: When the optional loading module is not installed, no core no-op loading catalog service is exposed; instead, loading-aware admin or remote read surfaces MUST report loading as unavailable.
- **FR-008**: The loading catalog MUST identify the assemblies Nuplane considers appropriate scan candidates for host discovery scenarios so that hosts do not need to crawl store folders or infer target-specific asset selection rules independently.
- **FR-008A**: The loading catalog MUST NOT expose discovered plugin, module, or application type identities; type discovery remains a host-owned concern performed against the scan candidates or other host-managed scanning flows.
- **FR-009**: Restart behavior MUST restore the active package catalog immediately from persisted state while representing loading information as stale or unavailable until the current process has refreshed loading state.
- **FR-010**: Host notification mechanisms for reconciliation and loading MUST become supplemental invalidation signals; a host MUST be able to derive correct package and loading state from query surfaces alone.
- **FR-011**: Package removal, rollback retention, downgrade, and load-failure scenarios MUST keep the active package catalog and loading catalog logically consistent with one another without redefining what is active versus merely retained.
- **FR-012**: The feature MUST support staged delivery in which the active package catalog can ship independently before loading-catalog and operator-facing extensions, and each delivered stage MUST leave a coherent and stable public model for subsequent stages.
- **FR-013**: Documentation for host integrations MUST describe the query-first model, including separate guidance for metadata-only consumers and for loading-enabled scanners such as CShells.
- **FR-014**: The repository MUST include or update a sample host application that demonstrates querying the loading catalog for active assembly scan candidates and using those candidates for host-owned type discovery.

### Operational & Safety Requirements *(mandatory)*

- **OSR-001**: Package and loading query surfaces MUST preserve deterministic reconciliation semantics: identical desired-state and source inputs produce the same active package inventory and the same state transitions for repeated identical cycles.
- **OSR-002**: Persisting and updating active package descriptors MUST preserve transactional store safety and last-known-good guarantees; metadata enrichment MUST NOT allow a partial write that leaves the active package set ambiguous.
- **OSR-003**: Package descriptors and loading descriptors MUST retain trusted source provenance and MUST NOT weaken existing source-integrity boundaries, validation behavior, or secret-handling rules.
- **OSR-004**: The feature MUST add observable signals for package-catalog state, loading-catalog state, restart-stale loading state, and package-versus-loading failures through structured logs, metrics, and health/degraded reporting.
- **OSR-005**: The feature MUST include automated unit, boundary, and restart-oriented coverage for persisted package inventory, active-versus-retained distinctions, disabled-loading behavior, loading-failure behavior, and any operator-facing read contracts introduced by the feature.

### Key Entities *(include if feature involves data)*

- **Active Package Descriptor**: The authoritative host-facing description of one currently active reconciled package, including identity, version, provenance, install location, and activation timing. An entry in this catalog means the package is active; entries for retained, failed, or removed versions are not included.
- **Active Package Catalog Snapshot**: A consistent point-in-time view of all active package descriptors that the host can query without replaying observer history.
- **Standalone Active Package Catalog Service**: The core runtime query service that exposes active package inventory directly to host code and serves as the inventory source composed by admin or remote read surfaces.
- **Standalone Loading Catalog Service**: The optional loading-module query service that exposes loading information and scan candidates directly to host code and serves as the loading source composed by admin or remote read surfaces when loading is installed.
- **Loading Package Descriptor**: The per-package loading view that pairs an active package with loading availability, current loading status, diagnostics, and scan guidance.
- **Assembly Scan Candidate**: A host-facing description of an assembly Nuplane recommends for discovery or scanning scenarios when optional loading is enabled.
- **Operational State Snapshot**: A separate point-in-time view of reconciliation health, most recent reconcile outcome, degraded reasons, and other operator-oriented state that is intentionally distinct from package inventory.
- **Retained Store Version**: A package version that remains on disk for cleanup or rollback reasons but is not necessarily part of the active host-available package inventory.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: In acceptance testing, a host can retrieve the full active package inventory in a single query after reconcile completion or restart recovery, and 100% of active packages include the provenance and install-location data needed for host decisions.
- **SC-002**: In restart scenarios with persisted state, 100% of tests can read the active package inventory without replaying prior observer events, and the post-restart active package set matches the pre-restart active package set whenever no new reconciliation inputs have changed.
- **SC-003**: In loading-enabled test scenarios, a host can distinguish loaded, failed, disabled, and stale loading states for 100% of active packages in a single loading-catalog query.
- **SC-004**: In acceptance testing, no retained but inactive package version is reported as host-available inventory, even when rollback or cleanup policies intentionally keep that version on disk.
- **SC-005**: Maintainers can deliver and demonstrate the core package catalog stage independently of the loading-catalog stage without redefining the meaning of package availability or requiring downstream hosts to rewrite their core inventory integration.
- **SC-006**: In sample validation, a repository sample host can query Nuplane for active assembly scan candidates and use that result to discover at least one expected host-relevant type from a loaded package without depending exclusively on observer callbacks.
