# Feature Specification: Loading & Query API Simplification

**Feature Branch**: `[015-simplify-loading-api]`  
**Created**: 2026-04-10  
**Status**: Draft  
**Input**: Updated user direction: "Simplify Nuplane's loading/query architecture across the entire codebase around active packages, load state, assemblies, and optional type finding; remove unnecessary complexity where possible; preserve clean admin/loading separation and query-first semantics; and allow unnecessary public or internal constructs to be removed, merged, renamed, refactored, or internalized outright."

## Clarifications

### Session 2026-04-11

- Q: Should the simplified loading API keep exact-version/provider mechanics available to hosts as advanced-only public surfaces? → A: No. Internalize or remove exact-version/provider mechanics entirely; hosts use `IActivePackageCatalog`, load state, `IPackageAssemblyCatalog`, and optional type finding only.
- Q: Should `IPackageTypeFinder` remain public after simplification, or be internalized behind assembly access? → A: Keep `IPackageTypeFinder` public as an optional secondary host surface, documented after assemblies in default guidance.

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Teach a Smaller Host Mental Model (Priority: P1)

As a host integrator, I want Nuplane's public query surface to be organized around a small set of clear concepts so that I can understand how to read active packages, inspect load state, access assemblies, and optionally find types without learning internal loading mechanics.

**Why this priority**: This is the primary value of the feature. If the vocabulary is not simplified first, every later loading/query enhancement continues to increase cognitive load for hosts and makes the architecture harder to explain.

**Independent Test**: Can be fully tested by reviewing the resulting public taxonomy, default onboarding guidance, and simplified contracts and confirming that a new host integration can complete the common read-only flows by learning only the four intended concepts.

**Acceptance Scenarios**:

1. **Given** a host developer who only needs the currently active package inventory, **When** they review the host-facing API taxonomy, **Then** the default guidance starts with active packages and does not require them to understand loading providers, scanners, or load sessions.
2. **Given** a host developer who needs loaded assemblies, **When** they review the same taxonomy, **Then** the default loading-enabled surface is package assemblies, with load state available as a separate explanatory surface rather than a prerequisite for every assembly read.
3. **Given** a host developer who wants assignable types from package assemblies, **When** they review the host-facing API taxonomy, **Then** type finding is presented as optional convenience layered on top of assembly access rather than as the primary discovery model.

---

### User Story 2 - Simplify the Whole Loading Architecture (Priority: P2)

As a Nuplane maintainer, I want a concrete simplification and disposition plan for the loading/query architecture so that I can reduce overlapping nouns, remove unnecessary abstractions, keep only the right contracts public, and clean up internal layering without reopening the architectural direction.

**Why this priority**: Once the public vocabulary is chosen, the next risk is preserving old complexity under new names. A concrete plan prevents partial cleanup, conflicting guidance, accidental exposure of low-level mechanics, and retention of abstractions that no longer justify their existence.

**Independent Test**: Can be fully tested by reviewing the specification and verifying that every relevant public or internal contract, model, method, route, and layering construct has a recommended outcome: keep, rename, merge, remove, classify as default public, classify as secondary public, classify as advanced-only public, or internalize.

**Acceptance Scenarios**:

1. **Given** the current loading/query contracts, **When** maintainers consult the specification, **Then** they can identify which names remain canonical, which names change, and which concepts should disappear from default host guidance.
2. **Given** a contract or abstraction that exists mainly to support low-level loading mechanics, **When** maintainers consult the specification, **Then** they can determine whether that construct stays public for a justified advanced scenario, becomes internal, is merged into a clearer construct, or is removed outright.
3. **Given** admin, runtime, and loading-owned query surfaces, **When** maintainers consult the specification, **Then** the boundaries between core admin, active package inventory, load state, assemblies, optional type finding, and host-owned discovery remain explicit and non-overlapping.

### Edge Cases

- A host only needs active package metadata and should not have to learn load-state or assembly terms to complete that scenario.
- A host needs assemblies but does not need detailed availability reasoning; the default surface must remain useful when loading is disabled, stale, or failed.
- A host must rely on the canonical active-package, load-state, assembly, and optional type-finding surfaces only; exact-version/provider mechanics are not retained as public escape hatches.
- Administrative read surfaces must continue to expose package and operational state without re-coupling core admin to loading-specific terminology or routes.
- `Assembly`, `Type`, and derived reflection artifacts may survive longer than intended if cached; the public model must make those unload-sensitive constraints explicit.
- A construct that duplicates another construct's responsibility through aliases, pass-through layers, or bookkeeping-only models should be removed or merged unless it preserves a distinct safety or ownership boundary.
- An advanced-only contract that no longer serves a clear host scenario should not remain public merely because it exists today.
- A public or internal name that still uses terms such as provider, scanner, candidate, loader, descriptor, materializer, coordinator, or session may be justified in rare cases, but it must not appear as the first concept taught to normal hosts and should be removed when it does not preserve unique meaning.

### Assumptions

- `IActivePackageCatalog` remains the canonical active package inventory surface and is not renamed away from its current top-level role.
- The goal is simplification of vocabulary, architecture, and mental models rather than the introduction of new loading capabilities.
- Query-first semantics remain the preferred integration model; observer-based notifications remain secondary invalidation signals rather than the primary source of truth.
- Host-owned discovery boundaries remain intact: Nuplane may help hosts reach assemblies or optionally find matching types, but it does not become the owner of host-specific plugin semantics.
- `IPackageAssemblyCatalog` remains the default loading-enabled assembly access surface for hosts.
- Type finding remains optional convenience layered over assemblies and is not required for hosts that only need assembly access.
- Exact-version and provider-style assembly or type access mechanics are internal-only implementation details if they survive at all; they are not part of the public host model.
- The entire loading/query architecture is in scope for cleanup, including public APIs, internal contracts, models, naming, ownership boundaries, and layering.
- Backward compatibility is not a constraint for this feature; unnecessary aliases, bridges, staged deprecations, or compatibility windows are not required.
- Low-level loading mechanics may remain available only when they serve a distinct advanced scenario or safety boundary; otherwise they should be removed, merged, or internalized.

## Requirements *(mandatory)*

### Architectural Simplification Principles

- Nuplane's default host-facing vocabulary MUST be teachable through four primary concepts only: **Active packages**, **Load state**, **Assemblies**, and **Optional type finding**.
- Terms that primarily describe internal mechanics or implementation shape rather than host intent MUST be removed from default host guidance wherever possible.
- The architecture plan MUST apply simplification across public APIs, internal contracts, models, naming, and layering, not only at public API boundaries.
- The resulting taxonomy and layering MUST make it obvious which surface answers inventory questions, which surface answers load-state questions, which surface returns unload-sensitive runtime objects, and which constructs are internal infrastructure rather than host concepts.
- Unnecessary abstractions, pass-through layers, duplicate models, and mechanics-first constructs SHOULD be removed when possible and otherwise refactored or merged into clearer canonical shapes.

### Recommended Host Taxonomy

1. **Active packages** answer "What packages are currently active?"
2. **Load state** answers "What is the current loading availability or failure state for those active packages?"
3. **Assemblies** answer "Which in-process runtime assemblies can the host inspect right now?"
4. **Optional type finding** answers "If the host wants convenience filtering, which matching types are currently discoverable from those assemblies?"

Hosts that only need package inventory stop at active packages. Hosts that need runtime inspection move from active packages to assemblies, consulting load state only when they need explanatory status or diagnostics. Type finding remains optional rather than part of the required base model.

### Recommended Simplification & Disposition Matrix

| Current surface | Recommended outcome | Concept taught to hosts | Visibility target | Simplification intent |
|-----------------|---------------------|-------------------------|-------------------|-----------------------|
| `IActivePackageCatalog` | Keep name as-is | Active packages | Default public | Remains the canonical inventory surface |
| `IActivePackageCatalog.GetSnapshotAsync` | Rename to `GetActivePackagesAsync` | Active packages | Default public | Remove the extra "snapshot" term from the primary host call |
| `ActivePackageCatalogSnapshot` | Rename to `ActivePackagesSnapshot` | Active packages | Default public | Keep point-in-time semantics while dropping "catalog" from the model name |
| `ActivePackageDescriptor` | Rename to `ActivePackage` | Active packages | Default public | Replace the implementation-flavored "descriptor" noun with the thing the host is reading |
| `ILoadingCatalog` | Rename to `IPackageLoadStateCatalog` | Load state | Default public | Align the service name with the concept hosts are actually querying |
| `ILoadingCatalog.GetSnapshotAsync` | Rename to `GetLoadStateAsync` | Load state | Default public | Make the method answer the host question directly |
| `LoadingCatalogSnapshot` | Rename to `PackageLoadStateSnapshot` | Load state | Default public | Separate load-state terminology from the broader loading subsystem name |
| `LoadingPackageDescriptor` | Rename to `PackageLoadState` | Load state | Default public | Replace "descriptor" with a direct package state concept |
| `LoadingStatus` | Rename to `PackageLoadStatus` | Load state | Default public | Keep package-scoped meaning explicit and consistent |
| `IPackageAssemblyCatalog` | Keep name as-is and present as the default loading-enabled assembly surface | Assemblies | Default public | Preserve the strongest current host-facing name |
| `PackageAssemblyCatalogEntry` | Rename to `PackageAssemblies` | Assemblies | Default public | Describe the returned value in host terms instead of catalog mechanics |
| `AssemblyScanCandidate` | Rename to `PackageAssemblyReference` | Assemblies | Default public | Remove the speculative "candidate" noun from the default model |
| `IPackageTypeScanner` | Rename to `IPackageTypeFinder` and keep it public as an optional secondary surface documented after assemblies | Optional type finding | Secondary public | Replace "scanner" with the host outcome while keeping the feature secondary to assembly access |
| `IPackageTypeScanner.FindTypesAsync(...)` | Keep the verb `FindTypesAsync`, but move it under the type-finding terminology | Optional type finding | Secondary public | Preserve a familiar action while reducing vocabulary overlap |
| `IPackageAssemblyProvider` | Remove or internalize entirely | None in default guidance | Internal or removed | Eliminate provider and exact-version mechanics from the public host model |
| `IPackageLoader` | Internalize or merge into clearer runtime infrastructure | None in default guidance | Internal by default | Keep loading mechanics out of the host mental model and reduce orchestration vocabulary |
| `IPackageUnloadCoordinator` | Internalize or merge into clearer runtime infrastructure | None in default guidance | Internal by default | Treat unload coordination as runtime infrastructure, not host vocabulary |
| `ILoadingEventDispatcher` / `IPackageLoadingObserver` / `ILoadingFailureTracker` | Internalize, remove, or consolidate behind query-first state surfaces | None in default guidance | Internal by default | Preserve query-first semantics and avoid observer-first framing |
| `PackageLoadSession`, `PackageLoadContextHandle`, `PackageLoadResult`, `DeactivationAttempt`, `UnloadOutcome`, `UnloadOutcomeRecord` | Remove, merge, or internalize unless a distinct boundary requires them | None in default guidance | Internal by default | Prevent low-level runtime bookkeeping from becoming architecture vocabulary |
| `MapNuplaneLoading` and `GET /nuplane/admin/loading` | Rename to `MapNuplaneLoadState` and `GET /nuplane/admin/load-state` | Load state | Loading-owned public composition | Align host and operator terminology with the new canonical concept |

### Exposure, Removal, and Layering Policy

- **Default public surfaces**: `IActivePackageCatalog`, the renamed load-state catalog, and `IPackageAssemblyCatalog`.
- **Secondary public surface**: `IPackageTypeFinder` remains public as an optional convenience surface, but default host guidance MUST teach assemblies first and introduce type finding only afterward.
- **Advanced-only public surfaces**: None are required by this specification; `IPackageTypeFinder` is the only retained non-default public surface and is classified as secondary optional rather than advanced-only. Mechanics-first and exact-version/provider access should be internalized or removed rather than retained as public escape hatches.
- **Internal-by-default surfaces**: Load orchestration, unload coordination, event dispatch, observer hooks, failure tracking, load sessions, and other runtime bookkeeping contracts.
- **Layering rule**: Naming, models, and ownership boundaries across admin, loading, runtime, and supporting packages MUST align to the canonical concepts and should collapse duplicate or pass-through layers when those layers do not preserve distinct meaning or safety.
- **Removal rule**: An abstraction that exists only to mirror retired vocabulary, preserve accidental layering, or expose low-level mechanics without a clear user or safety need SHOULD be removed rather than renamed.
- **Documentation rule**: Default guidance, samples, and host decision trees MUST start with default public surfaces only, then introduce `IPackageTypeFinder` as a secondary optional convenience after assembly access is explained; advanced-only contracts may appear only in advanced guidance.

### Functional Requirements

- **FR-001**: The feature MUST define and document a canonical host-facing loading/query taxonomy centered on only four primary concepts: active packages, load state, assemblies, and optional type finding.
- **FR-002**: The architecture plan MUST preserve `IActivePackageCatalog` as the active package inventory anchor and MUST treat that surface as the first concept taught to hosts.
- **FR-003**: The architecture plan MUST recommend a load-state naming scheme that replaces or demotes the current loading-catalog vocabulary for contracts, methods, models, routes, and supporting terminology that shape the mental model.
- **FR-004**: The architecture plan MUST preserve `IPackageAssemblyCatalog` as the default host-facing loading-enabled assembly access surface and MUST describe how its supporting model names should emphasize assemblies rather than catalog mechanics.
- **FR-005**: The architecture plan MUST define optional type finding as a convenience layer over assembly access, MUST rename the current scanner surface to `IPackageTypeFinder`, and MUST keep it public only as a secondary host surface documented after assemblies in default guidance.
- **FR-006**: The specification MUST provide a concrete simplification and disposition matrix covering relevant public and internal contracts, methods, models, routes, composition names, and layering constructs that shape the loading/query architecture.
- **FR-007**: The specification MUST classify each relevant loading/query construct as one of: default public, secondary public, advanced-only public, internal, merge/refactor, or remove, and MUST include the reason for that classification.
- **FR-008**: The architecture plan MUST identify which overlapping or implementation-flavored nouns should disappear from default host guidance and from internal architecture where unnecessary, including at least provider, scanner, candidate, descriptor, loader, coordinator, session, and similar mechanics-first terminology where applicable.
- **FR-009**: The architecture plan MUST preserve the clean separation between core admin and optional loading surfaces: core admin remains responsible for package, operational-state, and reconcile reads, while loading-owned composition remains responsible for load-state and assembly-oriented reads.
- **FR-010**: The architecture plan MUST preserve query-first semantics and host-owned discovery boundaries: query surfaces may expose active packages, load state, and assembly access, but discovered plugin or application semantics remain host-owned.
- **FR-011**: The architecture plan MUST define public constraints for unload-sensitive runtime objects, including `Assembly`, `Type`, and derived reflection artifacts, and MUST state where such objects are allowed, where they are forbidden, and how hosts should use them safely.
- **FR-012**: The architecture plan MUST make explicit that backward compatibility is not required for this simplification and that unnecessary aliases, bridges, staged deprecations, or compatibility windows are not prerequisites for removing or internalizing outdated constructs.
- **FR-013**: Default host-facing guidance MUST include a simple decision path that answers: when to use active packages, when to use load state, when to use assemblies, and when optional type finding is appropriate.
- **FR-014**: Public and operator-facing read models that are durable, serializable, or remotely exposed MUST NOT contain unload-sensitive `Assembly` or `Type` objects; those objects are limited to in-process runtime convenience surfaces only.
- **FR-015**: Any advanced-only contract that no longer provides distinct host value MUST be removed or internalized rather than retained merely because it exists in the current architecture.
- **FR-016**: The simplified host-facing API MUST NOT expose exact-version or provider-style assembly/type mechanics as public contracts; hosts use active packages, load state, `IPackageAssemblyCatalog`, and optional type finding only.
- **FR-017**: The architecture plan MUST simplify naming, contracts, models, and ownership boundaries across the full loading/query codebase so that duplicate concepts, pass-through layers, and unnecessary abstractions are reduced or eliminated.
- **FR-018**: The architecture plan MUST identify internal constructs that can be merged, collapsed, or deleted without losing required safety, trust, or ownership boundaries.

### Operational & Safety Requirements *(mandatory)*

- **OSR-001**: Vocabulary simplification MUST preserve the existing meaning of active-package inventory, load-state reporting, and loading-enabled assembly access under repeated identical reconciliation inputs.
- **OSR-002**: The resulting architecture MUST avoid partial or contradictory naming states in which multiple retained constructs represent the same concept without a clear canonical owner.
- **OSR-003**: Public models and guidance introduced by this feature MUST preserve existing trust and validation boundaries for packages, feeds, and package-derived artifacts; simplification MUST NOT encourage consumers to bypass validated package acquisition flows.
- **OSR-004**: Diagnostics, operator-facing guidance, and default documentation MUST use the canonical terminology consistently and MUST NOT reintroduce retired mechanics-first vocabulary as a parallel mental model.
- **OSR-005**: Test planning for the implementation phase MUST cover default host flows, the secondary optional `IPackageTypeFinder` flow, any advanced-only flows that remain public, disabled and stale load-state behavior, unload-safety guidance for runtime objects, and the removal or internalization of retired constructs.
- **OSR-006**: If any contract is proposed to remain advanced-only public during implementation planning, it MUST have an explicit support boundary and a documented reason it still exists as public rather than internal.
- **OSR-007**: When a construct or layer is removed or merged, its responsibilities MUST either disappear as unnecessary complexity or be reassigned to a clearer remaining construct without duplicating semantics.

### Key Entities *(include if feature involves data)*

- **Active Package**: A host-facing representation of one package that is currently active and available in the reconciled runtime inventory.
- **Active Packages Snapshot**: A point-in-time read of the active package inventory used by hosts and composed by operator-facing surfaces.
- **Package Load State**: A package-scoped view of whether an active package is loaded, stale, failed, or otherwise unavailable for runtime assembly access.
- **Package Load State Snapshot**: A point-in-time read of load-state information across the active package set.
- **Package Assemblies**: The host-facing runtime assembly collection for one active package, returned only from in-process assembly access surfaces that can safely expose unload-sensitive objects.
- **Package Assembly Reference**: A durable, non-runtime-object description of an assembly associated with an active package, safe for load-state and remote or serialized views.
- **Optional Type Finder**: A convenience query surface that finds matching runtime types from package assemblies without redefining host-owned discovery semantics.
- **Surface Classification**: The maintained mapping that labels each loading/query construct as default public, secondary public, advanced-only public, internal, merge/refactor, or remove.
- **Simplification Disposition**: The explicit decision for a current construct that states whether it is kept, renamed, merged, internalized, or removed and why that outcome improves the mental model.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: In spec review, 100% of default host integration flows can be explained using only the four intended concepts: active packages, load state, assemblies, and optional type finding.
- **SC-002**: The simplification and disposition matrix covers 100% of currently relevant loading/query contracts, models, methods, routes, and layering constructs that materially shape the public or internal mental model.
- **SC-003**: Before implementation planning begins, maintainers can classify every current loading/query construct into default public, secondary public, advanced-only public, internal, merge/refactor, or remove with no unresolved ambiguities.
- **SC-004**: In architecture review, all retained public constructs beyond the three default public surfaces have a documented justification, `IPackageTypeFinder` is explicitly documented as a secondary optional surface introduced after assemblies, and exact-version/provider mechanics are not retained as public host-facing escape hatches.
- **SC-005**: In documentation review, none of the default onboarding paths for common host scenarios require provider, scanner, candidate, descriptor, loader, coordinator, session, or similar mechanics-first nouns to explain how to use Nuplane.
- **SC-006**: In cleanup review, the resulting architecture contains no retained aliases, bridge constructs, or pass-through layers whose only purpose is to preserve superseded vocabulary.
- **SC-007**: In acceptance review, 100% of durable or remotely exposed read models exclude unload-sensitive `Assembly` and `Type` objects, and 100% of in-process runtime surfaces that do expose those objects include explicit lifecycle guidance.
