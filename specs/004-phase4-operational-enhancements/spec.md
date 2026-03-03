# Feature Specification: Phase 4 Operational Enhancements

**Feature Branch**: `004-phase4-operational-enhancements`  
**Created**: 2026-03-03  
**Status**: Draft  
**Input**: User description: "Extract requirements for a new spec based on Phase 4 of the Roadmap"

## Clarifications

### Session 2026-03-03

- Q: How should canary node selection be determined for percentage-based rollout? → A: Use stable hash-based selection so identical inputs produce the same selected nodes.
- Q: How should reconciliation behave when the selected channel is empty or unconfigured? → A: Perform no mutations and report degraded with explicit misconfiguration reason.
- Q: How should staged package promotion be triggered? → A: Promotion requires explicit operator action only.
- Q: How should the system behave when a staged promotion fails? → A: Keep current active version, mark staged candidate failed, and continue other package/node operations.

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Enforce Channel Separation (Priority: P1)

As an operator, I want package activation to be constrained to a selected channel so production, staging, and canary environments remain isolated and predictable.

**Why this priority**: Channel isolation is the foundational safety boundary for Phase 4 and prevents cross-environment contamination.

**Independent Test**: Can be fully tested by defining different desired package sets per channel, running reconciliation in each channel, and confirming each channel only activates its own approved packages.

**Acceptance Scenarios**:

1. **Given** production and staging channels have different desired package sets, **When** a reconciliation cycle runs for production, **Then** only production-scoped packages are considered for activation.
2. **Given** an operator changes the active channel selection, **When** the next reconciliation cycle runs, **Then** package evaluation and activation are performed only against the newly selected channel scope.

---

### User Story 2 - Stage and Promote Updates Safely (Priority: P2)

As an operator, I want updates to be staged before activation so I can validate readiness and then promote with controlled, atomic activation.

**Why this priority**: Staged rollout reduces blast radius and enables deliberate promotion decisions without losing deterministic behavior.

**Independent Test**: Can be tested by staging a newer package version, verifying it does not activate automatically, then promoting it and confirming atomic switch with rollback/LKG safety on failure.

**Acceptance Scenarios**:

1. **Given** a newer package version is discovered, **When** staged rollout is enabled, **Then** the version is prepared and remains inactive until explicit operator promotion is requested.
2. **Given** a staged version is promoted, **When** activation executes, **Then** the active pointer switches atomically and the previous last-known-good version remains available for fallback.

---

### User Story 3 - Limit Canary Exposure (Priority: P3)

As a release manager, I want canary rollout controls so only a defined subset of nodes receives a new package version before broader promotion.

**Why this priority**: Canary controls provide a risk-managed adoption path and early detection for problematic updates.

**Independent Test**: Can be tested by configuring a canary target subset and rollout percentage, running reconciliation across a node set, and verifying only eligible nodes activate the canary version.

**Acceptance Scenarios**:

1. **Given** canary rollout is configured for a subset of nodes, **When** activation runs, **Then** only nodes in the canary target set are eligible to activate the canary package version.
2. **Given** canary percentage is unchanged and rollout inputs are unchanged, **When** subsequent cycles run, **Then** the same eligible canary nodes remain selected.
3. **Given** canary percentage is increased, **When** subsequent cycles run, **Then** additional nodes are activated according to the updated rollout limit without affecting out-of-scope nodes.

---

### User Story 4 - Enforce Advanced Integrity Policies (Priority: P4)

As a security operator, I want strict integrity validation rules so only packages meeting required trust and verification criteria can be activated.

**Why this priority**: Integrity enforcement protects the runtime package supply chain and prevents unsafe activations.

**Independent Test**: Can be tested by attempting activation with both compliant and non-compliant packages and verifying compliant packages proceed while violations fail with non-mutating outcomes.

**Acceptance Scenarios**:

1. **Given** strict trust and integrity policies are configured, **When** a package fails a required validation rule, **Then** activation is blocked and the current last-known-good version remains active.
2. **Given** a package meets all required integrity checks, **When** activation is attempted, **Then** it is eligible for staged or direct activation per rollout policy.

---

### User Story 5 - Operate via Administrative Surfaces (Priority: P5)

As an operations engineer, I want an optional administrative surface to inspect package/runtime state and trigger reconciliation so I can diagnose and operate the system safely.

**Why this priority**: Operational visibility and control improve troubleshooting and reduce recovery time without coupling to a specific host.

**Independent Test**: Can be tested by retrieving runtime package/state/health views and issuing a manual reconcile trigger, then confirming outputs reflect the same truth as reconciliation logs and events.

**Acceptance Scenarios**:

1. **Given** the administrative surface is enabled, **When** an operator requests package and state views, **Then** the system returns current active, staged, and health-relevant status information.
2. **Given** an operator issues a manual reconcile request, **When** reconciliation completes, **Then** resulting state and outcomes are reflected consistently in operational views and diagnostics.

---

### Edge Cases

- Channel selection references a channel name with no configured desired-state sources; cycle remains non-mutating and reports degraded with explicit misconfiguration reason.
- A package is staged successfully but promotion condition is never met for an extended period.
- A staged promotion fails for one package/node while other staged promotions in the same cycle are still eligible.
- Canary node targeting changes while a canary rollout is in progress.
- A package passes feed trust checks but fails hash/signature verification at activation time.
- Administrative read operations are available while manual reconcile trigger is temporarily unavailable.
- Cleanup/retention actions encounter versions that are still required as last-known-good.

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: The system MUST support explicit channel selection for reconciliation and activation scopes, including at minimum production, staging, and canary channels.
- **FR-002**: The system MUST enforce channel separation so desired-state evaluation and activation in one channel do not activate packages scoped to another channel.
- **FR-002a**: If the selected channel has no configured desired-state sources, the system MUST perform no package mutations for that cycle and MUST record an explicit channel-misconfiguration outcome.
- **FR-003**: The system MUST support staged rollout behavior where a resolved update can be prepared without immediate activation.
- **FR-004**: The system MUST support controlled promotion of staged versions using explicit operator action only.
- **FR-005**: Promotion from staged to active MUST execute with atomic activation semantics and preserve last-known-good fallback behavior.
- **FR-005a**: If promotion fails for a package/node, the system MUST keep the current active version unchanged, mark the staged candidate as failed, and continue processing unrelated package/node operations.
- **FR-006**: The system MUST support canary rollout targeting based on a defined subset of eligible nodes.
- **FR-007**: For percentage-based canary rollout, the system MUST use deterministic stable selection so identical rollout inputs produce the same selected nodes across cycles.
- **FR-008**: The system MUST prevent canary-targeted activations from affecting non-eligible nodes.
- **FR-009**: The system MUST support advanced integrity policy configuration requiring package trust enforcement and integrity verification before activation.
- **FR-010**: The system MUST block activation of packages that fail required integrity policy checks and record the failed policy condition.
- **FR-011**: The system MUST expose an optional administrative capability surface for viewing package inventory, runtime state, reconciliation status, and health state.
- **FR-012**: The administrative capability surface MUST support on-demand reconciliation requests when invoked through a host-authorized administrative boundary.
- **FR-012a**: Unauthorized or unavailable administrative reconcile requests MUST produce explicit non-mutating outcome codes and correlation-linked diagnostics.
- **FR-013**: Administrative views MUST present a consistent snapshot of active/staged package status and last reconcile outcomes.
- **FR-014**: Channel, rollout, and integrity policy configuration changes MUST become effective at the next reconciliation cycle without requiring unsafe state mutation.
- **FR-015**: If any rollout, integrity, or policy check fails for a package, the failure MUST be isolated to impacted package/node scope and MUST NOT force unrelated package activations to fail.
- **FR-016**: The system MUST define bounded cleanup/retention behavior for historical versions while protecting versions required for fallback.

### Operational & Safety Requirements *(mandatory)*

- **OSR-001**: Reconciliation/apply flows MUST be idempotent for repeated identical inputs.
- **OSR-002**: Staging, promotion, and activation MUST follow transactional safety semantics and preserve last-known-good state on failure.
- **OSR-002a**: Regression coverage MUST verify non-mutating failure isolation across promotion, canary, and integrity failure paths for unaffected package/node scopes.
- **OSR-003**: Only explicitly configured channels, feeds, and policy-approved packages MAY influence activation; secrets and credentials MUST be handled outside source control.
- **OSR-004**: Every reconciliation cycle MUST emit correlation-linked logs, metrics, and health signals covering channel scope, staged/promoted counts, canary progression, integrity-policy failures, and manual trigger outcomes.
- **OSR-004b**: Every failed channel, promotion, canary, integrity, and administrative operation MUST emit an observer event with correlation ID, scoped target (package/node/channel), and reason code, in addition to logs/metrics/health signals.
- **OSR-004a**: Health signaling MUST report degraded (not healthy) for cycles where selected channel configuration is missing/empty and no mutations are performed.
- **OSR-005**: The feature MUST include automated unit, integration, and contract-level tests for channel isolation, staged promotion, canary limits, integrity enforcement, administrative operations, and non-mutating failure behavior.

### Key Entities *(include if feature involves data)*

- **Channel Policy**: Declares environment scope (production/staging/canary), eligible desired-state sources, and activation boundaries.
- **Staged Release Candidate**: Represents a resolved package version prepared for activation, including readiness status and promotion metadata.
- **Canary Rollout Plan**: Defines node eligibility set, current rollout percentage, progression state, and observed rollout outcomes.
- **Canary Selection Input**: Canonical input set used for deterministic canary node selection, including rollout identifier, eligible node identities, and target percentage.
- **Integrity Rule Set**: Represents trust and validation requirements that packages must satisfy before activation.
- **Operational Snapshot**: Operator-facing view of package inventory, active/staged status, reconcile history, and current health state.

## Assumptions

- Node identity and eligibility metadata required for canary targeting are available from host/runtime environment inputs.
- Administrative capabilities are optional and may be disabled in environments that do not expose operational control interfaces.
- Authorization/authentication for administrative actions is supplied by host integrations and is out of scope for this feature specification.
- Channel-aware desired-state inputs are provided consistently by configured sources at reconciliation time.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: In a validation run with three channels and distinct desired package sets, 100% of activation actions remain within the selected channel scope across 20 consecutive reconciliation cycles.
- **SC-002**: For staged rollout scenarios, 100% of staged package versions remain inactive until promotion is triggered, and 100% of promoted versions switch to active state atomically with fallback preserved on injected failure.
- **SC-003**: In canary validation with a defined eligible node set, 100% of activations occur only on eligible nodes, and non-eligible nodes experience 0 unintended canary activations.
- **SC-004**: In integrity validation using a mixed compliant/non-compliant package set, 100% of non-compliant packages are blocked from activation while compliant packages remain eligible.
- **SC-005**: In acceptance validation, for the two-step workflow (read operational snapshot and trigger reconcile), at least 95 of 100 attempts MUST complete end-to-end within 120 seconds.
