# Phase 0 Research — Phase 4 Cluster-Convergent Runtime Loading (Lean)

## Technical Clarifications Resolved

- Runtime stack is .NET multi-targeted (`net8.0;net9.0;net10.0`) with Microsoft.Extensions options/hosting/logging patterns.
- Reconciliation storage model is node-local filesystem-based store + metadata pointers (no distributed transaction store).
- Test strategy uses xUnit/NSubstitute with targeted unit + integration projects already present in repository.

No unresolved `NEEDS CLARIFICATION` items remain.

## Decisions

### 1) Deterministic convergence without coordination primitives
- Decision: Each replica independently reconciles shared desired-state inputs to a node-local store; convergence emerges from deterministic desired-state processing, not lock-step distributed control.
- Rationale: Preserves host neutrality and keeps runtime infrastructure lightweight while meeting `SC-001` convergence expectations.
- Alternatives considered: leader election or distributed locking (rejected: added operational coupling and failure modes); centralized active-state store (rejected: broader scope than Phase 4).

### 2) Shared desired manifest with exact version pins
- Decision: Manifest format requires exact package versions and deterministic ordering/projection.
- Rationale: Exact pins eliminate resolver drift and make replica output reproducible.
- Alternatives considered: version ranges (deferred: requires lock/selection policy); floating/latest semantics (rejected: non-deterministic).

### 3) Deterministic multi-source aggregation policy
- Decision: Aggregate multiple `IDesiredPackageSource` inputs with explicit source precedence and stable duplicate tie-break reason codes.
- Rationale: Guarantees identical desired inputs produce identical desired package sets across nodes.
- Alternatives considered: runtime enumeration order (rejected: unstable); fail-on-duplicate hard stop (deferred: too strict for initial adoption).

### 4) Trigger model combines periodic and explicit invocation
- Decision: Keep startup + periodic polling as baseline driver and add explicit in-process/admin trigger surfaces.
- Rationale: Polling ensures eventual convergence; explicit trigger shortens operator feedback loops.
- Alternatives considered: trigger-only model (rejected: brittle if trigger path unavailable); high-frequency polling only (rejected: unnecessary load/latency trade-off).

### 5) Transactional/LKG-first failure handling
- Decision: Enforce stage → validate → publish immutable version → atomic active switch; failures preserve LKG and remain non-mutating for impacted packages.
- Rationale: Satisfies constitution principle II and `OSR-002/OSR-003` safety requirements.
- Alternatives considered: in-place overwrite activation (rejected: risks partial/corrupt active state).

### 6) Optional loader integration boundary
- Decision: Define optional loader boundary interface in runtime with adapter into `Nuplane.Loading` package.
- Rationale: Delivers runtime loading capability without forcing host/plugin semantics into core runtime.
- Alternatives considered: mandatory loader in core runtime (rejected: violates optionality); introducing plugin model contracts (rejected: explicitly out-of-scope).

### 7) Optional admin operational surface with host-owned auth
- Decision: Provide optional read snapshot + manual reconcile operations in hosting/admin packages; authorization concerns remain host-supplied.
- Rationale: Enables operability while keeping framework host-neutral.
- Alternatives considered: built-in authz scheme (rejected: host-specific); no admin API (rejected: poor operational UX).

### 8) Observability contract as first-class output
- Decision: Every cycle and every failure mode emits correlation-linked structured logs, baseline metrics, health transitions, and observer failure events with scoped target + reason code.
- Rationale: Required for diagnosability and aligns with constitution principle IV.
- Alternatives considered: logs-only and metrics-only approaches (both rejected due to incomplete operator signal coverage).

### 9) Validation through .NET options pipeline
- Decision: Validation rules implemented via `IValidateOptions<T>` and startup fail-fast via `ValidateOnStart()` for required Phase 4 options.
- Rationale: Aligns with constitution section VII and prevents late runtime misconfiguration failures.
- Alternatives considered: options object `IsValid()` methods (rejected: policy/data coupling and weak DI composition).

### 10) Test evidence strategy
- Decision: Add unit tests for manifest and aggregation determinism + policy gates, and integration/regression tests for degraded non-mutating behavior, loader isolation, and admin trigger outcomes.
- Rationale: Covers deterministic core behavior and boundary regressions mandated by constitution principle V.
- Alternatives considered: unit-only or integration-only suites (both rejected as incomplete).
