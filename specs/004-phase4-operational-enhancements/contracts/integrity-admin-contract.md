# Contract: Integrity Gate & Admin Operations

## Interface Boundary
- Integrity gate input:
  - package identity/version
  - source trust context
  - configured integrity ruleset
  - correlation ID
- Integrity gate output:
  - `Passed` or `Failed`
  - failure reason code(s)
  - activation eligibility decision
- Optional admin operations:
  - read package inventory/state/health snapshot
  - request on-demand reconciliation

## Behavioral Contract
- Required integrity checks in enforce mode must complete before activation.
- Failed integrity evaluation blocks activation and preserves active/LKG state.
- Admin read operations return a consistent snapshot for one correlation scope.
- Manual reconcile trigger outcome is observable in logs/metrics/snapshot fields.

## Error Contract
- Integrity rule evaluation errors emit explicit policy failure outcomes.
- Failed integrity checks are non-mutating for activation state.
- Manual reconcile rejection/unavailability emits explicit admin operation outcome code.

## Test Contract
- Verify non-compliant packages are blocked while compliant packages remain eligible.
- Verify failed integrity checks do not mutate active pointer or LKG state.
- Verify admin snapshot consistency for active/staged/health data.
- Verify manual reconcile trigger emits observable completion/failure outcomes.
