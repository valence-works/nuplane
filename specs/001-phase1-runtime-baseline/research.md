# Phase 0 Research — Phase 1 Runtime Baseline

## Decision 1: Runtime and package architecture
- Decision: Use a multi-package .NET 8 class-library architecture (`Nuplane.Runtime`, `Nuplane.Store`, `Nuplane.NuGet`, `Nuplane.Sources.Directory`, `Nuplane.Hosting`, minimal `Nuplane.Abstractions`).
- Rationale: Preserves host neutrality, isolates responsibilities, and matches roadmap package boundaries.
- Alternatives considered: Single monolithic runtime library (rejected: weaker boundary testing and contract discipline).

## Decision 2: Reconciliation concurrency model
- Decision: Enforce single-flight reconciliation; skip/log overlapping triggers.
- Rationale: Prevents concurrent store/state mutation races and simplifies deterministic behavior.
- Alternatives considered: Queue-one model (rejected for Phase 1 complexity), concurrent cycles with locks (rejected as higher race/complexity risk).

## Decision 3: Duplicate desired-input conflict policy
- Decision: For duplicate package IDs in one cycle, use highest-version-wins with deterministic tie-break by source name.
- Rationale: Provides deterministic convergence while avoiding unnecessary cycle/package aborts.
- Alternatives considered: Fail conflicting package (rejected: lower availability), source-priority-wins (rejected: less intuitive for version progression).

## Decision 4: Desired source outage behavior
- Decision: If a source is unavailable, reuse that source’s last successful snapshot for the cycle, continue processing, and mark degraded.
- Rationale: Maintains forward progress and host stability while signaling risk explicitly.
- Alternatives considered: Abort full cycle (rejected: availability impact), treat source as empty (rejected: unsafe removals).

## Decision 5: Health recovery semantics
- Decision: Return from degraded to healthy only after a fully successful cycle with fresh reads from all configured sources.
- Rationale: Avoids false healthy states based on stale snapshots or partial success.
- Alternatives considered: Any successful cycle clears degraded (rejected: can mask source outages), manual acknowledgment required (rejected for Phase 1 operational burden).

## Decision 6: Supply-chain trust boundary
- Decision: Enforce strict package ID allowlist for desired inputs and reject non-allowlisted IDs pre-resolution.
- Rationale: Strong least-trust baseline aligned with constitution integrity requirements.
- Alternatives considered: Denylist-only (rejected: permissive by default), source-only trust without ID filter (rejected: wider attack surface).

## Decision 7: Store transaction and recovery model
- Decision: Keep per-package transaction flow as `stage -> validate -> publish immutable -> atomic switch -> persist state`, with explicit LKG fallback.
- Rationale: Guarantees no partial activation corruption and preserves host continuity on failure.
- Alternatives considered: In-place updates (rejected: corruption risk), cycle-wide atomicity (rejected: excessive coupling for Phase 1).

## Decision 8: Observability baseline
- Decision: Require correlation ID per cycle, structured logs, baseline metrics, and explicit healthy/degraded health signal.
- Rationale: Enables operational diagnosis and aligns with measurable success criteria.
- Alternatives considered: Logs-only baseline (rejected: weaker operability), metrics without correlation IDs (rejected: lower traceability).

## Decision 9: Testing and contract strategy
- Decision: Require unit tests for diff/transaction logic, boundary integration tests across runtime-store/nuget/source, and regression tests for failure/LKG cases.
- Rationale: Matches constitution test-discipline gate and protects high-risk runtime mutation paths.
- Alternatives considered: Integration-only strategy (rejected: slower fault localization), unit-only strategy (rejected: insufficient boundary confidence).
