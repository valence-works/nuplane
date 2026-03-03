# Phase 0 Research — Phase 4 Cluster-Convergent Runtime Loading (Lean)

## Decision 1: Convergence model (no distributed coordination)
- Decision: Each replica reconciles independently against shared desired-state inputs and a node-local store. Cluster convergence is achieved via deterministic desired inputs, not via leader election or distributed locks.
- Rationale: Keeps Nuplane host-neutral and avoids embedding distributed systems primitives.
- Alternatives considered: leader-based reconciliation (rejected: adds cluster coupling), shared distributed store (rejected: much larger scope/risk).

## Decision 2: Shared desired manifest (exact versions)
- Decision: Add an optional shared desired manifest input that pins exact package versions.
- Rationale: Exact versions provide deterministic convergence, simplify troubleshooting, and avoid version-range drift across replicas.
- Alternatives considered: version ranges in manifest (deferred: requires deterministic resolution policy/lock behavior), “latest” semantics (rejected: nondeterministic without additional controls).

## Decision 3: Manifest update protocol (upload then manifest)
- Decision: Recommend an update pattern where package blobs are uploaded first, and the manifest is written/overwritten last.
- Rationale: Minimizes “manifest references missing package” windows and makes failures cleanly retryable.
- Alternatives considered: implicit discovery by listing storage (rejected: listing can be eventually consistent/unordered, complicates determinism).

## Decision 4: Multi-source desired aggregation (deterministic tie-break)
- Decision: Support multiple desired sources but require deterministic ordering and explicit tie-break rules for duplicates.
- Rationale: Prevents “flip-flopping” desired sets across cycles and makes behavior testable.
- Alternatives considered: first-come runtime ordering (rejected: nondeterministic), error on duplicates only (deferred: too strict for early adopters).

## Decision 5: Triggering reconciliation (polling + explicit)
- Decision: Keep polling as the robustness baseline and add explicit triggers (in-process and optional REST) for near real-time updates.
- Rationale: Polling ensures eventual convergence even if triggers fail; explicit triggers reduce operator feedback loop time after uploads.
- Alternatives considered: triggers-only (rejected: brittle), short polling only (rejected: unnecessary overhead).

## Decision 6: Loader boundary (optional module)
- Decision: Provide an optional Loader SDK integration boundary (separate module) to load assemblies/types/services from active packages.
- Rationale: Keeps Nuplane core host-neutral while enabling end-to-end runtime extensibility where desired.
- Alternatives considered: mandate loading in core runtime (rejected: violates optionality), define a plugin model (rejected: out of scope).

## Decision 7: Observability and failure surfacing
- Decision: For manifest/source/acquisition/loader/admin failures, require correlation-linked logs/metrics/health plus explicit observer failure events with scoped targets and reason codes.
- Rationale: Operational triage requires more than logs; events make failure handling reliable for host integrations.
- Alternatives considered: logs-only (rejected: weak automation), metrics-only (rejected: poor diagnosability).

## Decision 8: Admin surface is optional and host-authorized
- Decision: Provide an optional admin surface for read snapshot + trigger reconcile; authentication/authorization is host-supplied.
- Rationale: Keeps Nuplane host-neutral and usable in different environments.
- Alternatives considered: embed auth model (rejected: host-specific), no admin surface (rejected: reduces usability/operability).

## Decision 9: Testing strategy
- Decision: Require unit tests for determinism (manifest + aggregation) and integration tests for degraded non-mutating behavior and admin/loader boundaries.
- Rationale: Determinism and failure isolation are easy to regress without explicit tests.
- Alternatives considered: unit-only (rejected: misses boundary failures), integration-only (rejected: poor failure localization).
