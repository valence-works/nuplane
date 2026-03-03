# Feature Specification: Phase 4 Cluster-Convergent Runtime Loading (Lean)

**Feature Branch**: `004-phase4-operational-enhancements`
**Created**: 2026-03-03
**Status**: Draft
**Input**: Phase 4 roadmap direction, refined toward near-term needs (runtime acquisition + optional loader + admin API)

## Goal

Enable a fleet of identical application replicas to converge on the same set of packages over time, by reconciling from shared desired-state inputs and applying changes safely to a node-local store. Provide an optional administrative surface for inspection and explicit reconcile triggers.

This feature is explicitly **not** a progressive delivery / rollout system. Channels, staged promotion workflows, and canary targeting are deferred to a later phase.

## Clarifications

### Session 2026-03-03

- All replicas should load the same set of packages eventually.
- Primary near-term requirement: add/update package references at runtime (startup + periodic reconcile + explicit trigger), with an optional Loader SDK to load types/services from activated packages.
- Admin API is useful, but authentication/authorization is host-supplied and out of scope.

## Non-Goals

- Distributed coordination primitives (leader election, distributed locks) inside Nuplane.
- Partial/fractional rollout (canary percentages), environment channels, or staged promotion workflows.
- Defining a plugin programming model. Nuplane provides package acquisition + store + events; hosts decide activation semantics beyond those boundaries.

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Converge from a Shared Desired Manifest (Priority: P1) 🎯 MVP

As a developer/operator, I want each replica to reconcile from the same deterministic desired-state manifest so all replicas converge to the same active package set over time.

**Why this priority**: Cluster convergence is the core value for runtime package loading without requiring distributed coordination inside Nuplane.

**Independent Test**: Run multiple host instances against a shared desired manifest, update the manifest to new exact versions, and verify that each host eventually reaches the same active set without unsafe mutations on failure.

**Acceptance Scenarios**:

1. **Given** two replicas read the same desired manifest, **When** they run reconciliation cycles, **Then** they compute the same desired package set and eventually activate the same versions.
2. **Given** the desired manifest is updated to point to a new version for a package, **When** replicas observe the update (polling or explicit trigger), **Then** each replica eventually activates the new version with transactional/LKG safety.
3. **Given** a desired manifest points to a package version that cannot be acquired (missing blob/feed outage), **When** reconciliation runs, **Then** the cycle is non-mutating for that package and the last-known-good active version remains active.

---

### User Story 2 - Acquire Packages from Multiple Sources (Priority: P2)

As a developer, I want to configure multiple desired-state inputs (directory, blob-like object storage, and/or NuGet feeds) so my app can acquire packages automatically at startup and over time.

**Why this priority**: Real deployments will mix sources; the system must remain deterministic and safe when sources are partial/unavailable.

**Independent Test**: Configure multiple desired sources and verify deterministic aggregation and failure isolation.

**Acceptance Scenarios**:

1. **Given** multiple desired sources are configured, **When** reconciliation aggregates desired state, **Then** the aggregated set is deterministic for identical inputs.
2. **Given** one desired source is temporarily unavailable, **When** reconciliation runs, **Then** the cycle produces a degraded, correlation-linked outcome and does not corrupt active/LKG state.

---

### User Story 3 - Load Activated Packages via an Optional Loader SDK (Priority: P3)

As a developer, I want an optional Loader SDK to load assemblies/types/services from the activated packages so my host can discover and use functionality provided by packages.

**Why this priority**: This is the end-to-end “runtime package reference” experience; acquisition without a loader is incomplete for many hosts.

**Independent Test**: Activate a package that contains a known type, then verify the loader can resolve and load it; verify failures are isolated and observable.

**Acceptance Scenarios**:

1. **Given** a package becomes active, **When** the loader is enabled, **Then** assemblies from the active package are loadable according to loader policy.
2. **Given** loading fails for a package, **When** reconciliation completes, **Then** the failure is observable and does not crash the host.

---

### User Story 4 - Operate via Administrative Surfaces (Priority: P4)

As an operations engineer, I want an optional administrative surface to inspect package/runtime state and trigger reconciliation so I can diagnose and operate the system safely.

**Why this priority**: It provides fast feedback loops for uploads and troubleshooting without requiring bespoke host integration.

**Independent Test**: Retrieve runtime state/health views and issue a manual reconcile trigger; confirm outputs reflect actual reconciliation outcomes.

**Acceptance Scenarios**:

1. **Given** the administrative surface is enabled, **When** an operator requests package and state views, **Then** the system returns current active packages, last reconcile outcome, and health-relevant status.
2. **Given** an operator issues a manual reconcile request, **When** reconciliation completes, **Then** resulting state and outcomes are reflected consistently in operational views and diagnostics.

---

### Edge Cases

- Desired manifest is updated while some replicas are offline; replicas converge on next startup.
- Desired manifest references a package version not yet fully uploaded; acquisition fails safely and is retried on later cycles.
- Multiple desired sources provide conflicting requests for the same package ID.
- Administrative read operations are available while manual reconcile trigger is temporarily unavailable.

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: The system MUST support a shared desired manifest as an optional desired-state input.
- **FR-001a**: When the desired manifest is used, it MUST be deterministic: identical manifest content MUST yield identical desired package requests.
- **FR-001b**: The desired manifest format MUST support exact package versions.
- **FR-001c**: If the desired manifest cannot be read or parsed, the system MUST perform no unsafe mutations and MUST emit a degraded outcome with diagnostics.

- **FR-002**: The system MUST support aggregating desired state from multiple configured desired sources.
- **FR-002a**: Aggregation MUST be deterministic for identical inputs (including deterministic ordering and tie-break rules).
- **FR-002b**: Unavailable desired sources MUST produce non-mutating degraded outcomes for impacted inputs and MUST NOT corrupt active/LKG state.

- **FR-003**: The system MUST support automatic reconciliation at startup and periodic reconciliation via polling.
- **FR-004**: The system MUST support explicit reconciliation triggers via an in-process API and (optionally) via an administrative surface.

- **FR-005**: The system MUST expose an optional administrative capability surface for viewing package inventory, runtime state, last reconcile outcome, and health state.
- **FR-006**: The administrative surface MUST support on-demand reconciliation requests when invoked through a host-authorized administrative boundary.
- **FR-006a**: Unauthorized or unavailable administrative reconcile requests MUST produce explicit non-mutating outcome codes and correlation-linked diagnostics.
- **FR-007**: Administrative views MUST present a consistent snapshot of active package status and last reconcile outcomes.

- **FR-008**: When the optional Loader SDK is enabled, the system MUST provide a safe integration boundary for loading assemblies/types from active packages.
- **FR-008a**: Loader failures MUST be isolated (per package) and MUST NOT crash the host.

### Operational & Safety Requirements *(mandatory)*

- **OSR-001**: Reconciliation/apply flows MUST be idempotent for repeated identical inputs.
- **OSR-002**: Package acquisition and activation MUST follow transactional safety semantics and preserve last-known-good state on failure.
- **OSR-003**: Desired-source outages, manifest errors, package acquisition failures, and loader failures MUST be isolated to the impacted package and MUST NOT force unrelated package activations to fail.

- **OSR-004**: Every reconciliation cycle MUST emit correlation-linked logs, metrics, and health signals covering desired inputs, acquisition outcomes, activation outcomes, and manual trigger outcomes.
- **OSR-004b**: Every failed desired-source, acquisition, activation, loader, and administrative operation MUST emit an observer event with correlation ID, scoped target (package/source/operation), and reason code, in addition to logs/metrics/health signals.

- **OSR-005**: The feature MUST include automated unit and integration tests for manifest determinism, multi-source aggregation determinism, degraded non-mutating failure behavior, loader boundary failure isolation, and administrative operations.

### Key Entities *(include if feature involves data)*

- **Desired Manifest**: A deterministic artifact describing the desired exact package set.
- **Operational Snapshot**: Operator-facing view of active packages, last reconcile outcome, and current health state.

## Assumptions

- Replicas share access to the same desired manifest and package sources (directory/blob/feed) but maintain node-local stores.
- Authentication/authorization for administrative actions is supplied by host integrations and is out of scope for this feature specification.
- Exact version pinning is the initial default for manifest-driven desired state; future phases may add configurable policies.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: In a validation run with multiple replicas reading the same desired manifest, 100% of replicas converge to the same active package set within a bounded time window (polling interval + one retry window) when sources are healthy.
- **SC-002**: On injected failures (manifest parse failure, source outage, acquisition failure, loader failure), 0 runs corrupt the local store or violate last-known-good preservation.
- **SC-003**: In acceptance validation, for the two-step workflow (read operational snapshot and trigger reconcile), at least 95 of 100 attempts MUST complete end-to-end within 120 seconds.
