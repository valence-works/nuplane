# Feature Specification: Optional Package Loading

**Feature Branch**: `003-phase3-assembly-loading`  
**Created**: 2026-03-02  
**Status**: Draft  
**Input**: User description: "Create a new spec based on the roadmap Phase 3"

## Clarifications

### Session 2026-03-02

- Q: How should `UnloadPending` retries be scheduled? → A: Retry unload for `UnloadPending` packages on every reconciliation cycle until success.
- Q: How should health treat `UnloadPending` packages? → A: Set health to degraded whenever at least one `UnloadPending` package exists.
- Q: How should host deactivation be bounded before unload attempt? → A: Require a configurable bounded deactivation timeout, then proceed to unload attempt and log timeout outcome.
- Q: How should shared assemblies be matched? → A: Match shared assemblies by strong identity: name, public key token, and major version.

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Load Active Package Assemblies (Priority: P1)

As a host operator, I want an optional loading module to load assemblies from active packages so hosts can run package-provided functionality without building a custom loader.

**Why this priority**: This is the core value of Phase 3; without loading, the module provides no runtime benefit.

**Independent Test**: Can be fully tested by enabling loading for a package set with known dependencies and verifying the package assemblies become available to the host while existing runtime reconciliation remains unchanged.

**Acceptance Scenarios**:

1. **Given** a package is active in the store and loading is enabled, **When** a load cycle runs, **Then** the package assemblies are loaded from that package's active folder.
2. **Given** multiple active packages, **When** a load cycle runs, **Then** each package is loaded in an isolated package-specific loading boundary.

---

### User Story 2 - Respect Shared Contracts (Priority: P2)

As a host integrator, I want designated shared contract assemblies to be reused instead of duplicated so type identity remains consistent across host and loaded packages.

**Why this priority**: Shared contracts prevent type mismatch issues and are necessary for safe host-package interaction.

**Independent Test**: Can be tested by configuring a shared contract list, loading a package that references those contracts, and verifying the loaded package reuses host contract assemblies.

**Acceptance Scenarios**:

1. **Given** a shared assembly list is configured, **When** package assemblies are loaded, **Then** designated shared assemblies are resolved from the shared set and not package-local duplicates.
2. **Given** no shared assembly list is configured, **When** package assemblies are loaded, **Then** loading continues using package-local dependency resolution rules.

---

### User Story 3 - Observe Best-Effort Unload Outcomes (Priority: P3)

As an operator, I want explicit unload outcome reporting when packages are removed so I can take action when in-process unload does not complete.

**Why this priority**: Unload is explicitly best-effort; operators need clear visibility to decide whether restart or other remediation is required.

**Independent Test**: Can be tested by removing an active package and verifying a deactivation/unload attempt is made, with success or pending status and diagnostics emitted.

**Acceptance Scenarios**:

1. **Given** a package is removed from desired state, **When** removal processing occurs, **Then** the host deactivation step executes before unload is attempted.
2. **Given** unload cannot complete, **When** unload processing finishes, **Then** the package is marked unload-pending and the failure is reported with diagnostics.

### Edge Cases

- Package load request arrives for a package that has no active store path at cycle time.
- Shared contract list references an assembly name that cannot be resolved by the host.
- A package is removed while another package still holds references that prevent unload.
- Repeated reconcile cycles process unchanged active packages and must not create duplicate active load registrations.
- Load for one package fails while other packages in the same cycle remain loadable.

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: The system MUST provide an optional loading module that can be enabled or disabled independently of reconciliation.
- **FR-002**: When enabled, the system MUST load assemblies for each active package from the active package location managed by the store.
- **FR-003**: The system MUST isolate package loads so each active package has an independent load boundary.
- **FR-004**: The system MUST support a configurable shared assembly policy to reuse designated host contract assemblies across packages, matched by strong identity (name, public key token, and major version).
- **FR-005**: The system MUST process package removals with the sequence: host deactivation request (bounded by a configurable timeout), unload attempt, and unload outcome reporting.
- **FR-006**: If unload does not complete, the system MUST mark the package as unload-pending, preserve that status for operator action, and retry unload on every reconciliation cycle until success.
- **FR-007**: The system MUST expose load and unload outcomes to observers via callbacks and to operators via runtime-visible status/outcome surfaces without requiring host process termination.
- **FR-008**: Failure in one package load or unload operation MUST NOT block processing of other packages in the same cycle.
- **FR-009**: If host deactivation times out, the system MUST continue removal processing by attempting unload and MUST record the deactivation-timeout outcome for operators.

### Operational & Safety Requirements *(mandatory)*

- **OSR-001**: Reconciliation and loading coordination MUST be deterministic and idempotent for identical desired/active inputs.
- **OSR-002**: Store activation guarantees and last-known-good safety MUST remain unchanged by the loading module; loading failures MUST be non-mutating to package activation state.
- **OSR-003**: Only assemblies from active store locations and explicitly configured shared assembly sources MUST be eligible for resolution.
- **OSR-004**: The feature MUST emit structured logs, metrics, and health signals for load attempts, unload attempts, success/failure counts, and unload-pending totals with cycle correlation, and health MUST report degraded whenever any package is unload-pending.
- **OSR-005**: The feature MUST include automated test coverage for package load boundaries, shared assembly policy behavior, unload success/failure reporting, idempotent repeated cycles, and non-blocking partial failures.

### Key Entities *(include if feature involves data)*

- **Package Load Session**: Runtime record for one active package load boundary, including package identity, active version, session lifecycle state, and last load outcome.
- **Shared Assembly Policy**: Host-defined list of assembly identities matched by name, public key token, and major version that must resolve from shared host context rather than package-local copies.
- **Unload Outcome Record**: Per-package result of removal-time unload processing, including attempt timestamp, outcome status (succeeded or unload-pending), and diagnostic reason when pending.

## Assumptions

- Loading remains opt-in and default-disabled for hosts that provide their own loading behavior.
- Host integration owns deactivation semantics and supplies the deactivation step invoked before unload attempt.
- Unload-pending packages are operationally acceptable as long as status is visible and does not corrupt reconciliation state.

## Success Criteria *(mandatory)*

### Validation Profile (for measurable outcomes)

- Profile name: `phase3-loading-baseline`
- Package set: 20 active packages with valid dependencies, including 5 packages with overlapping dependency names and 2 packages with shared-contract references.
- Cycle window: 10 consecutive reconciliation cycles with identical desired/active inputs for idempotence checks.
- Failure injection set: at least 5 controlled failing operations distributed across load failures, unload failures, and deactivation timeout events.
- Evidence sources: observer callback records plus correlation-linked logs/metrics/health snapshots captured per cycle.

### Measurable Outcomes

- **SC-001**: Under `phase3-loading-baseline`, at least 99% of active packages with valid dependencies are loaded successfully within one reconciliation cycle.
- **SC-002**: 100% of package removal events trigger an unload attempt and publish an explicit unload outcome (success or unload-pending).
- **SC-003**: For repeated identical desired state across 10 consecutive cycles, observed load-session states remain stable with zero unintended duplicate active load registrations.
- **SC-004**: Under `phase3-loading-baseline`, operators can identify load/unload failure cause and affected package from observer callbacks and correlation-linked logs/metrics/health for 100% of injected failed operations without enabling additional diagnostic modes.
