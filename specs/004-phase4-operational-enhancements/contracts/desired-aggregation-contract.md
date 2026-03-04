# Contract: Desired Aggregation (Multi-Source Determinism)

## Interface Boundary

- Inputs:
  - one or more configured `IDesiredPackageSource` inputs
  - optional desired manifest source
  - correlation ID
- Output:
  - an aggregated deterministic desired set (requests)
  - duplicate-resolution outcomes (when applicable)
  - degraded outcomes for unavailable sources

## Determinism Rules

- Source evaluation order MUST be deterministic and configuration-defined.
- Duplicate package IDs across sources MUST be resolved by explicit precedence + stable tie-break, never runtime enumeration order.
- Resulting package request sequence MUST be stable for identical inputs.

## Behavioral Contract

- Aggregation MUST be deterministic for identical inputs.
- Duplicate package IDs across sources MUST be resolved deterministically using explicit tie-break rules.
- Unavailable sources MUST not corrupt active/LKG and MUST not force unrelated packages to fail.

## Error Contract

- Source outage MUST emit explicit reason codes and degraded health signals.
- Any error MUST emit a failure observer event in addition to logs/metrics/health.
- Aggregation failure for one source/package MUST remain scoped and non-mutating for unaffected packages.

## Test Contract

- Verify duplicate tie-break is deterministic and stable across cycles.
- Verify one source outage produces degraded outcome and non-mutating behavior for impacted requests.
- Verify identical multi-source inputs across two replicas project the same aggregated request set.
