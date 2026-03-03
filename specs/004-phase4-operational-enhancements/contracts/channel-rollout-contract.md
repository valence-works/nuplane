# Contract: Channel & Rollout Boundary

## Interface Boundary
- Runtime accepts channel-scoped reconciliation input containing:
  - selected `channelName`
  - channel-specific desired package set
  - staged release candidates for that channel
  - optional canary rollout plan
  - correlation ID
- Runtime returns cycle outcomes containing:
  - channel evaluation result (`Applied`, `NoOp`, `DegradedMisconfigured`)
  - staged candidate state transitions
  - canary selection/progression results
  - per-package activation outcomes

## Behavioral Contract
- Channel isolation is strict: evaluation and activation are limited to selected channel scope.
- Empty/unconfigured channel performs non-mutating cycle and reports degraded with explicit reason code.
- Staged candidates remain inactive until explicit operator promotion request.
- Promotion failure is isolated to impacted package/node scope and does not block unrelated operations.

## Error Contract
- Missing channel configuration emits `channel.misconfigured` outcome with correlation ID.
- Promotion conflict or missing staged candidate emits explicit rejection/failure code.
- Any failure must preserve active and LKG pointers for unaffected package/node scopes.

## Test Contract
- Verify channel isolation across `prod`, `staging`, and `canary` with disjoint desired sets.
- Verify empty channel configuration yields non-mutating degraded cycle.
- Verify explicit promotion request requirement and inactive staging behavior.
- Verify isolated promotion failure continuation for unrelated package/node operations.
