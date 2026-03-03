# Phase 0 Research — Phase 4 Operational Enhancements

## Decision 1: Channel selection and isolation semantics
- Decision: Reconciliation and activation execute strictly within the selected channel scope (`prod`, `staging`, `canary`) and never cross-activate packages from other channels.
- Rationale: Enforces environment segmentation and prevents cross-environment contamination.
- Alternatives considered: Shared global desired set with channel tags (rejected: higher accidental activation risk), implicit default-channel fallback (rejected: hides config errors).

## Decision 2: Empty/unconfigured channel behavior
- Decision: When selected channel has no configured desired sources, run a non-mutating cycle and emit degraded health with explicit misconfiguration outcome.
- Rationale: Preserves store/runtime safety while surfacing actionable operator diagnostics.
- Alternatives considered: Hard cycle abort (rejected: unnecessary operational disruption), reporting healthy no-op (rejected: masks configuration defect).

## Decision 3: Staged rollout promotion trigger
- Decision: Promotion from staged candidate to active requires explicit operator action only.
- Rationale: Keeps release intent auditable and minimizes accidental activation from noisy readiness signals.
- Alternatives considered: automatic readiness promotion (rejected: less operator control), dual-mode per-channel promotion (deferred: increases policy complexity for Phase 4 baseline).

## Decision 4: Promotion failure isolation
- Decision: Promotion failure keeps current active version unchanged, marks staged candidate as failed, and continues unrelated package/node operations in the same cycle.
- Rationale: Aligns with transactional safety + bounded blast radius while preserving forward progress.
- Alternatives considered: fail-fast whole-cycle abort (rejected: broad impact), all-or-nothing global rollback (rejected: unnecessary coupling across independent updates).

## Decision 5: Canary node selection strategy
- Decision: Percentage-based canary rollout uses deterministic stable hashing over canonical selection input (`rolloutId`, sorted eligible node IDs, target percentage).
- Rationale: Guarantees idempotent node selection for identical inputs and avoids node flapping.
- Alternatives considered: random selection each cycle (rejected: nondeterministic), explicit allowlist only (rejected: no gradual rollout capability).

## Decision 6: Canary progression model
- Decision: Canary progression is monotonic within a rollout (percentage can increase in steps; selected node set expands deterministically).
- Rationale: Supports controlled expansion and reproducible rollout evidence.
- Alternatives considered: bidirectional percentage changes by default (rejected: operational ambiguity), ad hoc per-cycle overrides (rejected: weak auditability).

## Decision 7: Advanced integrity enforcement boundary
- Decision: Activation gate enforces trust policy + required integrity verification (hash/signature where configured); failing packages are non-mutating and produce explicit policy-failure outcomes.
- Rationale: Protects supply-chain boundary and preserves LKG on validation failure.
- Alternatives considered: warning-only policy failures (rejected: insufficient governance), post-activation verification (rejected: too late for safety guarantees).

## Decision 8: Optional admin surface contract
- Decision: Phase 4 defines an optional administrative interface surface for package/state/health reads and manual reconcile trigger; host integration provides authentication/authorization.
- Rationale: Keeps Nuplane host-neutral while enabling operator visibility/control.
- Alternatives considered: mandatory admin package in core runtime (rejected: violates optionality), no operator trigger surface (rejected: weak operability).

## Decision 9: Observability model
- Decision: Each cycle emits correlation-linked logs + metrics + health signals for channel scope, staged/promoted outcomes, canary selection/progression, integrity policy failures, and manual trigger results.
- Rationale: Enables triage and auditability for release governance decisions.
- Alternatives considered: logs-only telemetry (rejected: weak alerting), metrics-only telemetry (rejected: low diagnosability).

## Decision 10: Test and contract strategy
- Decision: Require unit tests for channel/promotion/canary/integrity logic, boundary integration tests across runtime-store-nuget-host surfaces, and contract tests for admin/read and reconcile trigger behavior.
- Rationale: Satisfies constitution test discipline for reconciliation and boundary changes.
- Alternatives considered: unit-only testing (rejected: misses boundary failures), integration-only testing (rejected: poor failure localization).
