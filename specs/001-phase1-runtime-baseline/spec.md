# Feature Specification: Phase 1 Runtime Baseline

**Feature Branch**: `001-phase1-runtime-baseline`  
**Created**: 2026-03-02  
**Status**: Draft  
**Input**: User description: "Create initial Phase 1 core runtime specification from roadmap"

## Clarifications

### Session 2026-03-02

- Q: How should duplicate package IDs from multiple desired inputs in one cycle be reconciled? → A: Highest-version-wins; deterministic tie-break by source name.
- Q: How should reconciliation behave when a desired source is temporarily unavailable? → A: Reuse last successful source snapshot, continue cycle, mark degraded.
- Q: How should overlapping reconciliation triggers be handled? → A: Single-flight only; ignore/log additional triggers while one cycle is active.
- Q: What package identity trust policy should Phase 1 enforce? → A: Strict allowlist only; reject non-allowlisted package IDs.
- Q: When should system health return from degraded to healthy? → A: Only after a fully successful cycle with fresh reads for all configured sources.

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Reconcile and Activate Desired Packages (Priority: P1)

As an operator, I can define a desired package set and have the runtime reconcile actual state to that desired state on a polling interval, so the host runs the intended package versions without manual intervention.

**Why this priority**: This is the core business value of Nuplane and the minimum viable capability.

**Independent Test**: Can be fully tested by providing a desired package set, running one reconciliation cycle, and verifying adds/updates/removals and active package state.

**Acceptance Scenarios**:

1. **Given** a desired package set that differs from current active state, **When** a reconciliation cycle runs, **Then** the runtime computes add/update/remove changes and applies them per package.
2. **Given** an unchanged desired package set, **When** repeated reconciliation cycles run, **Then** no additional package changes are applied and active state remains stable.

---

### User Story 2 - Maintain Availability During Failed Updates (Priority: P2)

As an operator, I can rely on last-known-good package versions remaining active if an update fails, so host functionality is preserved during partial or transient failures.

**Why this priority**: Safe failure handling is essential for runtime package mutation in production environments.

**Independent Test**: Can be fully tested by forcing a package update failure and verifying the active pointer does not switch away from the last-known-good version.

**Acceptance Scenarios**:

1. **Given** a package has an active last-known-good version, **When** an update fails during staging, validation, or activation, **Then** the previously active version remains active and failure details are recorded.
2. **Given** one package fails while others in the same cycle are valid, **When** reconciliation continues, **Then** unaffected packages complete according to policy and the host process remains running.

---

### User Story 3 - Observe Runtime Changes and Health (Priority: P3)

As an operator or host integrator, I can receive change notifications and operational signals for each reconciliation cycle, so I can monitor behavior and respond quickly to failures.

**Why this priority**: Operators need clear visibility to trust automated reconciliation.

**Independent Test**: Can be fully tested by running cycles with successful and failed transactions and verifying emitted change events, logs, metrics, and health status transitions.

**Acceptance Scenarios**:

1. **Given** a reconciliation cycle that changes package state, **When** processing completes, **Then** pre-change and post-change events are emitted with a shared correlation identifier.
2. **Given** one or more package failures exist, **When** health is evaluated, **Then** health is reported as degraded until failures are resolved.
3. **Given** a degraded state due to source or package failures, **When** a subsequent cycle completes with no failures and fresh reads from all configured sources, **Then** health returns to healthy.

### Edge Cases

- Desired state is empty; system removes currently active managed packages and records a no-desired-state outcome without host crash.
- Duplicate package identifiers appear from multiple desired inputs in one cycle; system selects the highest version and breaks equal-version ties by source name, then records the decision.
- Desired source is temporarily unavailable; system reuses the last successful snapshot for that source, continues reconciliation, and records degraded health with source access failure.
- Same package update is retried after a previous failure; system behaves idempotently and does not corrupt store state.
- Update succeeds for one package and fails for another in the same cycle; failure isolation preserves completed successful changes.

## Requirements *(mandatory)*

### Assumptions

- Initial scope is limited to Phase 1 baseline from the roadmap.
- Desired state is sourced from explicit package requests and a directory-based `.nupkg` source.
- Package resolution for remote retrieval is limited to a single configured feed in this phase.
- Package identity trust policy is strict allowlist-only for Phase 1.
- Reconciliation is polling-based with a default interval and a manual trigger. The polling loop is a hosted service (`BackgroundService`) registered via DI; the manual trigger is an `IReconciliationService.TriggerManualAsync` method.
- Host integration is event-driven and remains host-neutral.

### Functional Requirements

- **FR-001**: System MUST aggregate desired package state from explicit requests and configured desired-state sources for each reconciliation cycle.
- **FR-002**: System MUST resolve desired package versions deterministically for the current cycle, using highest-version-wins for duplicate package IDs and source-name tie-break for equal versions.
- **FR-003**: System MUST compute a desired-vs-active diff that identifies added, updated, and removed packages.
- **FR-004**: System MUST apply package changes as isolated per-package transactions.
- **FR-005**: System MUST support package removal when a package is no longer present in desired state.
- **FR-006**: System MUST provide polling-based reconciliation with a configurable interval and a manual trigger. Polling MUST be implemented as a `BackgroundService` (hosted service) that invokes the reconciliation engine at the configured `PollInterval` using a `PeriodicTimer`. The hosted service MUST be opt-in (disabled by default) and registered conditionally via DI. The reconciliation engine MUST also expose a public `IReconciliationService` interface with a `TriggerManualAsync` method for programmatic on-demand invocation independent of the polling loop.
- **FR-007**: System MUST ingest desired package changes from a directory containing `.nupkg` files.
- **FR-008**: System MUST support single-feed package retrieval for this phase.
- **FR-009**: System MUST persist active-state metadata that survives process restart.
- **FR-010**: System MUST emit package change notifications before and after applied change sets.
- **FR-011**: System MUST allow only one active reconciliation cycle at a time; if a trigger occurs while a cycle is active, it MUST be skipped and logged with correlation metadata (see OSR-010 for safety posture).
- **FR-012**: System MUST enforce a strict package ID allowlist for desired package inputs and MUST reject non-allowlisted package IDs before resolution.

### Operational & Safety Requirements *(mandatory)*

- **OSR-001**: Reconciliation/apply flows MUST be idempotent for repeated identical inputs.
- **OSR-002**: Update flows MUST define transactional behavior with explicit last-known-good fallback.
- **OSR-003**: Source trust requirements MUST restrict desired-state influence to explicitly configured sources, require strict package ID allowlisting, and validate package identity before activation.
- **OSR-004**: Observability MUST include structured cycle/package logs, correlation identifiers, metrics for outcomes and durations, and health signals for healthy/degraded states.
- **OSR-005**: Failures in package processing MUST be recorded with stage and timestamp and MUST NOT terminate host process execution.
- **OSR-006**: Retry behavior for failed reconciliation or package operations MUST be bounded and policy-driven.
- **OSR-007**: Credentials or secrets used for source access MUST be handled via secure runtime configuration and MUST NOT be committed to source control.
- **OSR-008**: Test coverage MUST include unit tests for diffing and transaction behavior, regression tests for failure and LKG fallback, and boundary tests for runtime-store and runtime-source contracts.
- **OSR-009**: If a desired source is unavailable during a cycle, the system MUST use that source’s last successful snapshot for reconciliation, continue processing, and report degraded health for the cycle.
- **OSR-010**: Reconciliation execution MUST be single-flight; concurrent cycle execution is prohibited to prevent store/state races (functional behavior defined in FR-011).
- **OSR-011**: Health status MUST return from degraded to healthy only after a fully successful reconciliation cycle with fresh reads from all configured desired sources.
- **OSR-012**: Configuration validation MUST use the .NET options pipeline (`IValidateOptions<T>` with `ValidateOnStart()`) and MUST NOT rely on `IsValid()` methods inside options classes.

### Key Entities *(include if feature involves data)*

- **Desired Package Request**: A requested package identity and version intent used as input to reconciliation.
- **Resolved Package**: A concrete package version selected for activation in the current cycle.
- **Package Change Set**: The grouped add/update/remove outcomes for one reconciliation cycle with correlation metadata.
- **Store State Record**: Persisted per-package active version, last-known-good version, and last failure details.
- **Desired Source Definition**: Configuration describing an allowed source of desired packages, including trust and access constraints.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: In acceptance tests with poll interval set to 60 seconds, at least 95% of detected desired-state changes are reconciled to active state within 60 seconds from detection timestamp.
- **SC-002**: In failure-injection scenarios, 100% of failed package updates preserve the previously active last-known-good package version.
- **SC-003**: In repeated-cycle tests with unchanged inputs, 100% of cycles produce no unintended package mutations.
- **SC-004**: For any reconciliation cycle, operators can determine cycle outcome and affected packages within 5 minutes using emitted logs/events that include correlation identifier, cycle status, and package identifiers.
- **SC-005**: For acceptance tests covering add, update, and remove flows, at least 95% of runs complete without manual recovery actions.
