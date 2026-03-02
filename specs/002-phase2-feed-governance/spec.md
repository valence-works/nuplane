# Feature Specification: Phase 2 Advanced Feeds & Governance

**Feature Branch**: `002-phase2-feed-governance`  
**Created**: 2026-03-02  
**Status**: Draft  
**Input**: User description: "Please verify that Phase 1 of the Roadmap (roadmap.md) has been completed and if so, create spec 0002 based on Phase 2 of the roadmap."

## Clarifications

### Session 2026-03-02

- Q: For cleanup when both retention count and age policies are configured, how is retention determined? → A: Keep versions that satisfy either retention rule (union retention).
- Q: In strict feed mode when a required feed is unavailable, what should fail? → A: Fail only packages requiring that feed; continue other packages.
- Q: When feeds have equal priority and contain the same matching version, what tie-breaker is used? → A: Select the feed with lexicographically smallest feed name.
- Q: For untrusted feeds, how should explicit override be scoped? → A: Per-package (or per-feed-rule) override with required reason.
- Q: In dry-run mode, should trust validators and lock-file checks execute? → A: Yes, run all checks and report outcomes; do not mutate state.

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Deterministic Multi-Feed Resolution (Priority: P1)

As an operator, I can configure multiple package feeds with priority and trust classification so package reconciliation remains deterministic and predictable across internal and external sources.

**Why this priority**: Multi-feed deterministic resolution is the primary Phase 2 outcome and enables safe enterprise package sourcing.

**Independent Test**: Can be fully tested by configuring at least three feeds with overlapping package availability, running repeated reconciliations, and verifying identical selected versions/feed origins for identical inputs.

**Acceptance Scenarios**:

1. **Given** a package request without a feed name and matching versions across multiple feeds, **When** reconciliation runs, **Then** the selected version and source are deterministic and consistent with configured feed priority rules.
2. **Given** a package request with an explicit feed name, **When** reconciliation runs, **Then** only that feed is considered for resolution.
3. **Given** a higher-priority feed outage, **When** strict feed mode is disabled, **Then** reconciliation falls back according to policy without corrupting active state.

---

### User Story 2 - Governance and Reproducibility Controls (Priority: P2)

As a platform owner, I can enforce feed trust policies and lock-file behavior so deployments are reproducible and only policy-compliant packages become active.

**Why this priority**: Governance and reproducibility reduce supply-chain risk and prevent unintended package drift in production.

**Independent Test**: Can be fully tested by applying trusted/restricted/untrusted feed classifications and lock-file modes (generate, enforce, strict), then validating expected pass/fail outcomes and identical reproducible activation sets.

**Acceptance Scenarios**:

1. **Given** a package from a restricted feed, **When** validator checks fail, **Then** activation is rejected and a policy failure is recorded.
2. **Given** enforce lock mode with an existing lock file, **When** feed versions change, **Then** reconciliation still activates the lock-file-defined versions.
3. **Given** strict lock mode and a missing lock entry for a desired package, **When** reconciliation runs, **Then** the cycle fails that package deterministically with an explicit lock compliance error.

---

### User Story 3 - Controlled Expansion and Retention Safety (Priority: P3)

As an operator, I can use feed-based rule discovery with hard limits and dry-run output, and apply cleanup policies that control disk growth while preserving rollback safety.

**Why this priority**: Controlled wildcard discovery and safe cleanup are required to scale operations without runaway ingestion or unsafe deletion.

**Independent Test**: Can be fully tested by executing rule-based discovery with limits and dry-run enabled, then running post-success cleanup and verifying retention rules with last-known-good protection.

**Acceptance Scenarios**:

1. **Given** feed-rule desired discovery with prefix and max-package constraints, **When** dry-run is executed, **Then** a deterministic change set is produced without applying package mutations.
2. **Given** cleanup retention limits after successful reconciliation, **When** old versions are eligible, **Then** eligible historical versions are removed while all last-known-good versions remain protected.
3. **Given** cleanup encounters a deletion failure, **When** maintenance continues, **Then** runtime health and active package set remain stable and the cleanup error is observable.

### Edge Cases

- Multiple feeds provide the same package and version while having equal priority; system applies deterministic tie-breaking and records selected source.
- Lock file hash does not match downloaded artifact; package activation is rejected and active state remains unchanged.
- Feed-rule discovery attempts to exceed configured max package count; system blocks additional candidates and records limit enforcement.
- Untrusted feed is configured but no explicit override exists; packages from that feed are rejected before activation.
- Manual-only cleanup mode is configured; no automated deletion occurs after reconciliation.

## Requirements *(mandatory)*

### Assumptions

- Phase 1 baseline behavior remains available and unchanged unless explicitly expanded by this Phase 2 scope.
- Operators define feed order and trust levels as part of configuration.
- Desired state in Phase 2 can be sourced from explicit requests, directory source, and feed rule-based discovery.
- Lock mode applies to package identity, source, and integrity constraints for deterministic reproducibility.

### Functional Requirements

- **FR-001**: System MUST support configuring multiple package feeds with priority ordering and trust classification.
- **FR-002**: System MUST support per-request explicit feed targeting and all-feed resolution when feed is unspecified.
- **FR-003**: System MUST resolve package candidates deterministically across eligible feeds for identical inputs; when priority and version are equal, tie-break by lexicographically smallest feed name.
- **FR-004**: System MUST support strict and fallback feed outage behavior based on configured policy; in strict mode, packages that require an unavailable feed MUST fail explicitly while unrelated packages continue.
- **FR-005**: System MUST enforce feed trust policy levels (`Trusted`, `Restricted`, `Untrusted`) during package eligibility and activation decisions.
- **FR-006**: System MUST require restricted-feed packages to pass configured validator checks before activation.
- **FR-007**: System MUST prevent untrusted-feed package activation unless an explicit per-package or per-feed-rule override is configured with a required operator-provided reason.
- **FR-008**: System MUST support lock-file generate mode that records resolved package identity, source, integrity hash, and timestamp.
- **FR-009**: System MUST support lock-file enforce mode that uses lock-defined package versions instead of live range resolution.
- **FR-010**: System MUST support lock-file strict mode that fails reconciliation for missing lock entries.
- **FR-011**: System MUST fail package activation on lock integrity hash mismatch.
- **FR-012**: System MUST support feed rule-based desired-state discovery using prefix matching and required hard package count limits.
- **FR-013**: System MUST support dry-run reconciliation that runs full policy, validator, and lock-file checks and produces deterministic outcomes/change sets without applying state mutations.
- **FR-014**: System MUST support cleanup policies for retaining last N versions, retaining versions younger than a configured age threshold, and manual-only cleanup; when count- and age-based retention are both configured, versions satisfying either rule MUST be retained.
- **FR-015**: System MUST ensure last-known-good versions are never deleted by cleanup operations.
- **FR-016**: System MUST execute automatic cleanup only after successful reconciliation cycles when automatic cleanup is enabled.

### Operational & Safety Requirements *(mandatory)*

- **OSR-001**: Reconciliation across multi-feed, lock, rule-discovery, and dry-run paths MUST remain deterministic and idempotent for repeated identical inputs.
- **OSR-002**: Package activation and removal flows MUST preserve transactional last-known-good safety and MUST NOT leave unknown active state on failures.
- **OSR-003**: Only explicitly configured desired sources and feeds MAY influence reconciliation, and integrity checks MUST run before activation in accordance with trust policy.
- **OSR-004**: Each reconciliation cycle MUST emit structured logs with correlation identifiers, including selected feed decisions, policy outcomes, lock-mode decisions, cleanup outcomes, and untrusted-override reason metadata when used.
- **OSR-005**: Metrics and health signals MUST distinguish healthy/degraded status and include feed availability issues, policy violations, lock mismatches, and cleanup failures.
- **OSR-006**: Feed outages, validator failures, and cleanup failures MUST be isolated and recorded without corrupting store state.
- **OSR-007**: Credentials and secret material for feed access MUST be supplied through secure runtime configuration and MUST NOT be committed to source control.
- **OSR-008**: Automated testing MUST cover deterministic feed resolution, trust enforcement, lock-mode behavior, dry-run behavior, and cleanup/LKG protection, including regression tests for each failure mode addressed.
- **OSR-009**: Boundary contracts between runtime, feed resolver, trust validator, lock coordinator, and store cleanup components MUST be verified by integration or contract tests before release.

### Key Entities *(include if feature involves data)*

- **Feed Definition**: Configured package source with name, endpoint, trust level, and priority metadata used during resolution.
- **Feed Resolution Decision**: Per-package record of candidate feeds, chosen feed, and deterministic tie-break rationale for one cycle.
- **Trust Policy Evaluation**: Validation outcome record for package eligibility based on feed trust level and validator results.
- **Lock Entry**: Immutable reproducibility record containing package identity, resolved version, source feed, integrity hash, and capture time.
- **Feed Rule**: Operator-defined desired-state discovery rule with feed target, ID prefix constraints, version strategy, and hard package limits.
- **Cleanup Policy**: Retention configuration defining automatic or manual cleanup behavior and protected version constraints.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: In repeated reconciliation tests with identical inputs across at least three feeds, 100% of cycles produce identical selected package versions and source selections.
- **SC-002**: In strict trust-policy validation tests, 100% of restricted-feed validation failures and untrusted-feed non-override attempts are blocked before activation.
- **SC-003**: In lock enforce-mode tests where feed versions drift, 100% of reconciliations activate the lock-defined package set without unintended version changes.
- **SC-004**: In strict lock-mode tests with missing lock entries or hash mismatches, 100% of affected packages fail with explicit policy/integrity outcomes and no active-state corruption.
- **SC-005**: In controlled feed-rule discovery tests, 100% of runs enforce configured max-package limits and produce dry-run diffs without mutating active package state.
- **SC-006**: In cleanup validation tests, 100% of protected last-known-good versions remain present while at least 95% of eligible non-protected stale versions are removed successfully.
