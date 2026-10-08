# Feature Specification: Reconciliation Completion After Removals

**Feature Branch**: `029-removal-completion`
**Created**: 2026-10-08
**Status**: Draft
**Input**: Publish the existing reconciliation completion notification after committed package removals even when no package was successfully applied. Preserve no-event behavior for unchanged idle and failed-only cycles, existing event ordering, payload, cancellation, observer isolation, and failure accounting; cover removal plus failed remaining acquisitions and prove persisted state is empty before the registered observer runs. Clarify the observer contract for an empty applied-package list.

## User Scenarios & Testing

### User Story 1 - Observe Committed Package Removal (Priority: P1)

A host registers a Nuplane package observer and removes its final active package. After Nuplane commits the empty active state, the host receives the existing reconciliation-completion callback with the removal change set and an empty applied-package list. The host can reread authoritative state and retire features that no longer have a package source.

**Why this priority**: Hosts currently depend on the completion callback to synchronize runtime features with committed package state. Omitting it on removal leaves a removed feature active in the host even though the store has committed its removal.

**Independent Test**: Seed a file-backed store with one active package, run the real reconciliation pipeline with an empty desired source, and assert from the registered observer that the persisted active set is already empty and the callback carries the removal plus an empty applied list.

**Acceptance Scenarios**:

1. **Given** one active package and no desired packages, **When** reconciliation commits its removal, **Then** the normal completion path invokes `OnPackagesChangedAsync` followed by `OnPackagesReconciledAsync` once with the same change set and an empty applied-package list, subject to the existing cancellation and observer-isolation behavior.
2. **Given** an active package is removed while a remaining desired package fails to apply, **When** reconciliation completes, **Then** observers receive completion with the removal and empty successful-applied list, while the result retains failure IDs and degraded status.
3. **Given** a no-change cycle has successfully applied packages, **When** reconciliation completes, **Then** the existing completion notification remains available with those applied packages.

### User Story 2 - Preserve Quiet Cycles (Priority: P2)

A host with no package removal does not receive a completion callback for an empty or failed-only apply result. Existing idle behavior remains quiet while failures continue to be reported through the current result, failure accounting, and health signals.

**Why this priority**: Hosts must not interpret an unchanged empty cycle or failed-only resolution as a committed package refresh.

**Independent Test**: Exercise the health-and-metrics middleware boundary with an empty change set and empty applied list for healthy, idle, and failed-only inputs; verify no reconciliation-completion dispatch and unchanged result accounting.

**Acceptance Scenarios**:

1. **Given** an unchanged empty idle cycle, **When** the cycle completes, **Then** completion observers are not called.
2. **Given** a failed-only cycle with no committed removals and no successful applied packages, **When** the cycle completes, **Then** completion observers are not called and the existing failure result remains intact.

### Edge Cases

- A removal-only change set has an empty applied list.
- A removal commits while some other requested packages fail; successful-applied remains empty.
- A changed callback throws or cancels: reconciliation-completion ordering and existing observer isolation/cancellation behavior remain unchanged.
- A completion observer throws: subsequent observers and pipeline `next()` behavior remain unchanged.
- A completion observer must see persisted active state after the removal has committed, not a stale pre-cycle snapshot.

## Requirements

### Functional Requirements

- **FR-001**: `HealthAndMetricsMiddleware` MUST notify reconciliation-completion observers when at least one package was successfully applied or the committed change set contains at least one removed package.
- **FR-002**: The completion notification MUST carry the original change set and the actual successful-applied package list, including an empty list for removal-only completion.
- **FR-003**: The reconciler MUST preserve changed-before-completion ordering and existing observer registration order, cancellation, isolation, and pipeline continuation behavior.
- **FR-004**: The reconciler MUST leave empty no-change and failed-only cycles without committed removals notification-free.
- **FR-005**: Completion notification MUST not alter reconciliation failure IDs, degraded status, metrics, persisted state, or last-known-good behavior.
- **FR-006**: The public observer contract MUST document that a removal can produce completion with an empty applied-package list.
- **FR-007**: The optional package auto-loading observer MUST process an empty-applied removal through its existing authoritative-state reread and context-retirement path without introducing new collection policy.

### Operational & Safety Requirements

- **OSR-001**: Repeating an identical removal reconciliation MUST leave the same empty active state and MUST NOT invent applied packages.
- **OSR-002**: Removal notification MUST occur only after the existing reconciliation pipeline commits active state; existing LKG and transaction behavior remain unchanged.
- **OSR-003**: This change MUST NOT alter source trust, package integrity, credentials, or package resolution policy.
- **OSR-004**: Existing correlation, cycle metrics, health, and failure accounting MUST remain unchanged for removal and failed-removal cycles.
- **OSR-005**: Tests MUST cover middleware boundaries, the optional loading observer's empty-applied removal path, a real persisted-state reconciliation regression, and a causal mutation/revert proof.

### Key Entities

- **Package change set**: The additions, updates, and removals computed for a reconciliation cycle.
- **Applied packages**: Packages successfully applied in the cycle; this list can be empty when removals commit.
- **Reconciliation completion notification**: The existing observer callback signaling that the cycle has committed and exposing the change set and applied list.
- **Persisted active state**: The package versions currently selected by the store after the cycle's commit.

## Success Criteria

### Measurable Outcomes

- **SC-001**: A normally completed cycle with a committed removal and an empty successful-applied list invokes completion observers once under the existing cancellation and observer-isolation semantics, with the original change set and empty applied list.
- **SC-002**: An observer in the real pipeline can read the file-backed state and sees the removed package absent before its callback runs.
- **SC-003**: Empty unchanged and failed-only cycles still invoke no completion callback.
- **SC-004**: A removal-plus-failure result remains degraded with the same failure identifiers before and after the notification change.
- **SC-005**: Reverting only the removal part of the notification condition causes the real removal-to-zero regression test to fail; restoring the condition makes it pass.
