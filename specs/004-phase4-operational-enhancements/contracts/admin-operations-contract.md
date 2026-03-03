# Contract: Admin Operations (Optional)

## Interface Boundary

- Admin read operations:
  - read operational snapshot (active packages, last reconcile outcome, health)
- Admin trigger operations:
  - request on-demand reconciliation

Authentication/authorization is host-supplied and out of scope.

## Behavioral Contract

- Admin reads MUST return a consistent snapshot.
- Manual reconcile triggers MUST execute through a host-authorized boundary.
- Trigger outcomes MUST be observable (correlation-linked logs/metrics/health and observer failure events on failure).

## Error Contract

- Unauthorized/unavailable reconcile trigger MUST produce explicit non-mutating outcome codes and diagnostics.

## Test Contract

- Verify snapshot consistency and correctness.
- Verify manual reconcile trigger outcomes are observable.
- Verify rejection/unavailable cases emit explicit outcome codes and failure events.
