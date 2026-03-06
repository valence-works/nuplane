# Contract: Admin Operations (Optional)

## Interface Boundary

- Admin read operations:
  - read operational snapshot (active packages, last reconcile outcome, health)
- Admin trigger operations:
  - request on-demand reconciliation
- Surfaces:
  - in-process hosting contract (`INuplaneAdminOperations` style boundary)
  - optional ASP.NET Core HTTP endpoints (separate optional package)

Authentication/authorization is host-supplied and out of scope.

## Behavioral Contract

- Admin reads MUST return a consistent snapshot.
- Manual reconcile triggers MUST execute through a host-authorized boundary.
- Trigger outcomes MUST be observable (correlation-linked logs/metrics/health and observer failure events on failure).
- Trigger API MUST return explicit outcome code (`Accepted`, `Rejected`, `Unavailable`, `Completed`) and correlation context.

## Error Contract

- Unauthorized/unavailable reconcile trigger MUST produce explicit non-mutating outcome codes and diagnostics.
- Read failures MUST not mutate runtime/package state.

## Test Contract

- Verify snapshot consistency and correctness.
- Verify manual reconcile trigger outcomes are observable.
- Verify rejection/unavailable cases emit explicit outcome codes and failure events.
- Verify HTTP and in-process surfaces project consistent operational snapshot fields.
