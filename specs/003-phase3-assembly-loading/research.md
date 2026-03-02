# Phase 0 Research — Phase 3 Optional Package Loading

## Decision 1: Per-package isolated collectible load contexts
- Decision: Create one collectible package load context per active package session and do not share package-local dependencies across package contexts.
- Rationale: Preserves deterministic isolation, prevents cross-package dependency conflicts, and enables package-scoped unload lifecycle.
- Alternatives considered: Single shared custom context (rejected: larger blast radius, version collision risk), default-context loading only (rejected: no unload control).

## Decision 2: Deterministic dependency resolution order
- Decision: Resolve package assemblies with explicit order: shared-contract policy match first, then package-local `AssemblyDependencyResolver`, then approved framework/shared fallback; otherwise fail explicitly.
- Rationale: Produces reproducible load behavior and avoids accidental cross-context binding.
- Alternatives considered: Permissive default-context fallback (rejected: nondeterministic leakage), path-probing only (rejected: brittle and incomplete).

## Decision 3: Shared assembly identity policy
- Decision: Match shared contract assemblies by strong identity (`Name + PublicKeyToken + MajorVersion`) as the default policy.
- Rationale: Maintains type identity safety while allowing minor/patch servicing compatibility.
- Alternatives considered: Name-only matching (rejected: spoofing/misbinding risk), full exact version identity (rejected as default: operationally rigid for patch/minor updates).

## Decision 4: Unload semantics as best-effort lifecycle
- Decision: Treat unload as cooperative and asynchronous; model explicit lifecycle states and do not assume immediate completion after unload initiation.
- Rationale: .NET unload completion depends on absence of live roots and cannot be guaranteed synchronously.
- Alternatives considered: Immediate unload success assumption (rejected: incorrect), blocking until guaranteed completion (rejected: can starve reconciliation).

## Decision 5: Bounded deactivation and retry behavior
- Decision: Use configurable bounded deactivation timeout before unload attempt; on timeout continue processing, record timeout outcome, mark `UnloadPending` when needed, and retry pending unload every reconciliation cycle.
- Rationale: Aligns with clarified spec behavior, keeps cycles deterministic, and avoids indefinite stalls.
- Alternatives considered: Indefinite wait (rejected: unbounded cycle time), manual-only retry (rejected: slower convergence), cycle-skipping backoff (rejected: conflicts with clarified retry cadence).

## Decision 6: Health and observability envelope
- Decision: Emit correlation-linked structured logs/metrics for load/unload/deactivation-timeout outcomes; degrade health whenever any package is `UnloadPending`.
- Rationale: Provides immediate operator visibility and actionable diagnostics without extra debug modes.
- Alternatives considered: Logs-only diagnostics (rejected: weak alertability), healthy state while pending unload exists (rejected: hides operational risk).

## Decision 7: Non-blocking per-package failure isolation
- Decision: Process package load/unload outcomes independently within a cycle so one package failure does not block unrelated package processing.
- Rationale: Preserves forward progress and availability in partial-failure conditions.
- Alternatives considered: Fail-fast cycle behavior (rejected: excessive blast radius).

## Decision 8: Source and supply-chain boundary for loading
- Decision: Restrict loading inputs to active store package paths plus explicitly configured shared assembly identities; do not introduce new credential or remote source behavior in Phase 3 loading.
- Rationale: Preserves constitution supply-chain boundary and minimizes attack surface.
- Alternatives considered: Additional runtime probing locations (rejected: trust boundary expansion).

## Decision 9: Test and contract strategy
- Decision: Require unit tests for identity matching and lifecycle transitions, integration tests for runtime-loading boundary behavior, and contract tests for loading/unload/observability surfaces including regression cases.
- Rationale: Satisfies constitution test discipline for runtime mutation boundaries and prevents regressions in high-risk unload paths.
- Alternatives considered: Unit-only coverage (rejected: weak boundary confidence), integration-only coverage (rejected: poor fault localization).
