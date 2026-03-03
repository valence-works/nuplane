# Contract: Unload Lifecycle and Health

## Interface Boundary
- On package removal, runtime invokes deactivation+unload boundary with:
  - package identity/session
  - configurable deactivation timeout
  - correlation ID
- Boundary returns:
  - deactivation outcome (completed/timed out)
  - unload outcome (`Unloaded` or `UnloadPending`)
  - reason codes and timestamps

## Behavioral Contract
- Removal sequence is deterministic:
  1. Request host deactivation
  2. Wait up to configured timeout
  3. Attempt unload
  4. Emit outcome
- If deactivation times out, unload attempt still occurs and timeout is reported.
- `UnloadPending` is retried every reconciliation cycle until success.
- Presence of any `UnloadPending` package sets health to `Degraded`.

## Error Contract
- Unload and timeout failures are explicitly reported; no silent suppression.
- Unload failures do not mutate active store activation state.
- One package unload failure does not block other package processing in the cycle.

## Test Contract
- Must verify deactivation timeout still proceeds to unload attempt.
- Must verify `UnloadPending` transitions retry each cycle until unload success.
- Must verify health reports degraded while any unload-pending package exists.
- Must verify per-package unload failure isolation and correlation-linked diagnostics.
